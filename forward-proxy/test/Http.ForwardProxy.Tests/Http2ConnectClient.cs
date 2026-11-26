// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

namespace DamianH.Http.ForwardProxy;

/// <summary>
/// A minimal, single-stream HTTP/2 client for classic <c>CONNECT</c> (RFC 9113 §8.5), which
/// <see cref="System.Net.Http.HttpClient"/> cannot send because it always adds <c>:scheme</c> and <c>:path</c>.
/// It implements only what the forward-proxy tests need: the connection preface and SETTINGS exchange, PING,
/// flow control, HPACK decoding without Huffman strings, DATA, and RST_STREAM.
/// </summary>
internal sealed class Http2ConnectClient : IAsyncDisposable
{
    public const int CancelError = 0x8;
    public const int ConnectError = 0xa;

    private const byte DataFrame = 0x0;
    private const byte HeadersFrame = 0x1;
    private const byte RstStreamFrame = 0x3;
    private const byte SettingsFrame = 0x4;
    private const byte PingFrame = 0x6;
    private const byte GoAwayFrame = 0x7;
    private const byte WindowUpdateFrame = 0x8;
    private const byte ContinuationFrame = 0x9;
    private const byte EndStreamFlag = 0x1;
    private const byte AckFlag = 0x1;
    private const byte EndHeadersFlag = 0x4;
    private const byte PaddedFlag = 0x8;
    private const byte PriorityFlag = 0x20;
    private const int StreamId = 1;
    private const int DefaultWindow = 65535;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient _tcp;
    private readonly SslStream _tls;
    private readonly CancellationTokenSource _closed = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _windowLock = new();
    private readonly Channel<byte[]> _data = Channel.CreateUnbounded<byte[]>();
    private readonly TaskCompletionSource<IReadOnlyList<KeyValuePair<string, string>>> _headers = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _reset = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HpackDecoder _decoder = new();
    private TaskCompletionSource _windowChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _connectionSendWindow = DefaultWindow;
    private long _streamSendWindow = DefaultWindow;
    private long _initialStreamWindow = DefaultWindow;
    private int _maxFrameSize = 16384;
    private ReadOnlyMemory<byte> _pending;
    private Task _readLoop = Task.CompletedTask;

    private Http2ConnectClient(TcpClient tcp, SslStream tls)
    {
        _tcp = tcp;
        _tls = tls;
        Tunnel = new TunnelStream(this);
    }

    /// <summary>The response status of the <c>CONNECT</c> request.</summary>
    public int StatusCode { get; private set; }

    /// <summary>The non-pseudo response header fields, with lower-case names.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; private set; } = [];

    /// <summary>The tunnel: writes become DATA frames and reads return the peer's DATA payloads.</summary>
    public Stream Tunnel { get; }

    /// <summary>Completes with the error code of a RST_STREAM received for the tunnel stream.</summary>
    public Task<int> Reset => _reset.Task;

