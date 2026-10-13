// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;

namespace DamianH.Http.ForwardProxy.Internal;

/// <summary>The original <c>Connection</c> options of the current HTTP/1.x request, as received on the wire.</summary>
internal interface IHttp1ConnectionOptionsFeature
{
    /// <summary>
    /// Gets the connection options of the current request other than <c>close</c>, <c>keep-alive</c> and
    /// <c>upgrade</c>. Kestrel replaces a <c>Connection</c> header holding one of those with just that option, which
    /// would otherwise forward the headers the client listed as hop-by-hop.
    /// </summary>
    /// <param name="rawTarget">The current request's raw target, used to confirm the observed head is this request's.</param>
    /// <param name="options">The options, or <see langword="null"/> when there are none.</param>
    /// <returns><see langword="false"/> when the options are unknown.</returns>
    bool TryGetConnectionOptions(string rawTarget, out string[]? options);

    /// <summary>Whether the current request asked to upgrade and observation is paused until its outcome is known.</summary>
    bool IsAwaitingUpgradeOutcome { get; }

    /// <summary>Resumes observation after the current upgrade request was answered without switching protocols.</summary>
    void ResumeAfterRejectedUpgrade();
}

/// <summary>
/// Observes the HTTP/1.x request heads that Kestrel reads from a connection to capture the raw <c>Connection</c>
/// options, following message framing (Content-Length and chunked bodies) across keep-alive and pipelined requests.
/// Bytes pass through unchanged. If the stream can't be followed reliably, observation stops and the options become
/// unknown.
/// </summary>
/// <remarks>
/// A request head becomes current once Kestrel consumes past its end: Kestrel processes HTTP/1.x requests in order
/// and consumes each head immediately before invoking the application for it.
/// </remarks>
internal sealed class Http1RequestHeadObserver(PipeReader inner, int maxLineLength) : PipeReader, IHttp1ConnectionOptionsFeature
{
    private const int MaxPendingHeads = 32;
    private const int MaxChunkSizeLine = 64;
    private const int LongestTrackedHeaderName = 17; // transfer-encoding

    private readonly Lock _sync = new();
    private readonly Queue<Head> _pending = new();
    private ReadOnlySequence<byte> _buffer;
    private long _consumed;
    private long _observed;
    private State _state;
    private volatile bool _disabled;
    private long _remaining;

    private byte[]? _line;
    private int _lineLength;
    private long _lineTotal;
    private bool _skipLine;
    private bool _headerDecided;

    private byte[]? _target;
    private int _targetLength;
    private bool _isConnect;
    private bool _upgrade;
    private long? _contentLength;
    private bool _hasTransferEncoding;
    private bool _chunked;
    private List<string>? _options;
    private long _pausedHeadEnd = -1;

    private bool _currentKnown;
    private long _currentEnd = -1;
    private string[]? _currentOptions;
    private string? _currentTarget;

    private enum State
    {
        RequestLine,
        HeaderLine,
        FixedBody,
        ChunkSize,
        ChunkData,
        ChunkDataEnd,
        Trailer,
        Paused,
        Disabled,
    }

    private readonly record struct Head(long End, string[]? Options, string? Target);

    public bool IsAwaitingUpgradeOutcome
    {
        get
        {
            lock (_sync)
            {
                return _state == State.Paused && _currentKnown && _currentEnd == _pausedHeadEnd;
            }
        }
    }

    public bool TryGetConnectionOptions(string rawTarget, out string[]? options)
    {
        lock (_sync)
        {
            options = _currentOptions;
            if (_currentKnown && (options is null || string.Equals(_currentTarget, rawTarget, StringComparison.Ordinal)))
            {
                return true;
            }

            options = null;
            return false;
        }
    }

    public void ResumeAfterRejectedUpgrade()
    {
        lock (_sync)
        {
            if (_state == State.Paused && _currentEnd == _pausedHeadEnd)
            {
                _state = State.RequestLine;
                _pausedHeadEnd = -1;
            }
        }
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_disabled)
        {
            return inner.ReadAsync(cancellationToken);
        }

