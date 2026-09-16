# Google Cloud Storage HTTP cache content store

`DamianH.HttpHybridCacheHandler.ContentStore.GoogleCloudStorage` targets `net10.0`,
`netstandard2.0`, and `net472` (.NET Framework 4.7.2+)
and uses the official `Google.Cloud.Storage.V1` **4.15.0** SDK. It depends on the
content-store abstractions, not the HTTP handler. Its independent release tag
prefix is `cache-gcs-v`.

```csharp
using DamianH.HttpHybridCacheHandler;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
// Application Default Credentials; configure credentials/endpoints/retries on
// your client as appropriate. Provision the bucket separately.
services.AddSingleton<StorageClient>(StorageClient.Create());
services.AddHttpHybridCacheGoogleCloudStorageContentStore(options =>
{
    options.BucketName = "my-private-cache-bucket";
    options.Prefix = "http-cache/v1/";
    options.DownloadBufferSize = 64 * 1024;
});
```

This registers `ILargeHttpCacheContentStore` directly; configure the HTTP handler's
large-body offload separately. Configure only one large-content backend. The
application owns the injected client. The adapter never creates buckets or changes
credentials, retry settings, access controls, or lifecycle rules.

## Streaming and ownership

- Logical keys map to `prefix/` plus lowercase SHA-256 of their UTF-8 bytes.
  Empty prefixes are supported. Changing the bucket or prefix loses access to
  old bodies; raw URLs and cache keys never appear in object names.
- `WriteAsync(key, content, contentLength, tags, ct)` requires a readable,
  seekable stream positioned before exactly `contentLength` remaining bytes.
  The adapter uploads a fixed-length view and leaves the caller's stream open.
  The caller must not mutate the stream during the awaited upload.
- Uploads use the SDK's resumable upload with 256 KiB chunks and
  `DeleteAndThrow` checksum validation. A finalized upload is published atomically
  by GCS; no partially uploaded object is readable. SDK checksum validation occurs
  after finalization and deletes a corrupt object on failure, so a validation
  failure can briefly leave a finalized object visible. Deletion can itself fail,
  for example due to permissions; the SDK exception retains those details.
  Recognized API/transport/checksum upload failures are wrapped in
  `HttpCacheContentStoreException`.
- Bytes are opaque `application/octet-stream`: **no `ContentEncoding` metadata**
  is set, even for internally compressed cache bodies. Tags are advisory and
  ignored; they are not GCS labels or provider-side invalidation.
- `OpenReadAsync` first fetches metadata, then downloads that exact generation.
  Each call returns its own non-seekable stream. A bounded pipe bridges the
  write-oriented SDK download API. Its default pause threshold is 64 KiB, with
  at most one additional 16 KiB write segment, plus fixed SDK/network buffers
  (download chunk size 256 KiB). It never buffers the whole object.
- Read through EOF to observe SDK integrity validation and late transfer errors.
  Bytes may already have reached the caller when a checksum or network error is
  reported. There is no transparent origin fallback after partial delivery.
- **Always dispose the returned stream**, preferably with `await using`.
  On .NET Framework/.NET Standard 2.0, use `using` on the returned `Stream`,
  or cast it to `IAsyncDisposable` to await disposal.
  Disposal cancels and joins the owned producer; synchronous disposal may block
  until SDK cancellation completes. The opening cancellation token remains
  active for the stream lifetime. Cancelling a read also cancels the download.
  As with ordinary streams, concurrent reads/disposal are not supported.
  A custom `StorageClient` implementation must honor cancellation while waiting
  on external work, otherwise disposal may wait indefinitely.
- Missing objects alone map to null/idempotent removal. GCS's ambiguous 404
  responses cause a bucket metadata probe: applications need
  `storage.buckets.get` as well as the relevant object permissions. A missing
  bucket or denied probe is an error, not a miss. Errors after metadata lookup,
  including deletion of the pinned generation, propagate through the stream.
  Other provider/authentication/configuration failures are not suppressed.
- Removal always deletes one mapped object, never a prefix. Bodies can be shared
  by multiple metadata entries: URI invalidation must not eagerly delete them.

## Retention is operator-managed

HTTP freshness is the handler's concern, not GCS object age. Configure lifecycle
rules on a dedicated bucket or the configured prefix. For example:

```json
{
  "rule": [
    {
      "action": { "type": "Delete" },
      "condition": { "age": 30, "isLive": true, "matchesPrefix": ["http-cache/v1/"] }
    },
    {
      "action": { "type": "Delete" },
      "condition": { "daysSinceNoncurrentTime": 7, "isLive": false, "matchesPrefix": ["http-cache/v1/"] }
    }
  ]
}
```

Review versioning, soft-delete retention, holds, and retention policies separately;
they can retain data/cost beyond these rules or prevent deletion. Noncurrent-version
rules apply when object versioning is enabled. Lifecycle cleanup can remove a
still-referenced body (a subsequent lookup becomes a miss), and deduplication or
HTTP revalidation does not refresh object creation time.

The high-level SDK upload API does not expose its resumable-session URI to this
adapter for explicit abort. Interrupted resumable sessions may remain until GCS
expires them (normally one week); live-object lifecycle rules do not abort them.
Monitor unfinished uploads and apply suitable operational limits. No distributed
reference counting, automatic bucket cleanup, or exact HTTP-expiry synchronization
is provided.

The virtual-client tests cover contract, error, cancellation, and backpressure
behavior. Real GCS credentials/resources are not required by these tests, and
mocked results do not establish real-provider interoperability, NativeAOT support,
or compatibility with alternate GCS endpoints.

## Opt-in real GCS tests

The integration theory covers exact large-byte and empty-body round trips,
missing-body lookup, and idempotent single-object removal using the real SDK.
It is conditionally **skipped** unless `HTTP_CACHE_GCS_RUN_INTEGRATION` is exactly
`1`; no client or cloud request is created while skipped.

To opt in, supply your own existing, isolated test bucket and prefix, plus
Application Default Credentials with object read/create/delete and
`storage.buckets.get` permissions:

```powershell
$env:HTTP_CACHE_GCS_RUN_INTEGRATION = "1"
$env:HTTP_CACHE_GCS_TEST_BUCKET = "my-existing-private-test-bucket"
$env:HTTP_CACHE_GCS_TEST_PREFIX = "http-cache-adapter-tests"
# Optional if ADC is already configured:
$env:GOOGLE_APPLICATION_CREDENTIALS = "C:\private\gcs-test-service-account.json"
dotnet test --project hybrid-cache-handler\test\HttpHybridCacheHandler.ContentStore.GoogleCloudStorage.Tests\HttpHybridCacheHandler.ContentStore.GoogleCloudStorage.Tests.csproj
```

Once enabled, missing configuration, credentials, authorization, transport,
integrity, and cleanup failures fail the test; they are not converted to skips.
Each case appends its own random GUID namespace beneath the supplied prefix and
cleans up only its exact mapped object in `finally`, with a separate cleanup
timeout. Tests do not provision buckets, list objects, recursively delete prefixes,
or alter lifecycle policies. A process crash or inaccessible bucket can leave
test-owned objects behind. The resource owner remains responsible for costs,
orphan cleanup, noncurrent versions, and soft-delete retention. Use a dedicated
test bucket, not production resources.

Passing virtual-client tests or seeing skipped integration tests is **not**
evidence of a successful real-provider run.
