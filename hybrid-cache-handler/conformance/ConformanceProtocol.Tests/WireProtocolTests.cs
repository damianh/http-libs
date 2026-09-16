// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using Conformance;
using Xunit;

public class WireProtocolTests
{
    [Fact]
    public async Task RequestRetainsMethodFullUriVersionHeadersAndBinaryBody()
    {
        using var request = new HttpRequestMessage(new HttpMethod("PATCH"), "http://127.0.0.1:4567/a%2Fb?q=a%20b&x=2")
        {
            Version = HttpVersion.Version10,
            Content = new ByteArrayContent([0, 1, 255, 13, 10])
        };
        request.Headers.TryAddWithoutValidation("X-Values", ["a,b", "c"]);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
        request.Content.Headers.TryAddWithoutValidation("X-Content", ["one", "two"]);

        using var copy = WireProtocol.DecodeRequest(await WireProtocol.EncodeRequest(request, CancellationToken.None));
        Assert.Equal(request.Method, copy.Method);
        Assert.Equal(request.RequestUri!.AbsoluteUri, copy.RequestUri!.AbsoluteUri);
        Assert.Equal(request.Version, copy.Version);
        Assert.Equal(request.Headers.GetValues("X-Values"), copy.Headers.GetValues("X-Values"));
        Assert.False(copy.Headers.Contains("X-Content"));
        Assert.Equal(request.Content.Headers.GetValues("X-Content"), copy.Content!.Headers.GetValues("X-Content"));
        Assert.Equal(await request.Content.ReadAsByteArrayAsync(), await copy.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ResponseRetainsReasonVersionAndSetCookieValueBoundaries()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation)
        {
            ReasonPhrase = "Custom reason",
            Version = HttpVersion.Version11,
            Content = new ByteArrayContent([0, 128, 255])
        };
        string[] cookies = ["a=1; Expires=Wed, 21 Oct 2037 07:28:00 GMT", "b=2; Path=/"];
        response.Headers.TryAddWithoutValidation("Set-Cookie", cookies);
        response.Headers.TryAddWithoutValidation("X-Values", ["a,b", "c"]);
        response.Content.Headers.TryAddWithoutValidation("Content-Type", "image/png");

        using var copy = WireProtocol.DecodeResponse(await WireProtocol.EncodeResponse(response, CancellationToken.None));
        Assert.Equal(response.StatusCode, copy.StatusCode);
        Assert.Equal(response.ReasonPhrase, copy.ReasonPhrase);
        Assert.Equal(response.Version, copy.Version);
        Assert.Equal(cookies, copy.Headers.GetValues("Set-Cookie"));
        Assert.Equal(response.Headers.GetValues("X-Values"), copy.Headers.GetValues("X-Values"));
        Assert.Equal(response.Content.Headers.ContentType, copy.Content!.Headers.ContentType);
        Assert.Equal(await response.Content.ReadAsByteArrayAsync(), await copy.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task NoRequestContentStaysAbsent()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/");
        using var copy = WireProtocol.DecodeRequest(await WireProtocol.EncodeRequest(request, CancellationToken.None));
        Assert.Null(copy.Content);
    }

    [Fact]
    public async Task HeadResponseRetainsDeclaredLengthWithoutInventingBody()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = 100;
        using var copy = WireProtocol.DecodeResponse(await WireProtocol.EncodeResponse(response, CancellationToken.None));
        Assert.Equal(100, copy.Content!.Headers.ContentLength);
        Assert.Empty(await copy.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task FrameCanBeReadFromFragmentedStream()
    {
        var payload = WireProtocol.EncodeHello("conformance-ipc-v1");
        using var buffer = new MemoryStream();
        await WireProtocol.WriteFrame(buffer, payload, CancellationToken.None);
        using var fragmented = new FragmentedStream(buffer.ToArray());
        Assert.Equal(payload, await WireProtocol.ReadFrame(fragmented, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(WireProtocol.MaxFrame + 1)]
    public async Task RejectsInvalidFrameLengthBeforeAllocating(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(length)));
        await Assert.ThrowsAsync<InvalidDataException>(() => WireProtocol.ReadFrame(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task TruncatedFramesFailInsteadOfReturningPartialMessages(int length)
    {
        using var stream = new MemoryStream(new byte[] { 0, 0, 0, 2, WireProtocol.Hello }.Take(length).ToArray());
        await Assert.ThrowsAsync<EndOfStreamException>(() => WireProtocol.ReadFrame(stream, CancellationToken.None));
    }

    [Fact]
    public void RejectsTrailingDataWrongKindAndOversizedStrings()
    {
        Assert.Throws<InvalidDataException>(() => WireProtocol.DecodeHello([WireProtocol.Request]));
        Assert.Throws<InvalidDataException>(() => WireProtocol.DecodeHello([.. WireProtocol.EncodeHello("hi"), 0]));
        Assert.Throws<InvalidDataException>(() => WireProtocol.DecodeHello([WireProtocol.Hello, 255, 255, 255, 127]));
        Assert.Throws<EndOfStreamException>(() => WireProtocol.DecodeHello([WireProtocol.Hello, 1, 0, 0, 0]));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(WireProtocol.MaxBody)]
    [InlineData(WireProtocol.MaxFrame)]
    public async Task BoundedWritesCapCapacityAndRejectOverflowWithoutChangingBuffer(int limit)
    {
        using var stream = new WireProtocol.BoundedWriteStream(limit, "Limit exceeded.");
        var chunk = new byte[limit / 2 + 1];
        stream.Write(chunk, 0, chunk.Length);
        Assert.InRange(stream.Capacity, chunk.Length, limit);
        await stream.WriteAsync(chunk, 0, limit - chunk.Length, CancellationToken.None);
        Assert.Equal(limit, stream.Length);
        Assert.Equal(limit, stream.Capacity);

        Assert.Throws<InvalidDataException>(() => stream.WriteByte(1));
        Assert.Throws<InvalidDataException>(() => stream.Write(chunk, 0, 1));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await stream.WriteAsync(chunk, 0, 1, CancellationToken.None));
        Assert.Equal(limit, stream.Length);
        Assert.Equal(limit, stream.Capacity);
    }

    [Fact]
    public void OversizedWriteDoesNotAllocateOrPartiallyWrite()
    {
        using var stream = new WireProtocol.BoundedWriteStream(17, "Limit exceeded.");
        Assert.Throws<InvalidDataException>(() => stream.Write(new byte[18], 0, 18));
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, stream.Capacity);
    }

    [Fact]
    public void BinaryWriterChecksScalarAndSpanWritesBeforeGrowingBuffer()
    {
        using var stream = new WireProtocol.BoundedWriteStream(9, "Limit exceeded.");
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        writer.Write(42);
        writer.Write(new byte[] { 2, 3, 4 }.AsSpan());
        Assert.Equal(8, stream.Length);
        Assert.Throws<InvalidDataException>(() => writer.Write(42));
        Assert.Equal(8, stream.Length);
        writer.Write((byte)5);
        Assert.Throws<InvalidDataException>(() => writer.Write((byte)6));
        Assert.Throws<InvalidDataException>(() => writer.Write(new byte[] { 7 }.AsSpan()));
        Assert.Equal(9, stream.Length);
        Assert.Equal(9, stream.Capacity);
        Assert.Equal(new byte[] { 1, 42, 0, 0, 0, 2, 3, 4, 5 }, stream.ToArray());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, WireProtocol.MaxBody)]
    [InlineData(true, WireProtocol.MaxBody)]
    public async Task FrameLimitIncludesMetadataHeadersAndBody(bool contentHeaders, int bodyLength)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1/")
        {
            Content = new ByteArrayContent([])
        };
        request.Content.Headers.ContentLength = bodyLength;
        HttpHeaders headers = contentHeaders ? request.Content.Headers : request.Headers;
        var values = Enumerable.Repeat("", 64).ToArray();
        Assert.True(headers.TryAddWithoutValidation("X-Padding", values));
        var overhead = (await WireProtocol.EncodeRequest(request, CancellationToken.None)).Length;

        request.Content.Dispose();
        request.Content = new ByteArrayContent(new byte[bodyLength]);
        request.Content.Headers.ContentLength = bodyLength;
        headers = contentHeaders ? request.Content.Headers : request.Headers;
        var remaining = WireProtocol.MaxFrame - overhead - bodyLength;
        for (var i = 0; i < values.Length; i++)
        {
            var length = Math.Min(1024 * 1024, remaining);
            values[i] = new string('a', length);
            remaining -= length;
        }
        Assert.Equal(0, remaining);
        headers.Remove("X-Padding");
        Assert.True(headers.TryAddWithoutValidation("X-Padding", values));
        Assert.Equal(WireProtocol.MaxFrame, (await WireProtocol.EncodeRequest(request, CancellationToken.None)).Length);

        values[^1] += "a";
        headers.Remove("X-Padding");
        Assert.True(headers.TryAddWithoutValidation("X-Padding", values));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => WireProtocol.EncodeRequest(request, CancellationToken.None));
        Assert.Equal("IPC message exceeds frame limit.", error.Message);
    }