        var read = inner.ReadAsync(cancellationToken);
        if (read.IsCompletedSuccessfully)
        {
            var result = read.Result;
            Observe(result.Buffer);
            return new(result);
        }

        return ReadSlowAsync(read);
    }

    public override bool TryRead(out ReadResult result)
    {
        if (!inner.TryRead(out result))
        {
            return false;
        }

        Observe(result.Buffer);
        return true;
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        lock (_sync)
        {
            if (_state == State.Disabled)
            {
                inner.AdvanceTo(consumed, examined);
                return;
            }

            var length = _buffer.Slice(0, consumed).Length;
            _buffer = default;
            inner.AdvanceTo(consumed, examined);
            _consumed += length;
            while (_pending.TryPeek(out var head) && head.End <= _consumed)
            {
                _pending.Dequeue();
                _currentKnown = true;
                _currentEnd = head.End;
                _currentOptions = head.Options;
                _currentTarget = head.Target;
            }

            if (_consumed > _observed)
            {
                // Bytes were consumed without being observed (for example an upgraded connection): stop.
                Disable();
            }
        }
    }

    public override void CancelPendingRead() => inner.CancelPendingRead();

    public override void Complete(Exception? exception = null)
    {
        lock (_sync)
        {
            Disable();
        }

        inner.Complete(exception);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ReadResult> ReadSlowAsync(ValueTask<ReadResult> read)
    {
        var result = await read.ConfigureAwait(false);
        Observe(result.Buffer);
        return result;
    }

    private void Observe(ReadOnlySequence<byte> buffer)
    {
        lock (_sync)
        {
            _buffer = buffer;
            if (_state is State.Disabled or State.Paused)
            {
                return;
            }

            var start = _observed - _consumed;
            if (start < 0)
            {
                Disable();
                return;
            }

            if (start >= buffer.Length)
            {
                return;
            }

            foreach (var segment in buffer.Slice(start))
            {
                var span = segment.Span;
                var processed = Process(span);
                _observed += processed;
                if (processed < span.Length)
                {
                    return;
                }
            }
        }
    }

    /// <returns>The number of bytes processed; fewer than the span when observation stops or must wait.</returns>
    private int Process(ReadOnlySpan<byte> span)
    {
        var i = 0;
        while (i < span.Length)
        {
            switch (_state)
            {
                case State.FixedBody:
                case State.ChunkData:
                {
                    var take = (int)Math.Min(_remaining, span.Length - i);
                    i += take;
                    _remaining -= take;
                    if (_remaining == 0)
                    {
                        _state = _state == State.FixedBody ? State.RequestLine : State.ChunkDataEnd;
                    }

                    break;
                }

                case State.Paused:
                case State.Disabled:
                    return i;

                default:
                {
                    if (_state == State.RequestLine && _lineTotal == 0 && _pending.Count >= MaxPendingHeads)
                    {
                        // Wait for Kestrel to consume earlier heads before observing more.
                        return i;
                    }

                    var rest = span[i..];
                    var newLine = rest.IndexOf((byte)'\n');
                    if (!AppendLine(newLine < 0 ? rest : rest[..newLine]))
                    {
                        Disable();
                        return i;
                    }

                    if (newLine < 0)
                    {
                        return span.Length;
                    }

                    i += newLine + 1;
                    if (!CompleteLine(_observed + i))
                    {
                        Disable();
                        return i;
                    }

                    break;
                }
            }
        }

        return i;
    }

    private bool AppendLine(ReadOnlySpan<byte> part)
    {
        _lineTotal += part.Length;
        if (_skipLine || part.IsEmpty)
        {
            return true;
        }

        if (_state is State.Trailer or State.ChunkDataEnd)
        {
            // Only emptiness matters; keep the first byte to recognise a lone CR.
            if (_lineLength == 0)
            {
                Buffer(part[..1]);
            }

            _skipLine = _lineTotal > 1;
            return true;
        }

        if (_state == State.ChunkSize)
        {
            // The size precedes any extension; the remainder only needs skipping.
            var room = MaxChunkSizeLine - _lineLength;
            Buffer(part[..Math.Min(room, part.Length)]);
            _skipLine = _lineLength >= MaxChunkSizeLine;
            return true;
        }

        if (_lineLength + part.Length > maxLineLength)
        {
            return false;
        }

        Buffer(part);
        if (_state == State.HeaderLine && !_headerDecided)
        {
            var line = _line.AsSpan(0, _lineLength);
            var colon = line.IndexOf((byte)':');
            if (colon >= 0)
            {
                _headerDecided = true;
                _skipLine = !IsTrackedHeader(line[..colon]);
            }
            else if (_lineLength > LongestTrackedHeaderName)
            {
                _headerDecided = true;
                _skipLine = true;
            }
        }

        return true;
    }

    private void Buffer(ReadOnlySpan<byte> part)
    {
        if (_line is null || _line.Length < _lineLength + part.Length)
        {
            var grown = ArrayPool<byte>.Shared.Rent(Math.Max(256, _lineLength + part.Length));
            if (_line is not null)
            {
                _line.AsSpan(0, _lineLength).CopyTo(grown);
                ArrayPool<byte>.Shared.Return(_line);
            }

            _line = grown;
        }

        part.CopyTo(_line.AsSpan(_lineLength));
        _lineLength += part.Length;
    }

    /// <param name="end">The absolute stream offset just past the line's terminator.</param>
    private bool CompleteLine(long end)
    {
        var line = _line is null ? [] : _line.AsSpan(0, _lineLength);
        if (!line.IsEmpty && line[^1] == '\r' && _lineLength == _lineTotal)
        {
            line = line[..^1];
        }

        var isEmpty = _lineTotal == 0 || (_lineTotal == 1 && _lineLength == 1 && _line![0] == '\r');
        var ok = _state switch
        {
            State.RequestLine => isEmpty || OnRequestLine(line),
            State.HeaderLine => isEmpty ? OnEndOfHead(end) : _skipLine || OnHeader(line),
            State.ChunkSize => OnChunkSize(line),
            State.ChunkDataEnd => isEmpty && SetState(State.ChunkSize),
            State.Trailer => !isEmpty || SetState(State.RequestLine),
            _ => false,
        };

        _lineLength = 0;
        _lineTotal = 0;
        _skipLine = false;
        _headerDecided = false;
        if (_state is State.RequestLine or State.FixedBody or State.ChunkData or State.Paused)
        {
            ReturnLineBuffer();
        }

        return ok;
    }

    private bool SetState(State state)
    {
        _state = state;
        return true;
    }

    private bool OnRequestLine(ReadOnlySpan<byte> line)
    {
        var firstSpace = line.IndexOf((byte)' ');
        if (firstSpace <= 0)
        {
            return false;
        }

        var afterMethod = line[(firstSpace + 1)..];
        var secondSpace = afterMethod.IndexOf((byte)' ');
        if (secondSpace <= 0 || !afterMethod[(secondSpace + 1)..].StartsWith("HTTP/1."u8))
        {
            // Includes the HTTP/2 prior-knowledge preface (PRI * HTTP/2.0).
            return false;
        }

        var target = afterMethod[..secondSpace];
        _target = ArrayPool<byte>.Shared.Rent(target.Length);
        target.CopyTo(_target);
        _targetLength = target.Length;
        _isConnect = line[..firstSpace].SequenceEqual("CONNECT"u8);
        _state = State.HeaderLine;
        return true;
    }

    private bool OnHeader(ReadOnlySpan<byte> line)
    {
        var colon = line.IndexOf((byte)':');
        if (colon <= 0)
        {
            return false;
        }

        var name = line[..colon];
        var value = line[(colon + 1)..].Trim(" \t"u8);
        if (Ascii.EqualsIgnoreCase(name, "Connection"u8))
        {
            foreach (var range in value.Split((byte)','))
            {
                var option = value[range].Trim(" \t"u8);
                if (option.IsEmpty || Ascii.EqualsIgnoreCase(option, "close"u8) || Ascii.EqualsIgnoreCase(option, "keep-alive"u8))
                {
                    continue;
                }

                if (Ascii.EqualsIgnoreCase(option, "upgrade"u8))
                {
                    _upgrade = true;
                    continue;
                }

                (_options ??= []).Add(Encoding.Latin1.GetString(option));
            }

            return true;
        }

        if (Ascii.EqualsIgnoreCase(name, "Content-Length"u8))
        {
            if (_contentLength.HasValue || value.IsEmpty || value.IndexOfAnyExceptInRange((byte)'0', (byte)'9') >= 0 || value.Length > 18)
            {
                return false;
            }

            long length = 0;
            foreach (var digit in value)
            {
                length = (length * 10) + (digit - '0');
            }

            _contentLength = length;
            return true;
        }

        _hasTransferEncoding = true;
        var lastComma = value.LastIndexOf((byte)',');
        var coding = (lastComma < 0 ? value : value[(lastComma + 1)..]).Trim(" \t"u8);
        if (!coding.IsEmpty)
        {
            _chunked = Ascii.EqualsIgnoreCase(coding, "chunked"u8);
        }

        return true;
    }

    private bool OnEndOfHead(long end)
    {
        string[]? options = null;
        string? target = null;
        if (_options is { Count: > 0 })
        {
            options = [.. _options];
            target = Encoding.UTF8.GetString(_target.AsSpan(0, _targetLength));
            _options.Clear();
        }

        _pending.Enqueue(new Head(end, options, target));
        var ok = true;
        if (_isConnect)
        {
            // Only reachable mid-connection; the proxy answers it and closes the connection.
            ok = false;
        }
        else if (_upgrade && _contentLength.GetValueOrDefault() == 0 && !_hasTransferEncoding)
        {
            // Kestrel treats the rest of the connection as the upgrade body until the request completes.
            _state = State.Paused;
            _pausedHeadEnd = end;
        }
        else if (_hasTransferEncoding)
        {
            ok = _chunked;
            _state = State.ChunkSize;
        }
        else if (_contentLength > 0)
        {
            _remaining = _contentLength.Value;
            _state = State.FixedBody;
        }
        else
        {
            _state = State.RequestLine;
        }

        ResetHead();
        return ok;
    }

    private bool OnChunkSize(ReadOnlySpan<byte> line)
    {
        var end = line.IndexOfAny(";\t \r"u8);
        var digits = end < 0 ? line : line[..end];
        if (digits.IsEmpty || digits.Length > 15 || (end < 0 && _skipLine))
        {
            return false;
        }

        long size = 0;
        foreach (var digit in digits)
        {
            var value = HexConverter(digit);
            if (value < 0)
            {
                return false;
            }

            size = (size << 4) | (uint)value;
        }

        if (size == 0)
        {
            _state = State.Trailer;
        }
        else
        {
            _remaining = size;
            _state = State.ChunkData;
        }

        return true;
    }

    private static int HexConverter(byte digit) => digit switch
    {
        >= (byte)'0' and <= (byte)'9' => digit - '0',
        >= (byte)'a' and <= (byte)'f' => digit - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => digit - 'A' + 10,
        _ => -1,
    };

    private static bool IsTrackedHeader(ReadOnlySpan<byte> name) =>
        Ascii.EqualsIgnoreCase(name, "Connection"u8)
            || Ascii.EqualsIgnoreCase(name, "Content-Length"u8)
            || Ascii.EqualsIgnoreCase(name, "Transfer-Encoding"u8);

    private void ResetHead()
    {
        if (_target is not null)
        {
            ArrayPool<byte>.Shared.Return(_target);
            _target = null;
        }

        _targetLength = 0;
        _isConnect = false;
        _upgrade = false;
        _contentLength = null;
        _hasTransferEncoding = false;
        _chunked = false;
    }

    private void ReturnLineBuffer()
    {
        if (_line is not null)
        {
            ArrayPool<byte>.Shared.Return(_line);
            _line = null;
        }
    }

    private void Disable()
    {
        _state = State.Disabled;
        _disabled = true;
        _currentKnown = false;
        _currentOptions = null;
        _currentTarget = null;
        _pending.Clear();
        _buffer = default;
        ReturnLineBuffer();
        ResetHead();
    }
}
