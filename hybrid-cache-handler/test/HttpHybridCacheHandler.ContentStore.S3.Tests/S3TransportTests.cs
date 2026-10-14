using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Options;

namespace DamianH.Http.HybridCacheHandler;

public class S3TransportTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(5 * 1024 * 1024 + 113)]
    public async Task Official_SDK_transport_preserves_opaque_bytes_and_multipart_boundaries(int length)
    {
        using var transport = new S3Transport();
        using var client = Client(transport);
        var store = new S3ContentStore(client, Options.Create(new S3ContentStoreOptions
        {
            BucketName = "cache-bucket",
            MultipartThreshold = 1024,
            PartSize = 5 * 1024 * 1024
        }));
        var bytes = new byte[length];
        new Random(2026).NextBytes(bytes);
        // Internal gzip must remain an opaque object, never provider Content-Encoding metadata.
        bytes[0] = 0x1f;
        bytes[1] = 0x8b;
        using var input = new MemoryStream(bytes);
        await store.WriteAsync("key", input, bytes.Length, null, TestContext.Current.CancellationToken);
        Assert.True(input.CanRead);
        Assert.Equal(bytes, transport.Stored);
        Assert.DoesNotContain("gzip", transport.Encodings);
        Assert.Equal(length > 1024 ? 2 : 0, transport.Parts.Count);
        await using var read = await store.OpenReadAsync("key", TestContext.Current.CancellationToken);
        Assert.NotNull(read);
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, copy.ToArray());
        await store.RemoveAsync("key", TestContext.Current.CancellationToken);
        Assert.Null(await store.OpenReadAsync("key", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("NoSuchKey", 404, true)]
    [InlineData("NoSuchBucket", 404, false)]
    [InlineData("AccessDenied", 403, false)]
    [InlineData("SlowDown", 503, false)]
    public async Task Official_SDK_error_unmarshalling_only_treats_NoSuchKey_as_missing(string code, int status, bool missing)
    {
        using var transport = new S3Transport { ErrorCode = code, ErrorStatus = (HttpStatusCode)status };
        using var client = Client(transport);
        var store = new S3ContentStore(client, Options.Create(new S3ContentStoreOptions { BucketName = "cache-bucket" }));
        if (missing)
            Assert.Null(await store.OpenReadAsync("key", TestContext.Current.CancellationToken));
        else
            Assert.Equal(code, (await Assert.ThrowsAsync<AmazonS3Exception>(() =>
                store.OpenReadAsync("key", TestContext.Current.CancellationToken).AsTask())).ErrorCode);
    }

    private static AmazonS3Client Client(S3Transport transport) => new(new BasicAWSCredentials("test-key", "test-secret"), new AmazonS3Config
    {
        ServiceURL = "https://s3.test.invalid",
        AuthenticationRegion = "us-east-1",
        ForcePathStyle = true,
        MaxErrorRetry = 0,
        HttpClientFactory = new TransportFactory(transport)
    });

    private sealed class TransportFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler, disposeHandler: false);
    }

    private sealed class S3Transport : HttpMessageHandler
    {
        public byte[]? Stored { get; private set; }
        public List<byte[]> Parts { get; } = [];
        public List<string> Encodings { get; } = [];
        public string? ErrorCode { get; init; }
        public HttpStatusCode ErrorStatus { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query;
            if (ErrorCode is not null) return Error(ErrorCode, ErrorStatus);
            if (request.Method == HttpMethod.Get)
                return Stored is null ? Error("NoSuchKey", HttpStatusCode.NotFound) :
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Stored) };
            if (request.Method == HttpMethod.Delete)
            {
                Stored = null;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Post && query.Contains("uploads"))
                return Xml("<InitiateMultipartUploadResult><Bucket>cache-bucket</Bucket><Key>key</Key><UploadId>upload</UploadId></InitiateMultipartUploadResult>");
            if (request.Method == HttpMethod.Post && query.Contains("uploadId"))
            {
                var completion = XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var checksums = completion.Descendants().Where(element => element.Name.LocalName == "ChecksumSHA256").Select(element => element.Value).ToArray();
                Assert.Equal(Parts.Select(part => Convert.ToBase64String(SHA256.HashData(part))), checksums);
                Stored = Parts.SelectMany(part => part).ToArray();
                return Xml("<CompleteMultipartUploadResult><Location>https://s3.test.invalid/cache-bucket/key</Location><Bucket>cache-bucket</Bucket><Key>key</Key><ETag>\"complete\"</ETag></CompleteMultipartUploadResult>");
            }
            Assert.Equal(HttpMethod.Put, request.Method);
            var content = request.Content!;
            Encodings.AddRange(content.Headers.ContentEncoding);
            var body = await content.ReadAsByteArrayAsync(cancellationToken);
            if (content.Headers.ContentEncoding.Contains("aws-chunked") ||
                (request.Headers.TryGetValues("Content-Encoding", out var encodings) && encodings.Contains("aws-chunked")))
                body = DecodeChunks(body);
            if (query.Contains("partNumber"))
                Parts.Add(body);
            else
                Stored = body;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"etag-{Parts.Count}\"");
            if (query.Contains("partNumber"))
                response.Headers.Add("x-amz-checksum-sha256", Convert.ToBase64String(SHA256.HashData(body)));
            return response;
        }

        private static byte[] DecodeChunks(byte[] body)
        {
            using var decoded = new MemoryStream();
            var position = 0;
            while (position < body.Length)
            {
                var end = body.AsSpan(position).IndexOf("\r\n"u8);
                Assert.True(end >= 0);
                var header = Encoding.ASCII.GetString(body, position, end).Split(';')[0];
                var size = Convert.ToInt32(header, 16);
                position += end + 2;
                if (size == 0) break;
                decoded.Write(body, position, size);
                position += size + 2;
            }
            return decoded.ToArray();
        }

        private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };

        private static HttpResponseMessage Error(string code, HttpStatusCode status) => new(status)
        {
            Content = new StringContent($"<Error><Code>{code}</Code><Message>simulated</Message><RequestId>test</RequestId></Error>", Encoding.UTF8, "application/xml")
        };
    }
}
