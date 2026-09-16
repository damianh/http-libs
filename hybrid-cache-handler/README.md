# DamianH.HttpHybridCacheHandler

[![NuGet](https://img.shields.io/nuget/v/DamianH.HttpHybridCacheHandler.svg)](https://www.nuget.org/packages/DamianH.HttpHybridCacheHandler/)
[![Downloads](https://img.shields.io/nuget/dt/DamianH.HttpHybridCacheHandler.svg)](https://www.nuget.org/packages/DamianH.HttpHybridCacheHandler/)

RFC 9111 compliant client-side HTTP caching for `HttpClient`, powered by .NET's `HybridCache` for efficient L1 (memory) and L2 (distributed) caching.

## Table of Contents

- [Features](#features)
- [Installation](#installation)
- [Framework compatibility](#framework-compatibility)
- [Quick Start](#quick-start)
- [Handler Pipeline Configuration](#handler-pipeline-configuration)
  - [Recommended Setup](#recommended-setup)
  - [AutomaticDecompression Explained](#automaticdecompression-explained)
  - [Handler Ordering](#handler-ordering)
  - [Common Mistakes](#common-mistakes)
- [Configuration Options](#configuration-options)
- [Cache Behavior](#cache-behavior)
- [Performance & Memory](#performance--memory)
- [Metrics](#metrics)
- [Benchmarks](#benchmarks)
- [RFC 9111 Conformance Suite](#rfc-9111-conformance-suite)
- [Samples](#samples)

## Features

### Core Caching Capabilities
- **RFC 9111 Compliant**: Full implementation of HTTP caching specification for client-side caching
- **HybridCache Integration**: Leverages .NET's HybridCache for efficient L1 (memory) and L2 (distributed) caching
- **Transparent Operation**: Works seamlessly with existing HttpClient code

### Cache-Control Directives

**Request Directives:**
- `max-age`: Control maximum acceptable response age
- `max-stale`: Accept stale responses within specified staleness tolerance
- `min-fresh`: Require responses to remain fresh for specified duration
- `no-cache`: Force revalidation with origin server
- `no-store`: Bypass cache completely
- `only-if-cached`: Return cached responses or 504 if not cached

**Response Directives:**
- `max-age`: Define response freshness lifetime
- `no-cache`: Store but require validation before use
- `no-store`: Prevent caching
- `public`/`private`: Control cache visibility
- `must-revalidate`: Enforce validation when stale

### Advanced Features

- **Conditional Requests**: Automatic ETag (`If-None-Match`) and Last-Modified (`If-Modified-Since`) validation
- **Vary Header Support**: Response-driven `Vary` matching with multiple stored variants per resource
- **Unsafe Method Invalidation**: Invalidates cached GET/HEAD entries for the target URI (and same-origin `Location`/`Content-Location`) after successful unsafe requests like POST/PUT/DELETE
- **Freshness Calculation**: Supports `Expires` header, `Age` header, and heuristic freshness (Last-Modified based)
- **Stale Response Handling**: 
  - `stale-while-revalidate`: Serve stale content while updating in background
  - `stale-if-error`: Serve stale content when origin is unavailable
- **Configurable Limits**: Per-item content size limits (default 10MB)
- **Optional Large Content Store**: Route large cached response bodies to Azure Blob Storage, Amazon S3, Google Cloud Storage, or the filesystem using separately versioned adapters
- **Metrics**: Built-in metrics via `System.Diagnostics.Metrics` for hit/miss rates and cache operations
- **Custom Cache Keys**: Extensible cache key generation for advanced scenarios
- **Request Collapsing**: The default buffered path coalesces requests through HybridCache. Opt-in streaming fills use independent origin streams until an entry is published

## Installation

```bash
dotnet add package DamianH.HttpHybridCacheHandler
```

## Framework compatibility

All six packages ship `net10.0`, `netstandard2.0`, and `net472` assets:
the handler, ContentStore contracts, and the Azure Blob, S3, Google Cloud Storage,
and filesystem adapters. The .NET Standard build is a library asset, not an
executable runtime. The existing samples, benchmarks, and YARP conformance host
remain .NET 10 applications; the separate FileDistributedCache product is not
part of this target expansion.

NuGet restores target-appropriate dependency assemblies, including the older-target
support packages for async interfaces, `TimeProvider`, memory, and diagnostics
where required. .NET Framework applications should enable automatic binding
redirect generation. Cloud adapters retain their existing SDK versions; check
the SDK vendors' support policies as well as the library target.

**Upstream dependency warning:** `Microsoft.Extensions.Caching.Hybrid` 10.8.0
contains .NET Standard 2.0 and .NET Framework-compatible assemblies, but its
`buildTransitive` targets explicitly warn that `net472` is unsupported and
untested by that package and recommend .NET 8 or later. We do not suppress that
warning. This repository's downstream compatibility coverage does not change
Microsoft's support commitment.

### Header representation

- `net10.0` retains the `HttpHeaders.NonValidated` snapshot path.
- `netstandard2.0` and `net472` use supported public header enumeration, preserving
  the values and value boundaries that enumeration exposes. No reflection,
  private runtime fields, serialized-header reparsing, or comma-joining workaround
  is used.
- The older-target builds can normalize casing, whitespace, dates, ETags, and
  parsed value grouping, including duplicate parsed directives. Exact original
  header representation is not guaranteed. The `netstandard2.0` build uses this
  path **even when loaded on .NET 10**.
- Standards-compliant freshness, privacy/no-store protection, validators, Vary,
  invalidation, and streaming publication rules are intended to match. Malformed
  headers may be rejected more conservatively: for example, Framework does not
  normalize an `Expires` date ending in `UTC` to the required `GMT`, so the strict
  date parser rejects it. Without another freshness rule, that response is not
  considered fresh merely because the malformed date is in the future.

### Compatibility validation

The dedicated compatibility suite runs the `net10.0` baseline, explicitly forced
`netstandard2.0` library assets on a .NET 10 host on Windows/Linux, and `net472`
executables on Windows. It asserts `TargetFrameworkAttribute` on all six loaded
assemblies, rather than assuming a modern test host selected older assets.
Tests use an in-process origin and no new live cloud credentials.
The default Microsoft HybridCache implementation and its default serializers are
also exercised across independent DI containers sharing a `MemoryDistributedCache`
backend. Those cases require actual L2 metadata/body reads, including Vary,
validators, header-value boundaries, Age, and compressed/uncompressed content;
they do not substitute a test HybridCache or custom metadata serializer.

From the repository root:

```bash
dotnet run hybrid-cache-handler/build.cs -- test-compatibility-modern
dotnet run hybrid-cache-handler/build.cs -- test-compatibility-standard
# Windows only
dotnet run hybrid-cache-handler/build.cs -- test-compatibility-framework
# All six nupkgs: asset/dependency checks and package-only consumers
dotnet run hybrid-cache-handler/build.cs -- check-packages
```

The Framework test executable targets 4.7.2, but Windows runners generally have
an in-place .NET Framework 4.8 runtime. The suite reports the installed runtime;
this is not a claim that an actual 4.7.2 runtime was tested. Package checks build
all three consumer targets, run .NET 10 consumers on Windows/Linux and Framework
consumers on Windows, and inspect the selected compile/runtime package assets.
The .NET Standard consumer is build-only; the forced-asset test host provides
its executable behavioral coverage. The RFC conformance matrix also exercises
all three implementations in default HybridCache and filesystem modes; see
[RFC 9111 Conformance Suite](#rfc-9111-conformance-suite).

Development ContentStore prereleases can sort below the deliberately released
dependency floor (`0.1.0`). In that case, the package checker repacks the identical
contract binaries at the floor in a disposable, private feed for consumer testing.
It does not rewrite the original release artifacts or suppress downgrade errors;
release ContentStore at the declared floor before releasing its consumers.

## Quick Start

### Basic Usage with Recommended Configuration (.NET 10)

```csharp
var services = new ServiceCollection();

services.AddHttpHybridCacheHandler(options =>
{
    options.FallbackCacheDuration = TimeSpan.FromMinutes(5);
    options.MaxCacheableContentSize = 10 * 1024 * 1024; // 10MB
    options.CompressionThreshold = 1024; // Compress cached content >1KB
});

services.AddHttpClient("MyClient")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // Enable automatic decompression - server compression handled transparently
        AutomaticDecompression = DecompressionMethods.All,
        
        // DNS refresh every 5 minutes - critical for cloud/microservices
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        
        // Close idle connections after 2 minutes
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        
        // Reasonable connection timeout
        ConnectTimeout = TimeSpan.FromSeconds(10)
    })
    .AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>());

var client = services.BuildServiceProvider()
    .GetRequiredService<IHttpClientFactory>()
    .CreateClient("MyClient");

var response = await client.GetAsync("https://api.example.com/data");
```

## Handler Pipeline Configuration

### Recommended Setup

**On .NET 10, prefer `SocketsHttpHandler` with automatic decompression enabled**
for explicit DNS refresh and connection-pool lifetime control:

```csharp
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
})
```

**On .NET Framework 4.7.2, use `HttpClientHandler`:**

```csharp
services.AddHttpClient("MyClient")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AutomaticDecompression =
            DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    .AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>());
```

This example also uses APIs available to .NET Standard 2.0 consumers. Choose the
primary handler for the application's actual runtime; .NET Standard itself is
not a runtime. `SocketsHttpHandler` pooling options such as
`PooledConnectionLifetime`, `PooledConnectionIdleTimeout`, and `ConnectTimeout`
are not available on Framework's `HttpClientHandler`. Do not copy those options
or `DecompressionMethods.All` into Framework-targeted code.

### AutomaticDecompression Explained

**Two different compressions:**

1. **Transport Compression** (Server → Client)
   - Controlled by: `AutomaticDecompression` on the primary HTTP handler
   - Purpose: Reduce network bandwidth
   - Result: Handler receives **decompressed** content

2. **Cache Storage Compression** (This library)
   - Controlled by: `CompressionThreshold` in options
   - Purpose: Reduce cache storage size
   - Result: Content compressed before storing in cache

**Example Flow:**
```
Server sends: gzipped 512 bytes
    ↓
SocketsHttpHandler: auto-decompresses → 2048 bytes
    ↓
HttpHybridCacheHandler: receives decompressed content
    ↓
Our compression: compresses → 600 bytes
    ↓
Cache: stores 600 bytes (no Base64 overhead!)
```

**Benefits:**
- Cache handler can inspect and validate response content
- Cache-Control, ETag, and Last-Modified headers are readable
- Enables intelligent caching decisions
- Storage compression is optional and configurable

### Handler Ordering

**Pipeline structure:**
```
HttpClient → [Outer Handlers] → HttpHybridCacheHandler → SocketsHttpHandler → Network
```

#### With Polly Resilience (Recommended for Production)

```csharp
.AddHttpMessageHandler(sp => new HttpHybridCacheHandler(...))
.AddStandardResilienceHandler(options =>
{
    options.Retry.MaxRetryAttempts = 3;
    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
});
```

**Order:** Polly (outer) → Cache → SocketsHttpHandler

**Why:** Cache hit = fast path, Polly never invoked. Cache miss + network failure = Polly retries.

#### With Authentication

```csharp
.AddHttpMessageHandler(() => new AuthenticationHandler())
.AddHttpMessageHandler(sp => sp.GetRequiredService<HttpHybridCacheHandler>());
```

To include auth headers in request-key partitioning, configure `VaryHeaders` in the `AddHttpHybridCacheHandler` options.

Auth is applied before caching; configured `VaryHeaders` partition the request key and response `Vary` is always enforced on cache hits.

### Common Mistakes

**Wrong: Not enabling AutomaticDecompression**
```csharp
new SocketsHttpHandler()  // Defaults to None!
```
**Problem:** Cache handler receives compressed content, can't inspect properly.

**Correct: Explicitly enable decompression**
```csharp
new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.All
}
```

**Choose a handler your application target supports.** `HttpClientHandler` with
GZip/Deflate decompression is the Framework-compatible choice;
`SocketsHttpHandler` is recommended for .NET 10 applications.

**Wrong: Cache handler after Polly**
```csharp
.AddStandardResilienceHandler()  // Outer
.AddHttpMessageHandler(sp => new HttpHybridCacheHandler(...))  // Inner - Wrong!
```

**Correct: Cache handler before Polly**
```csharp
.AddHttpMessageHandler(sp => new HttpHybridCacheHandler(...))  // Inner - Correct!
.AddStandardResilienceHandler()  // Outer
```

**Golden Rule:** `HttpHybridCacheHandler` should receive **decompressed, ready-to-use** content.

## Configuration Options

### Cache Mode

The library supports two cache modes, following RFC 9111 semantics:

#### CacheMode.Private (Default)
Browser-like cache behavior suitable for client applications:

**Use Cases:**
- HttpClient in web applications, APIs, background services
- Scaled-out clients sharing cache (multiple instances, serverless/Lambda)
- Per-user/per-tenant caching scenarios

**Behavior:**
- Caches responses with `Cache-Control: private`
- Uses `max-age` directive (ignores `s-maxage`)
- Caches authenticated requests if marked `private` or `max-age`
- Request key can be client-specific when `VaryHeaders` is configured
- Response variants are always matched using stored `Vary` header values

**Example:**
```csharp
new HttpHybridCacheHandlerOptions
{
    Mode = CacheMode.Private, // Shares cache across app instances via Redis L2
    FallbackCacheDuration = TimeSpan.FromMinutes(5)
}
```

#### CacheMode.Shared
Proxy/CDN-like cache behavior suitable for gateways:

**Use Cases:**
- Reverse proxies (YARP, Envoy)
- API gateways
- Edge caches / CDN-like scenarios

**Behavior:**
- Does NOT cache responses with `Cache-Control: private`
- Prefers `s-maxage` over `max-age`
- In shared mode, authenticated responses are cacheable only with explicit shared-cache permissions (`public`, `s-maxage`, or `must-revalidate`)
- Supports targeted cache-control headers (for example `CDN-Cache-Control`) via `TargetedCacheControlHeaderNames`
- Cache is shared across all clients/users

**Example:**
```csharp
new HttpHybridCacheHandlerOptions
{
    Mode = CacheMode.Shared, // RFC 9111 shared cache semantics
    MaxCacheableContentSize = 50 * 1024 * 1024 // 50MB
}
```

### HttpHybridCacheHandlerOptions

- **Mode**: Cache mode determining caching behavior (default: `CacheMode.Private`). Use `CacheMode.Shared` for proxy/CDN scenarios
- **HeuristicFreshnessPercent**: Heuristic freshness percentage for responses with Last-Modified but no explicit freshness info (default: 0.1 or 10%)
- **HeuristicFreshnessMinimum**: Minimum heuristic freshness lifetime applied when Last-Modified exists but explicit freshness is absent (default: 30 seconds)
- **VaryHeaders**: Headers to include in Vary-aware cache keys (default: none `[]`; response `Vary` matching is still enforced)
- **TargetedCacheControlHeaderNames**: Response headers that carry targeted shared-cache directives (default: `CDN-Cache-Control`). Applied only in `CacheMode.Shared`
- **MaxCacheableContentSize**: Maximum size in bytes for cacheable response content (default: 10 MB). Responses larger than this will not be cached
- **FallbackCacheDuration**: Fallback cache duration for responses without explicit caching headers (default: `TimeSpan.MinValue`, meaning responses without caching headers are not cached)
- **CompressionThreshold**: Minimum content size in bytes to enable compression (default: 1024 bytes). Set to 0 or negative value to disable compression
- **LargeContentThreshold**: Size threshold in bytes for routing cached content to an optional `ILargeHttpCacheContentStore` (default: 1 MiB). Set to 0 or negative value to always use HybridCache content storage
- **CompressibleContentTypes**: Content types eligible for compression (default: `text/*`, `application/json`, `application/json+*`, `application/xml`, `application/javascript`, `image/svg+xml`)
- **CacheableContentTypes**: Content types eligible for caching (default: `text/*`, `application/json`, `application/json+*`, `application/xml`, `application/javascript`, `application/xhtml+xml`, `image/*`)
- **ContentKeyPrefix**: Prefix for content cache keys (default: `"httpcache:content:"`). Content is stored separately from metadata to avoid Base64 encoding overhead
- **IncludeDiagnosticHeaders**: Whether to include diagnostic headers (`X-Cache-Diagnostic`, etc.) in responses (default: `false`)

### Optional content stores

Metadata and HTTP freshness remain in HybridCache. Choose one separately packaged body store:

| Package | Backend | Configuration |
| --- | --- | --- |
| `DamianH.HttpHybridCacheHandler.ContentStore` | Provider-independent streaming contracts, no cloud SDK dependency | [Contract and ownership rules](src/HttpHybridCacheHandler.ContentStore/README.md) |
| `DamianH.HttpHybridCacheHandler.ContentStore.AzureBlob` | Official `Azure.Storage.Blobs` SDK | [Azure setup](src/HttpHybridCacheHandler.ContentStore.AzureBlob/README.md) |
| `DamianH.HttpHybridCacheHandler.ContentStore.S3` | Official `AWSSDK.S3` SDK | [S3 setup](src/HttpHybridCacheHandler.ContentStore.S3/README.md) |
| `DamianH.HttpHybridCacheHandler.ContentStore.GoogleCloudStorage` | Official `Google.Cloud.Storage.V1` SDK | [Google Cloud setup](src/HttpHybridCacheHandler.ContentStore.GoogleCloudStorage/README.md) |
| `DamianH.HttpHybridCacheHandler.ContentStore.FileSystem` | Native file streams, no cloud SDK | [Filesystem setup](src/HttpHybridCacheHandler.ContentStore.FileSystem/README.md) |

Adapters depend on ContentStore, not on the handler or HybridCache. Configure credentials,
endpoints, and retries through an injected SDK client. Registration never provisions cloud
resources or modifies lifecycle policies. Stowage and FluentStorage are not dependencies.

`LargeContentThreshold` uses the original response-body length, before internal storage
compression. `MaxCacheableContentSize` still controls whether a body may be cached at all.
Without an external store, the existing HybridCache-only path remains available.

#### Streaming and retention

With external storage enabled, cacheable origin bodies can stream to the caller while the
handler stages a copy in bounded memory and temporary files. A completed body is uploaded
before its metadata is published. Early disposal, an incomplete response, or cancellation
does not populate the cache. Cache admission limits must not truncate the origin response.

Use `HttpCompletionOption.ResponseHeadersRead` and consume/dispose the response stream
to benefit from streaming. `HttpClient`'s default `ResponseContentRead`, `ReadAsStringAsync`,
and `ReadAsByteArrayAsync` can still buffer the whole response in the caller.
Cold streaming requests do not share a live response stream; simultaneous misses can
make independent origin requests. Completing consumption can include cache upload latency.

Temporary spool storage and persistent cache-body retention are separate concerns.
Configure the handler's temporary staging independently of the selected body store:

```csharp
services.AddHttpHybridCacheHandler(options =>
{
    options.LargeContentThreshold = 1024 * 1024;
    options.SpoolMemoryThreshold = 64 * 1024;
    options.MaxSpoolDiskBytes = 1024L * 1024 * 1024;
    options.MaxConcurrentDiskSpools = 32;
    options.SpoolDirectory = Path.Combine(Path.GetTempPath(), "MyApp-cache-spool");
});
```

The defaults are 64 KiB staging memory per spool, a 1 GiB aggregate active disk
budget, 32 concurrent disk spools, and the system temporary directory. Compression
may require a second spool, which counts toward the same disk limits. Reservations
are process-wide; use consistent limits across handlers. Exhaustion abandons caching
while origin delivery continues. These limits exclude caller buffers, fixed transfer
buffers, provider SDK buffers, and persistent body storage.

Use a trusted private staging parent. This is an application/deployment responsibility,
including when `SpoolDirectory` is null and uses `Path.GetTempPath()`: verify access
for the account running the process rather than assuming the default directory is
private. On Windows, provision the parent with inheritable ACLs that restrict access
to trusted principals before spooling. The handler inherits those ACLs; it does not
restrict or validate them, so a shared/public parent is not made private by the handler.
Unique directory names and leases manage spool lifetime, not access control.

Each spill owns a unique leased directory;
cleanup releases completed spools and can reclaim abandoned leased directories
without deleting a live owner's files. HTTP cache metadata remains the caller's
HybridCache configuration responsibility.

On Linux and macOS, the `netstandard2.0` and `net472` builds create these private
staging directories through the native POSIX `mkdir` OS call (`libc`), passing a
UTF-8 path and owner-only mode `0700`. The return code is checked; there is no
fallback that creates a directory with weaker permissions or narrows permissions
after response data is written. This native call does not inspect runtime internals.
The `net10.0` build retains managed `Directory.CreateDirectory` with
`UnixFileMode`. On Windows, directory creation uses the managed API and the trusted
parent's ACLs. Other operating systems are not supported for older-target disk
spooling. These are implementation/platform requirements, not claims of completed
macOS or actual Framework 4.7.2 runtime testing.

Configure cloud lifecycle policies for cached objects and incomplete uploads, and configure
age/size cleanup for filesystem storage. Retention is not HTTP freshness: a deleted body
becomes a cache miss even if metadata is still fresh. Revalidation does not necessarily
refresh an object's creation time. Do not delete shared content-addressed bodies merely
because one URL or variant was invalidated.

#### Versioning and migration

The handler, ContentStore, and each adapter have independent versions and release tags.
Release compatible ContentStore versions before consumers. SDK updates need not force
a handler release.

The content-store interfaces retain their namespace but now live in the ContentStore
assembly. Implementations migrate from a materialized sequence write to a seekable,
caller-owned input stream with an explicit stored length. The adapter must leave the
input stream open and finish consuming it before returning. Returned read streams are
owned by the response/caller. This is a breaking change from the initial pre-release
Stowage implementation, not a binary-compatible replacement.

Build targets are `pack-handler`, `pack-contentstore`, `pack-azureblob`, `pack-s3`,
`pack-gcs`, and `pack-filesystem`; `pack-all` creates the local bundle. The release
workflow selects one package and publishes only its exact artifact. Consumers declare
the compatible ContentStore dependency floor through `HttpCacheContentStorePackageVersion`
(initially `0.1.0`), rather than leaking another project's computed prerelease version.

## Metrics

The handler emits the following counters via `System.Diagnostics.Metrics` under the meter named `DamianH.HttpHybridCacheHandler`:

| Counter | Description |
|---------|-------------|
| `cache.hits` | Number of cache hits (fresh, revalidated, stale-while-revalidate, stale-if-error) |
| `cache.misses` | Number of cache misses (including cache errors and failed revalidations) |
| `cache.stale` | Number of stale cache entries served (stale-while-revalidate or stale-if-error) |
| `cache.size_exceeded` | Number of responses that exceeded `MaxCacheableContentSize` and were not cached |

All counters include the following tags (following [OpenTelemetry semantic conventions](https://opentelemetry.io/docs/specs/semconv/http/http-metrics/)):

| Tag | Description | Example |
|-----|-------------|---------|
| `http.request.method` | HTTP method | `GET`, `HEAD` |
| `url.scheme` | URL scheme | `http`, `https` |
| `server.address` | Server hostname | `api.example.com` |
| `server.port` | Server port | `443` |

## Cache Behavior

### Diagnostic Headers

When `IncludeDiagnosticHeaders` is enabled in options, the handler adds diagnostic information to responses:

- **X-Cache-Diagnostic**: Indicates cache behavior for the request
  - `HIT-FRESH`: Served from cache, content is fresh
  - `HIT-REVALIDATED`: Served from cache after successful 304 revalidation
  - `HIT-STALE-WHILE-REVALIDATE`: Served stale while background revalidation occurs
  - `HIT-STALE-IF-ERROR`: Served stale due to backend error
  - `HIT-ONLY-IF-CACHED`: Served from cache with only-if-cached directive
  - `MISS`: Not in cache, fetched from backend
  - `MISS-REVALIDATED`: Cache entry was stale and resource changed
  - `MISS-CACHE-ERROR`: Cache operation failed, bypassed
  - `MISS-ONLY-IF-CACHED`: Not in cache with only-if-cached directive (504 Gateway Timeout)
  - `BYPASS-METHOD`: Request method not cacheable (POST, PUT, etc.)
  - `BYPASS-NO-STORE`: Request has no-store directive
- **X-Cache-Age**: Age of cached content in seconds (only for cache hits)
- **X-Cache-MaxAge**: Maximum age of cached content in seconds (only for cache hits)
- **X-Cache-Compressed**: "true" if content was stored compressed (only for cache hits)

Example:
```csharp
var options = new HttpHybridCacheHandlerOptions
{
    IncludeDiagnosticHeaders = true
};
```

### Cacheable Responses

Only GET and HEAD requests are cached. Responses are cached when:
- Status code is 200 OK
- Cache-Control allows caching (not no-store, not no-cache without validation)
- Content size is within MaxContentSize limit

### Cache Key Generation

Cache keys are generated from:
- HTTP method
- Request URI
- Optional configured `VaryHeaders` request values

When reading from cache, the handler then enforces the stored response `Vary` values and selects a matching variant.

### Conditional Requests

When serving stale content, the handler automatically adds:
- `If-None-Match` header with cached ETag
- `If-Modified-Since` header with cached Last-Modified date

If the server responds with 304 Not Modified, the cached response is refreshed and served.

## Performance & Memory

The handler is designed for high-performance scenarios with several key optimizations:

### Content/Metadata Separation Architecture

**Eliminates Base64 overhead in distributed cache:**

- **Metadata** (small, ~1-2KB): Status code, headers, timestamps, and variant metadata → Stored as JSON
- **Content** (large, variable): Stored separately as bytes in HybridCache, or as a streamed object in the configured external store
  - **No Base64 encoding** = 33% size savings
  - Content deduplication via SHA256 hash
  - Same content shared across cache variants (different `Vary` selections)

**Trade-offs:**
- Two cache lookups (metadata + content) vs one lookup
- Acceptable: L1 (memory) cache makes second lookup very fast (~microseconds)
- Benefit: Zero Base64 overhead on all cached content

### Memory Efficiency

- **Buffered fills** use HybridCache request coalescing.
- **Streaming fills** give each caller an independent origin stream. Staging memory and temporary disk admission are bounded; completed bodies are uploaded before metadata becomes reusable.
- **Content addressing** permits variants to share stored bodies. This does not imply that concurrent streaming misses share an origin request or upload.
- The default HybridCache body store still materializes byte arrays. External storage avoids full-body allocations during cache fills and ordinary full-body replay; existing range-response construction can still buffer a cached representation. Caller buffering and fixed SDK buffers remain separate costs.

### Efficient Caching

- **L1/L2 Strategy**: Fast in-memory (L1) + optional distributed (L2) via HybridCache
- **Size Limits**: Configurable per-item limits (default: 10MB) prevent memory issues
- **Conditional Requests**: ETags and Last-Modified enable efficient 304 responses

### Benchmark Results

See [benchmark guidance](benchmarks/Benchmarks/README.md) for the buffered-path benchmarks
and `StreamingFillBenchmarks`. The latter generates 1, 32, and 128 MiB bodies without
allocating them up front and drains via `ResponseHeadersRead`, with compression on/off.
Its discard-only content store measures handler staging rather than cloud SDK or network costs.
Reported allocation totals are not measurements of peak live memory or disk usage.

## Benchmarks

Run benchmarks to measure performance:

```bash
dotnet run --project benchmarks/Benchmarks/Benchmarks.csproj -c Release
```

## RFC 9111 Conformance Suite

The handler is tested against [http-tests/cache-tests](https://github.com/http-tests/cache-tests)
(the HTTP caching test suite behind [cache-tests.fyi](https://cache-tests.fyi), used to assess
browsers, proxies and CDNs). The suite's client sends scripted requests through a minimal YARP
reverse proxy that uses `HttpHybridCacheHandler` in `Shared` mode. All three
implementation targets run in both default HybridCache and streaming filesystem
modes. The `net10.0` and explicitly selected `netstandard2.0` assemblies run directly
in .NET 10 hosts. The `net472` assemblies run in a separate .NET Framework worker
behind the same modern front-end, not on modern .NET. Startup checks the loaded
assembly targets and records the actual runtime.

Run it locally:

```powershell
# Windows
.\hybrid-cache-handler\conformance\run-conformance.ps1 -Framework net10.0
.\hybrid-cache-handler\conformance\run-conformance.ps1 -Framework netstandard2.0 -FileSystem
.\hybrid-cache-handler\conformance\run-conformance.ps1 -Framework net472 -FileSystem
```

```bash
# Linux/macOS
./hybrid-cache-handler/conformance/run-conformance.sh --framework netstandard2.0 --file-system
```

The script clones the suite (pinned commit), starts the suite's origin server and the proxy, runs
the full suite and compares `results-<framework>-<default|filesystem>.json` against
the unchanged `expected-results.json` baseline. Missing fixtures and regressions
from previously passing fixtures fail the run. Legacy targets have reviewed,
exact header-formatting exceptions in `legacy-expectations.json`; these do not
inflate raw pass counts or apply to modern runs. Known baseline failures remain
visible. Passing every fixture is not a goal: the suite also measures optional
optimizations, and these runs are not a conformance certification.

Each of the six local Windows runs executed 365 fixtures: 344 raw passes per
modern mode and 324 per legacy mode, with no unaccepted baseline regressions.
Framework execution used .NET Framework 4.8, not an actual 4.7.2 runtime.
HybridCache's upstream `net472` unsupported/untested warning remains applicable.

- Debug a single fixture: `.\run-conformance.ps1 -Framework net472 -TestId <test-id>`
  (diagnostics only, not a full-suite gate).
- After reviewing new passes from a full modern run, use `.\run-conformance.ps1 -Update`
  (or `run-conformance.sh --update`) to ratchet the baseline.

The `cache-conformance` CI matrix covers all six Windows combinations and the four
modern/Standard Linux combinations. Each cell uploads target/mode-qualified
results, runtime/assembly provenance, and build/origin/proxy/client/comparison logs.
Linux execution is configured in CI but was not verified locally.
See the [conformance harness documentation](conformance/README.md) for prerequisites,
IPC fidelity and lifecycle checks, and exact result-gating rules.

## Samples

See the [`/samples`](../../samples) directory for complete examples:

- [`HttpClientFactorySample`](../../samples/HttpClientFactorySample): Integration with IHttpClientFactory
- [`YarpCachingProxySample`](../../samples/YarpCachingProxySample): Building a caching reverse proxy with YARP
- [`FusionCacheSample`](../../samples/FusionCacheSample): Using FusionCache via its HybridCache adapter for enhanced caching features
- [`FileDistributedCacheSample`](../../samples/FileDistributedCacheSample): File-based L2 cache with HttpHybridCacheHandler