    /// <summary>
    /// Connects to an HTTP/2 TLS proxy listener, sends <c>CONNECT authority</c>, and waits for the response headers.
    /// The proxy's certificate is not validated.
    /// </summary>
    public static async Task<Http2ConnectClient> ConnectAsync(int proxyPort, string authority, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var tcp = new TcpClient();
        SslStream? tls = null;
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, proxyPort, timeout.Token);
            tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = [SslApplicationProtocol.Http2],
            }, timeout.Token);
            if (tls.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
            {
                throw new InvalidOperationException("The proxy did not negotiate HTTP/2.");
            }
        }
        catch
        {
            tls?.Dispose();
            tcp.Dispose();
            throw;
        }

        var client = new Http2ConnectClient(tcp, tls);
        try
        {
            await client.StartAsync(authority, timeout.Token);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>Aborts the tunnel stream with RST_STREAM.</summary>
    public Task ResetAsync(int errorCode, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, errorCode);
        return WriteFrameAsync(RstStreamFrame, 0, StreamId, payload, cancellationToken);
    }

    /// <summary>Returns the value of a response header field, or <see langword="null"/>.</summary>
    public string? GetHeader(string name) =>
        Headers.Where(h => h.Key == name).Select(h => h.Value).FirstOrDefault();

    /// <summary>Creates an <see cref="HttpClient"/> whose single connection is this tunnel.</summary>
    public HttpClient CreateHttpClient(Func<X509Certificate?, SslPolicyErrors, bool> validateCertificate)
    {
        var used = 0;
        return new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = (_, _) => Interlocked.Exchange(ref used, 1) == 0
                ? ValueTask.FromResult(Tunnel)
                : throw new InvalidOperationException("The tunnel supports only one connection."),
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, errors) => validateCertificate(certificate, errors) },
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _closed.CancelAsync();
        _tls.Dispose();
        _tcp.Dispose();
        try
        {
            await _readLoop.WaitAsync(Timeout);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
        }

        _closed.Dispose();
        _writeLock.Dispose();
    }

    private async Task StartAsync(string authority, CancellationToken cancellationToken)
    {
        await _tls.WriteAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray(), cancellationToken);
        var settings = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(settings, 0x2); // SETTINGS_ENABLE_PUSH
        await WriteFrameAsync(SettingsFrame, 0, 0, settings, cancellationToken);

        // Classic CONNECT carries only :method and :authority (RFC 9113 §8.5).
        using var block = new MemoryStream();
        HpackDecoder.WriteLiteralWithIndexedName(block, 2, "CONNECT");
        HpackDecoder.WriteLiteralWithIndexedName(block, 1, authority);
        _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
        await WriteFrameAsync(HeadersFrame, EndHeadersFlag, StreamId, block.ToArray(), cancellationToken);

        var headers = await _headers.Task.WaitAsync(cancellationToken);
        var status = headers.FirstOrDefault(h => h.Key == ":status").Value
            ?? throw new InvalidOperationException("The CONNECT response has no :status.");
        StatusCode = int.Parse(status, System.Globalization.CultureInfo.InvariantCulture);
        Headers = headers.Where(h => !h.Key.StartsWith(':')).ToArray();
    }

    private async Task ReadLoopAsync()
    {
        var token = _closed.Token;
        MemoryStream? headerBlock = null;
        var headerEndStream = false;
        try
        {
            while (true)
            {
                var header = new byte[9];
                await _tls.ReadExactlyAsync(header, token);
                var length = (header[0] << 16) | (header[1] << 8) | header[2];
                var type = header[3];
                var flags = header[4];
                var streamId = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5)) & 0x7fffffff;
                var payload = new byte[length];
                await _tls.ReadExactlyAsync(payload, token);

                if (headerBlock is not null && type != ContinuationFrame)
                {
                    throw new InvalidDataException("Expected a CONTINUATION frame.");
                }

                switch (type)
                {
                    case SettingsFrame when (flags & AckFlag) == 0:
                        ApplySettings(payload);
                        await WriteFrameAsync(SettingsFrame, AckFlag, 0, ReadOnlyMemory<byte>.Empty, token);
                        break;
                    case PingFrame when (flags & AckFlag) == 0:
                        await WriteFrameAsync(PingFrame, AckFlag, 0, payload, token);
                        break;
                    case WindowUpdateFrame:
                        AddSendWindow(streamId, BinaryPrimitives.ReadInt32BigEndian(payload) & 0x7fffffff);
                        break;
                    case GoAwayFrame:
                        throw new IOException($"The proxy sent GOAWAY with error {BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(4))}.");
                    case HeadersFrame when streamId == StreamId:
                        headerBlock = new MemoryStream();
                        headerEndStream = (flags & EndStreamFlag) != 0;
                        headerBlock.Write(GetHeaderBlockFragment(payload, flags).Span);
                        if ((flags & EndHeadersFlag) != 0)
                        {
                            CompleteHeaders(headerBlock, headerEndStream);
                            headerBlock = null;
                        }

                        break;
                    case ContinuationFrame when headerBlock is not null && streamId == StreamId:
                        headerBlock.Write(payload);
                        if ((flags & EndHeadersFlag) != 0)
                        {
                            CompleteHeaders(headerBlock, headerEndStream);
                            headerBlock = null;
                        }

                        break;
                    case DataFrame when streamId == StreamId:
                        var data = StripPadding(payload, flags);
                        if (data.Length > 0)
                        {
                            await _data.Writer.WriteAsync(data.ToArray(), token);
                        }

                        if (length > 0)
                        {
                            await SendWindowUpdatesAsync(length, (flags & EndStreamFlag) == 0, token);
                        }

                        if ((flags & EndStreamFlag) != 0)
                        {
                            _data.Writer.TryComplete();
                        }

                        break;
                    case RstStreamFrame when streamId == StreamId:
                        var error = BinaryPrimitives.ReadInt32BigEndian(payload);
                        _reset.TrySetResult(error);
                        Fail(new IOException($"The proxy reset the tunnel stream with error {error}."));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
            if (!token.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    private void CompleteHeaders(MemoryStream block, bool endStream)
    {
        var fields = _decoder.Decode(block.ToArray());
        if (!_headers.TrySetResult(fields) && !endStream)
        {
            throw new InvalidDataException("Unexpected header block on the tunnel stream.");
        }

        if (endStream)
        {
            _data.Writer.TryComplete();
        }
    }

    private void Fail(Exception ex)
    {
        _headers.TrySetException(ex);
        _reset.TrySetException(ex);
        _ = _reset.Task.Exception;
        _data.Writer.TryComplete(ex);
        lock (_windowLock)
        {
            _windowChanged.TrySetException(ex);
            _ = _windowChanged.Task.Exception;
        }
    }

    private void ApplySettings(byte[] payload)
    {
        for (var i = 0; i + 6 <= payload.Length; i += 6)
        {
            var id = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(i));
            var value = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(i + 2));
            if (id == 0x4)
            {
                lock (_windowLock)
                {
                    // Only stream 1 is open, so the initial window delta applies to it alone.
                    _streamSendWindow += (long)value - _initialStreamWindow;
                    _initialStreamWindow = value;
                    SignalWindow();
                }
            }
            else if (id == 0x5)
            {
                _maxFrameSize = (int)value;
            }
        }
    }

    private void AddSendWindow(int streamId, int increment)
    {
        lock (_windowLock)
        {
            if (streamId == 0)
            {
                _connectionSendWindow += increment;
            }
            else if (streamId == StreamId)
            {
                _streamSendWindow += increment;
            }

            SignalWindow();
        }
    }

    private void SignalWindow()
    {
        _windowChanged.TrySetResult();
        _windowChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async Task SendWindowUpdatesAsync(int length, bool streamOpen, CancellationToken cancellationToken)
    {
        var increment = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(increment, length);
        await WriteFrameAsync(WindowUpdateFrame, 0, 0, increment, cancellationToken);
        if (streamOpen)
        {
            await WriteFrameAsync(WindowUpdateFrame, 0, StreamId, increment, cancellationToken);
        }
    }

    private async Task WriteDataAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        while (!buffer.IsEmpty)
        {
            int count;
            Task windowChanged;
            lock (_windowLock)
            {
                count = (int)Math.Min(Math.Min(_connectionSendWindow, _streamSendWindow), Math.Min(buffer.Length, _maxFrameSize));
                windowChanged = _windowChanged.Task;
                if (count > 0)
                {
                    _connectionSendWindow -= count;
                    _streamSendWindow -= count;
                }
            }

            if (count <= 0)
            {
                await windowChanged.WaitAsync(Timeout, linked.Token);
                continue;
            }

            await WriteFrameAsync(DataFrame, 0, StreamId, buffer[..count], linked.Token);
            buffer = buffer[count..];
        }
    }

    private async Task<int> ReadDataAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_pending.IsEmpty)
        {
            try
            {
                if (!await _data.Reader.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }
            }
            catch (ChannelClosedException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }

            _pending = _data.Reader.TryRead(out var next) ? next : ReadOnlyMemory<byte>.Empty;
        }

        var count = Math.Min(buffer.Length, _pending.Length);
        _pending[..count].CopyTo(buffer);
        _pending = _pending[count..];
        return count;
    }

    private async Task WriteFrameAsync(byte type, byte flags, int streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var frame = new byte[9 + payload.Length];
        frame[0] = (byte)(payload.Length >> 16);
        frame[1] = (byte)(payload.Length >> 8);
        frame[2] = (byte)payload.Length;
        frame[3] = type;
        frame[4] = flags;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5), streamId);
        payload.CopyTo(frame.AsMemory(9));
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _tls.WriteAsync(frame, cancellationToken);
            await _tls.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static ReadOnlyMemory<byte> GetHeaderBlockFragment(byte[] payload, byte flags)
    {
        var fragment = StripPadding(payload, flags);
        return (flags & PriorityFlag) != 0 ? fragment[5..] : fragment;
    }

    private static ReadOnlyMemory<byte> StripPadding(byte[] payload, byte flags)
    {
        if ((flags & PaddedFlag) == 0)
        {
            return payload;
        }

        var padding = payload[0];
        return payload.AsMemory(1, payload.Length - 1 - padding);
    }

    private sealed class TunnelStream(Http2ConnectClient client) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(client.ReadDataAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            client.ReadDataAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            client.ReadDataAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(client.WriteDataAsync(buffer, cancellationToken));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            client.WriteDataAsync(buffer.AsMemory(offset, count), cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            client.WriteDataAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>An HPACK (RFC 7541) decoder with a dynamic table but no Huffman support.</summary>
    private sealed class HpackDecoder
    {
        private static readonly (string Name, string Value)[] StaticTable =
        [
            (":authority", ""), (":method", "GET"), (":method", "POST"), (":path", "/"), (":path", "/index.html"),
            (":scheme", "http"), (":scheme", "https"), (":status", "200"), (":status", "204"), (":status", "206"),
            (":status", "304"), (":status", "400"), (":status", "404"), (":status", "500"), ("accept-charset", ""),
            ("accept-encoding", "gzip, deflate"), ("accept-language", ""), ("accept-ranges", ""), ("accept", ""),
            ("access-control-allow-origin", ""), ("age", ""), ("allow", ""), ("authorization", ""), ("cache-control", ""),
            ("content-disposition", ""), ("content-encoding", ""), ("content-language", ""), ("content-length", ""),
            ("content-location", ""), ("content-range", ""), ("content-type", ""), ("cookie", ""), ("date", ""),
            ("etag", ""), ("expect", ""), ("expires", ""), ("from", ""), ("host", ""), ("if-match", ""),
            ("if-modified-since", ""), ("if-none-match", ""), ("if-range", ""), ("if-unmodified-since", ""),
            ("last-modified", ""), ("link", ""), ("location", ""), ("max-forwards", ""), ("proxy-authenticate", ""),
            ("proxy-authorization", ""), ("range", ""), ("referer", ""), ("refresh", ""), ("retry-after", ""),
            ("server", ""), ("set-cookie", ""), ("strict-transport-security", ""), ("transfer-encoding", ""),
            ("user-agent", ""), ("vary", ""), ("via", ""), ("www-authenticate", ""),
        ];

        private const int MaxTableSize = 4096;

        private readonly List<(string Name, string Value)> _dynamic = [];
        private int _tableSize;
        private int _maxSize = MaxTableSize;

        public static void WriteLiteralWithIndexedName(Stream stream, int nameIndex, string value)
        {
            WriteInteger(stream, 0x00, 4, nameIndex);
            var bytes = Encoding.ASCII.GetBytes(value);
            WriteInteger(stream, 0x00, 7, bytes.Length);
            stream.Write(bytes);
        }

        public IReadOnlyList<KeyValuePair<string, string>> Decode(ReadOnlySpan<byte> block)
        {
            var fields = new List<KeyValuePair<string, string>>();
            var position = 0;
            while (position < block.Length)
            {
                var first = block[position];
                if ((first & 0x80) != 0)
                {
                    var (name, value) = Lookup(ReadInteger(block, ref position, 7));
                    fields.Add(new(name, value));
                }
                else if ((first & 0x40) != 0)
                {
                    var field = ReadLiteral(block, ref position, 6);
                    fields.Add(new(field.Name, field.Value));
                    Add(field);
                }
                else if ((first & 0x20) != 0)
                {
                    var size = ReadInteger(block, ref position, 5);
                    if (size > MaxTableSize)
                    {
                        throw new InvalidDataException("HPACK table size update exceeds the advertised limit.");
                    }

                    _maxSize = size;
                    Evict();
                }
                else
                {
                    var field = ReadLiteral(block, ref position, 4);
                    fields.Add(new(field.Name, field.Value));
                }
            }

            return fields;
        }

        private (string Name, string Value) ReadLiteral(ReadOnlySpan<byte> block, ref int position, int prefix)
        {
            var index = ReadInteger(block, ref position, prefix);
            var name = index == 0 ? ReadString(block, ref position) : Lookup(index).Name;
            return (name, ReadString(block, ref position));
        }

        private (string Name, string Value) Lookup(int index)
        {
            if (index >= 1 && index <= StaticTable.Length)
            {
                return StaticTable[index - 1];
            }

            var dynamicIndex = index - StaticTable.Length - 1;
            if (dynamicIndex < 0 || dynamicIndex >= _dynamic.Count)
            {
                throw new InvalidDataException($"Invalid HPACK index {index}.");
            }

            return _dynamic[dynamicIndex];
        }

        private void Add((string Name, string Value) field)
        {
            _dynamic.Insert(0, field);
            _tableSize += EntrySize(field);
            Evict();
        }

        private void Evict()
        {
            while (_tableSize > _maxSize)
            {
                _tableSize -= EntrySize(_dynamic[^1]);
                _dynamic.RemoveAt(_dynamic.Count - 1);
            }
        }

        private static int EntrySize((string Name, string Value) field) => 32 + field.Name.Length + field.Value.Length;

        private static string ReadString(ReadOnlySpan<byte> block, ref int position)
        {
            if ((block[position] & 0x80) != 0)
            {
                throw new NotSupportedException("Huffman-encoded HPACK strings are not supported by this test client.");
            }

            var length = ReadInteger(block, ref position, 7);
            var value = Encoding.Latin1.GetString(block.Slice(position, length));
            position += length;
            return value;
        }

        private static int ReadInteger(ReadOnlySpan<byte> block, ref int position, int prefix)
        {
            var mask = (1 << prefix) - 1;
            var value = block[position++] & mask;
            if (value < mask)
            {
                return value;
            }

            for (var shift = 0; ; shift += 7)
            {
                if (shift > 21)
                {
                    throw new InvalidDataException("HPACK integer is too large.");
                }

                var b = block[position++];
                value += (b & 0x7f) << shift;
                if ((b & 0x80) == 0)
                {
                    return value;
                }
            }
        }

        private static void WriteInteger(Stream stream, byte pattern, int prefix, int value)
        {
            var mask = (1 << prefix) - 1;
            if (value < mask)
            {
                stream.WriteByte((byte)(pattern | value));
                return;
            }

            stream.WriteByte((byte)(pattern | mask));
            value -= mask;
            while (value >= 0x80)
            {
                stream.WriteByte((byte)((value & 0x7f) | 0x80));
                value >>= 7;
            }

            stream.WriteByte((byte)value);
        }
    }
}
