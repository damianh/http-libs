# HttpClientFactory caching sample

Demonstrates `HttpHybridCacheHandler` with a named `HttpClient`, HybridCache,
automatic decompression, and repeated-request timings.

**[Full sample guide](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/samples/http-client-factory/)**

From this directory:

```powershell
dotnet run
```

Or from the repository root:

```powershell
dotnet run --project hybrid-cache-handler\samples\HttpClientFactorySample\HttpClientFactorySample.csproj
```

The application sends three requests to `https://httpbin.org/cache/60` and waits
for a key before exiting. It sets a five-minute `FallbackCacheDuration`, uses the
default 10 MB size limit, and compresses eligible bodies at 1024 bytes.
Timings illustrate caching rather than establish a benchmark; actual hits depend
on the origin's responses. See the full guide for registration code and API references.
