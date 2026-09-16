// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;

namespace DamianH.HttpHybridCacheHandler;

internal sealed class StreamingCacheContent : CompatibleHttpContent
{
    private readonly HttpContent _origin;
    private readonly long? _length;
    private readonly HttpHybridCacheHandlerOptions _options;
    private readonly Ct _requestCancellation;
    private readonly Func<AdaptiveSpool, Ct, Task> _complete;
    private readonly Action<Exception> _log;
    private readonly Action _sizeExceeded;
    private Action? _releaseFill;
    private TeeStream? _tee;

    public StreamingCacheContent(HttpContent origin, long? length, HttpHybridCacheHandlerOptions options,
        Ct requestCancellation, Func<AdaptiveSpool, Ct, Task> complete, Action<Exception> log, Action releaseFill,
        Action sizeExceeded)
    {
        _origin = origin;
        _length = length;
        _options = options;
        _requestCancellation = requestCancellation;
        _complete = complete;
        _log = log;
        _sizeExceeded = sizeExceeded;
        _releaseFill = releaseFill;
        foreach (var header in origin.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length ?? 0;
        return _length.HasValue;
    }

    protected override Stream CreateContentReadStream(Ct ct) =>
        _tee ??= CreateTee(_origin.ReadAsStream(ct));
    protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(Ct.None);
    protected override async Task<Stream> CreateContentReadStreamAsync(Ct ct) =>
        _tee ??= CreateTee(await _origin.ReadAsStreamAsync(ct));
    private TeeStream CreateTee(Stream stream) =>
        new(stream, _length, _options, _requestCancellation, _complete, _log, _origin.Dispose, ReleaseFill, _sizeExceeded);

    private void ReleaseFill() => Interlocked.Exchange(ref _releaseFill, null)?.Invoke();

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, Ct.None);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, Ct ct)
    {
        var tee = await CreateContentReadStreamAsync(ct);
        try
        {
            await tee.CopyToAsync(stream, 64 * 1024, ct);
        }
        catch
        {
            tee.Dispose();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _tee?.Dispose();
            }
            finally
            {
                try
                {
                    _origin.Dispose();
                }
                finally
                {
                    ReleaseFill();
                }
            }
        }
        base.Dispose(disposing);
    }

    private sealed class TeeStream(Stream origin, long? expectedLength, HttpHybridCacheHandlerOptions options,
        Ct requestCancellation, Func<AdaptiveSpool, Ct, Task> complete, Action<Exception> log,
        Action disposeOrigin, Action releaseFill, Action sizeExceeded) : CompatibleStream
    {
        private AdaptiveSpool? _spool = new(options, log);
        private long _read;
        private bool _eof;
        private bool _disposed;
        private bool _sizeExceeded;
        private readonly CancellationTokenSource _lifetime = new();
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin seekOrigin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            Task.Run(() => ReadAsync(buffer.AsMemory(offset, count)).AsTask()).GetAwaiter().GetResult();
        public override int Read(Span<byte> buffer)
        {
            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length, 64 * 1024));
            try
            {
                var count = Read(rented, 0, Math.Min(buffer.Length, rented.Length));
                rented.AsSpan(0, count).CopyTo(buffer);
                return count;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, Ct ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, Ct ct = default)
        {
            Guard.NotDisposed(_disposed, this);
            if (buffer.IsEmpty || _eof)
            {
                return 0;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, requestCancellation, _lifetime.Token);
            ct = linked.Token;
            try
            {
                ct.ThrowIfCancellationRequested();
                var count = await origin.ReadAsync(buffer, ct);
                if (count == 0)
                {
                    _eof = true;
                    if (expectedLength.HasValue && expectedLength != _read)
                    {
                        Abandon();
                        throw new IOException("The origin response length does not match its declared Content-Length.");
                    }
                }
                else
                {
                    _read = checked(_read + count);
                    if (expectedLength.HasValue && _read > expectedLength)
                    {
                        throw new IOException("The origin response length exceeds its declared Content-Length.");
                    }
                    if (expectedLength == _read)
                    {
                        // Length-limited consumers may never ask for another read. Verify
                        // EOF and publish before giving them the final declared bytes.
                        if (await origin.ReadAsync(new byte[1], ct) != 0)
                        {
                            throw new IOException("The origin response length exceeds its declared Content-Length.");
                        }
                        _eof = true;
                    }
                }

                if (!_sizeExceeded && _read > options.MaxCacheableContentSize)
                {
                    _sizeExceeded = true;
                    sizeExceeded();
                    Abandon();
                }

                if (_spool != null)
                {
                    try
                    {
                        if (count != 0)
                        {
                            await _spool.WriteAsync(buffer[..count], ct);
                        }
                        if (_eof)
                        {
                            await complete(_spool, ct);
                            Abandon();
                        }
                    }
                    catch (Exception ex) when (HttpHybridCacheHandler.IsExpectedCacheFailure(ex))
                    {
                        log(ex);
                        Abandon();
                    }
                }
                return count;
            }
            catch
            {
                Abandon();
                throw;
            }
        }

        private void Abandon()
        {
            try
            {
                _spool?.Dispose();
            }
            finally
            {
                _spool = null;
                releaseFill();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _lifetime.Cancel();
                Abandon();
                disposeOrigin();
                _lifetime.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
