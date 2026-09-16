// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Conformance;

internal static class WireProtocol
{
    internal const byte Request = 1;
    internal const byte Response = 2;
    internal const byte Hello = 3;
    internal const byte OriginError = 4;
    internal const int MaxFrame = 64 * 1024 * 1024;
    internal const int MaxBody = 60 * 1024 * 1024;
    private const int MaxString = 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static async Task WriteFrame(Stream stream, byte[] frame, CancellationToken token)
    {
        if (frame.Length is < 1 or > MaxFrame) throw new InvalidDataException("Invalid frame length.");
        var prefix = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(frame.Length));
        await stream.WriteAsync(prefix, 0, prefix.Length, token).ConfigureAwait(false);
        await stream.WriteAsync(frame, 0, frame.Length, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadFrame(Stream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await ReadExactly(stream, prefix, token).ConfigureAwait(false);
        var size = System.Net.IPAddress.NetworkToHostOrder(BitConverter.ToInt32(prefix, 0));
        if (size is < 1 or > MaxFrame) throw new InvalidDataException("Invalid frame length.");
        var frame = new byte[size];
        await ReadExactly(stream, frame, token).ConfigureAwait(false);
        return frame;
    }

    private static async Task ReadExactly(Stream stream, byte[] bytes, CancellationToken token)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Truncated conformance IPC frame.");
            offset += read;
        }
    }

    internal static byte[] EncodeHello(string description) => Encode(Hello, writer => WriteString(writer, description));
    internal static string DecodeHello(byte[] frame) => Decode(frame, Hello, ReadString);
    internal static byte[] EncodeOriginError(string error) => Encode(OriginError, writer => WriteString(writer, error));
    internal static string DecodeOriginError(byte[] frame) => Decode(frame, OriginError, ReadString);

    internal static async Task<byte[]> EncodeRequest(HttpRequestMessage request, CancellationToken token)
    {
        var body = await ReadBody(request.Content, token).ConfigureAwait(false);
        return Encode(Request, writer =>
        {
            WriteString(writer, request.Method.Method);
            WriteString(writer, request.RequestUri!.AbsoluteUri);
            WriteString(writer, request.Version.ToString());
            WriteHeaders(writer, request.Headers);
            WriteContent(writer, request.Content, body);
        });
    }

    internal static HttpRequestMessage DecodeRequest(byte[] frame) => Decode(frame, Request, reader =>
    {
        var request = new HttpRequestMessage(new HttpMethod(ReadString(reader)), new Uri(ReadString(reader), UriKind.Absolute));
        try
        {
            request.Version = Version.Parse(ReadString(reader));
            ReadHeaders(reader, request.Headers);
            request.Content = ReadContent(reader);
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    });

    internal static async Task<byte[]> EncodeResponse(HttpResponseMessage response, CancellationToken token)
    {
        var body = await ReadBody(response.Content, token).ConfigureAwait(false);
        return Encode(Response, writer =>
        {
            writer.Write((int)response.StatusCode);
            WriteString(writer, response.ReasonPhrase ?? "");
            WriteString(writer, response.Version.ToString());
            WriteHeaders(writer, response.Headers);
            WriteContent(writer, response.Content, body);
        });
    }

    internal static HttpResponseMessage DecodeResponse(byte[] frame) => Decode(frame, Response, reader =>
    {
        var status = reader.ReadInt32();
        if (status is < 100 or > 599) throw new InvalidDataException("Invalid HTTP response status.");
        var response = new HttpResponseMessage((System.Net.HttpStatusCode)status);
        try
        {
            response.ReasonPhrase = ReadString(reader);
            response.Version = Version.Parse(ReadString(reader));
            ReadHeaders(reader, response.Headers);
            response.Content = ReadContent(reader);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    });

    private static byte[] Encode(byte kind, Action<BinaryWriter> write)
    {
        using var stream = new BoundedWriteStream(MaxFrame, "IPC message exceeds frame limit.");
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(kind);
        write(writer);
        return stream.ToArray();
    }

    private static T Decode<T>(byte[] frame, byte kind, Func<BinaryReader, T> read)
    {
        if (frame.Length is < 1 or > MaxFrame) throw new InvalidDataException("Invalid frame length.");
        using var stream = new MemoryStream(frame, false);
        using var reader = new BinaryReader(stream, Utf8, true);
        if (reader.ReadByte() != kind) throw new InvalidDataException("Unexpected IPC message type.");
        var result = read(reader);
        if (stream.Position != stream.Length)
        {
            (result as IDisposable)?.Dispose();
            throw new InvalidDataException("Unexpected trailing IPC data.");
        }
        return result;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        if (Utf8.GetByteCount(value) > MaxString) throw new InvalidDataException("IPC string exceeds limit.");
        var bytes = Utf8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = ReadCount(reader, MaxString);
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("Truncated IPC string.");
        return Utf8.GetString(bytes);
    }

    private static int ReadCount(BinaryReader reader, int limit)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > limit) throw new InvalidDataException("IPC field exceeds limit.");
        return count;
    }

    private static void WriteHeaders(BinaryWriter writer, HttpHeaders headers)
    {
        var entries = headers.ToArray();
        if (entries.Length > 512) throw new InvalidDataException("Too many headers.");
        writer.Write(entries.Length);
        foreach (var entry in entries)
        {
            WriteString(writer, entry.Key);
            var values = entry.Value.ToArray();
            if (values.Length > 4096) throw new InvalidDataException("Too many header values.");
            writer.Write(values.Length);
            foreach (var value in values) WriteString(writer, value);
        }
    }

    private static void ReadHeaders(BinaryReader reader, HttpHeaders headers)
    {
        var count = ReadCount(reader, 512);
        for (var i = 0; i < count; i++)
        {
            var name = ReadString(reader);
            var values = new string[ReadCount(reader, 4096)];
            for (var j = 0; j < values.Length; j++) values[j] = ReadString(reader);
            if (!headers.TryAddWithoutValidation(name, values))
                throw new InvalidDataException("Invalid or misplaced IPC header: " + name);
        }
    }

    private static async Task<byte[]> ReadBody(HttpContent? content, CancellationToken token)
    {
        if (content is null) return [];
        token.ThrowIfCancellationRequested();
        if (content.Headers.ContentLength > MaxBody) throw new InvalidDataException("IPC body exceeds limit.");
        using var buffer = new BoundedWriteStream(MaxBody, "IPC body exceeds limit.", token);
        using var cancellation = token.Register(content.Dispose);
        // YARP request content is write-only: ReadAsStreamAsync is deliberately unsupported.
        await content.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static void WriteContent(BinaryWriter writer, HttpContent? content, byte[] bytes)
    {
        writer.Write(content is not null);
        if (content is null) return;
        WriteHeaders(writer, content.Headers);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static HttpContent? ReadContent(BinaryReader reader)
    {
        var present = reader.ReadByte();
        if (present == 0) return null;
        if (present != 1) throw new InvalidDataException("Invalid content presence flag.");
        using var headerContent = new ByteArrayContent([]);
        ReadHeaders(reader, headerContent.Headers);
        var length = ReadCount(reader, MaxBody);
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("Truncated IPC body.");
        var content = new BufferedWireContent(bytes);
        foreach (var header in headerContent.Headers)
        {
            if (!content.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                content.Dispose();
                throw new InvalidDataException("Invalid IPC content header: " + header.Key);
            }
        }
        return content;
    }

    private sealed class BufferedWireContent(byte[] bytes) : HttpContent
    {
        // Only an explicitly transported header declares a length; buffering must not invent one.
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(bytes, 0, bytes.Length);
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, false));
    }

    internal sealed class BoundedWriteStream(int limit, string limitMessage, CancellationToken token = default) : Stream
    {
        private readonly MemoryStream _buffer = new();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }
        internal int Capacity => _buffer.Capacity;
        internal byte[] ToArray() => _buffer.ToArray();
        private void EnsureCapacity(int count)
        {
            token.ThrowIfCancellationRequested();
            if (count > limit - _buffer.Length) throw new InvalidDataException(limitMessage);
            var required = (int)_buffer.Length + count;
            if (required > _buffer.Capacity)
            {
                // MemoryStream's geometric growth can exceed the limit even when the write fits.
                _buffer.Capacity = Math.Max(required, (int)Math.Min(limit, Math.Max(256L, 2L * _buffer.Capacity)));
            }
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            _buffer.Write(buffer, offset, count);
        }
        public override void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _buffer.WriteByte(value);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }
        public override void Flush() => token.ThrowIfCancellationRequested();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _buffer.Dispose();
            base.Dispose(disposing);
        }
    }
}
