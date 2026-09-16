# Google Cloud Storage HTTP cache content store

`DamianH.HttpHybridCacheHandler.ContentStore.GoogleCloudStorage` targets `net10.0`,
`netstandard2.0`, and `net472` using the official Google SDK and independent
content-store contracts.
Release tags use `cache-gcs-v`.

**[Full Google Cloud Storage guide](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/content-stores/google-cloud-storage/)**

Install from your application project directory:

```powershell
dotnet add package DamianH.HttpHybridCacheHandler.ContentStore.GoogleCloudStorage
```

```csharp
using DamianH.HttpHybridCacheHandler;
using Google.Cloud.Storage.V1;

services.AddSingleton<StorageClient>(StorageClient.Create());
services.AddHttpHybridCacheGoogleCloudStorageContentStore(options =>
{
    options.BucketName = "my-private-cache-bucket";
    options.Prefix = "http-cache/v1/";
});
```

The application owns the SDK client, credentials, retries, and pre-existing
resources. Configure handler offload separately and only one backend. No buckets
or lifecycle rules are created. Writes need seekable caller-owned input and exact
remaining length. Always dispose independent read streams; read through EOF to
observe integrity errors. No origin fallback is possible after partial delivery.
On Framework/Standard 2.0, use `using` on returned streams, or cast to
`IAsyncDisposable` for async disposal.

Missing-object detection requires `storage.buckets.get` as well as object
permissions; other failures are not misses. Checksum validation can fail after
finalization, and its compensating delete can also fail. Retention, unfinished
resumable sessions, versions, and soft-delete costs are operator responsibilities.
Do not eagerly delete shared bodies. See the guide for exact limits and opt-in
tests; mock tests do not prove interoperability, alternate-endpoint, or NativeAOT support.