    [Fact]
    public async Task OversizedResponseHeadersFailBeforeSerializingLaterFields()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var value = new string('a', 1024 * 1024);
        Assert.True(response.Headers.TryAddWithoutValidation("X-Padding", Enumerable.Repeat(value, 128)));
        Assert.True(response.Headers.TryAddWithoutValidation("X-Later", "\ud800"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => WireProtocol.EncodeResponse(response, CancellationToken.None));
        Assert.Equal("IPC message exceeds frame limit.", error.Message);
    }

    [Fact]
    public void StringLimitUsesUtf8BytesAndRejectsBeforeAllocatingEncodedPayload()
    {
        var value = new string('\u00e9', 1024 * 1024 / 2);
        Assert.Equal(value, WireProtocol.DecodeHello(WireProtocol.EncodeHello(value)));
        var oversized = value + "a";
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => WireProtocol.EncodeHello(oversized));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 64 * 1024);
    }

    [Fact]
    public async Task PreCancelledReadsAndWritesObserveCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new MemoryStream(new byte[] { 0, 0, 0, 1, 1 });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WireProtocol.ReadFrame(stream, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WireProtocol.WriteFrame(stream, [1], cancellation.Token));
    }

    [Fact]
    public async Task WriteOnlyYarpStyleContentIsCopiedWithoutOpeningAReadStream()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1/") { Content = new WriteOnlyContent() };
        using var copy = WireProtocol.DecodeRequest(await WireProtocol.EncodeRequest(request, CancellationToken.None));
        Assert.Null(copy.Content!.Headers.ContentLength);
        Assert.Equal(new byte[] { 1, 2, 3 }, await copy.Content!.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task BufferingResponseDoesNotInventContentLength()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new WriteOnlyContent() };
        using var copy = WireProtocol.DecodeResponse(await WireProtocol.EncodeResponse(response, CancellationToken.None));
        Assert.Null(copy.Content!.Headers.ContentLength);
        Assert.Equal(new byte[] { 1, 2, 3 }, await copy.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public void OriginErrorsAreExplicitMessagesNotSyntheticHttpResponses()
    {
        var frame = WireProtocol.EncodeOriginError("Origin disconnected.");
        Assert.Equal("Origin disconnected.", WireProtocol.DecodeOriginError(frame));
        Assert.Throws<InvalidDataException>(() => WireProtocol.DecodeResponse(frame));
    }

    [Fact]
    public async Task RejectsDeclaredOversizedBodyBeforeCopying()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1/") { Content = new ByteArrayContent([]) };
        request.Content.Headers.ContentLength = WireProtocol.MaxBody + 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => WireProtocol.EncodeRequest(request, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsUndeclaredOversizedBodyDuringCopying()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1/") { Content = new OversizedContent() };
        await Assert.ThrowsAsync<InvalidDataException>(() => WireProtocol.EncodeRequest(request, CancellationToken.None));
    }

    private sealed class OversizedContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i <= WireProtocol.MaxBody / chunk.Length; i++)
                await stream.WriteAsync(chunk, 0, chunk.Length);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class WriteOnlyContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[] { 1, 2, 3 }, 0, 3);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => throw new NotImplementedException();
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            base.ReadAsync(buffer, offset, Math.Min(count, 1), cancellationToken);
    }
}
