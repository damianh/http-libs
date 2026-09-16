# HTTP cache content-store contracts

`DamianH.HttpHybridCacheHandler.ContentStore` provides provider-independent
streaming contracts for the HTTP caching handler and adapters, without a cloud
SDK or HybridCache dependency.

Targets are `net10.0`, `netstandard2.0`, and `net472`, matching the handler and
four adapters. NuGet supplies older-target async support dependencies; enable
automatic binding redirects in Framework applications. The handler's HybridCache
dependency warns that `net472` is unsupported/untested upstream. The contracts
do not depend on HybridCache. See
[framework compatibility](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/compatibility/)
for header behavior and actual-runtime testing limitations.

**[Full contracts, ownership, and migration guide](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/content-stores/overview/)**

Install from your project directory:

```powershell
dotnet add package DamianH.HttpHybridCacheHandler.ContentStore
```

Given a configured `IHttpCacheContentStore`:

```csharp
using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("example body"));
await store.WriteAsync("opaque-key", input, input.Length, null, CancellationToken.None);
using var body = await store.OpenReadAsync("opaque-key", CancellationToken.None);
if (body is not null)
    await body.CopyToAsync(Stream.Null);
```

Writes require a readable, seekable caller-owned stream and exact remaining stored
length; adapters finish consuming it before returning and leave it open. Callers
own and must dispose returned read streams. Only missing reads return null.
Recognized SDK write failures become `HttpCacheContentStoreException`
(`IOException`); cancellation and programming errors are not converted.

`ILargeHttpCacheContentStore` identifies the optional external body store. HTTP
freshness, variant selection, and metadata invalidation remain with the handler;
retention is separate. Do not eagerly remove shared bodies when invalidating metadata.
The stream contract is a breaking change from the initial Stowage prerelease, not
a binary-compatible replacement. This package is versioned with `cache-contentstore-v` tags.
