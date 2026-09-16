# Amazon S3 HTTP cache content store

`DamianH.HttpHybridCacheHandler.ContentStore.S3` targets `net10.0`,
`netstandard2.0`, and `net472` using the official AWS SDK and independent
content-store contracts. Release tags use
`cache-s3-v`.

**[Full S3 guide](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/content-stores/s3/)**

Install from your application project directory:

```powershell
dotnet add package DamianH.HttpHybridCacheHandler.ContentStore.S3
```

Given an application-owned `IAmazonS3` client configured through the normal AWS
credential chain:

```csharp
using Amazon.S3;
using DamianH.HttpHybridCacheHandler;

services.AddSingleton<IAmazonS3>(s3);
services.AddHttpHybridCacheS3ContentStore(options =>
{
    options.BucketName = "my-existing-cache-bucket";
    options.KeyPrefix = "my-app/http-cache/";
});
```

Configure handler offload separately; register only one backend. The adapter
never provisions buckets or changes credentials, retry/signing settings, or
lifecycle policies. Inputs must be caller-owned, readable, seekable, and have the
exact remaining length. Dispose returned streams and keep the SDK client alive
until all users finish.

Multipart parts stream sequentially. Failed aborts can leave uploads requiring
operator cleanup; only `NoSuchKey` is a miss, not every 404. Retention is separate
from HTTP freshness; do not delete shared bodies on URI invalidation.
Arbitrary S3-compatible endpoints, directory buckets, and NativeAOT are not
promised. The guide includes limits, permissions, lifecycle/versioning safety,
and explicit opt-in real-provider tests; local tests are not interoperability proof.
