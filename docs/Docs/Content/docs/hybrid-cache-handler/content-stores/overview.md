---
title: Cache content-store contracts and offload
sidebarLabel: Contracts and ownership
description: Independent streaming contracts, large-body routing, staging budgets, retention, and package migration.
order: 1
section: HTTP cache content stores
---

# Cache content-store contracts and offload

Metadata and HTTP freshness remain in HybridCache. Choose one separately packaged
body store:

| Package | Backend | Guide |
| --- | --- | --- |
| `DamianH.HttpHybridCacheHandler.ContentStore` | Provider-independent streaming contracts, no cloud SDK dependency | This page |
| `DamianH.HttpHybridCacheHandler.ContentStore.AzureBlob` | Official `Azure.Storage.Blobs` SDK | [Azure Blob](/docs/hybrid-cache-handler/content-stores/azure-blob/) |
| `DamianH.HttpHybridCacheHandler.ContentStore.S3` | Official `AWSSDK.S3` SDK | [Amazon S3](/docs/hybrid-cache-handler/content-stores/s3/) |
| `DamianH.HttpHybridCacheHandler.ContentStore.GoogleCloudStorage` | Official `Google.Cloud.Storage.V1` SDK | [Google Cloud Storage](/docs/hybrid-cache-handler/content-stores/google-cloud-storage/) |
| `DamianH.HttpHybridCacheHandler.ContentStore.FileSystem` | Native file streams, no cloud SDK | [Filesystem](/docs/hybrid-cache-handler/content-stores/file-system/) |

Adapters depend on ContentStore, not on the handler or HybridCache. Configure
credentials, endpoints, and retries through an injected SDK client. Registration
never provisions cloud resources or modifies lifecycle policies. Stowage and
FluentStorage are not dependencies.

## Independent contracts

`DamianH.HttpHybridCacheHandler.ContentStore` provides provider-independent
contracts for the handler and adapters. No cloud SDK or HybridCache dependency
is required to implement a store.

The contracts and all five consuming HTTP cache packages ship `net10.0`,
`netstandard2.0`, and `net472` assets. Let NuGet restore the older-target async
support packages and enable automatic binding redirects in Framework
applications. See [framework compatibility](/docs/hybrid-cache-handler/compatibility/)
for tested runtimes, header representation, and the handler's upstream
HybridCache support warning; the contracts themselves do not depend on HybridCache.

Install in the application or adapter project directory:

```powershell
dotnet add package DamianH.HttpHybridCacheHandler.ContentStore
```

[NuGet](https://www.nuget.org/packages/DamianH.HttpHybridCacheHandler.ContentStore/) |
[Contract source](https://github.com/damianh/http-libs/tree/main/hybrid-cache-handler/src/HttpHybridCacheHandler.ContentStore)

`IHttpCacheContentStore` provides complete streaming writes, independently owned
read streams, and single-key removal. `ILargeHttpCacheContentStore` identifies the
optional external body store. The handler retains responsibility for HTTP
freshness, variant selection, and metadata invalidation. Storage retention is
separate from HTTP freshness.

Recognized SDK write failures propagate as `HttpCacheContentStoreException`
(an `IOException`) with the original provider exception attached. Cancellation
and programming errors are not converted. This lets the handler log and abandon
a failed fill without depending on cloud SDK exceptions or corrupting the origin
response. Only missing reads return null; other read failures propagate.

## Routing and streaming

`LargeContentThreshold` uses the original response-body length before internal
compression. `MaxCacheableContentSize` still determines whether a body can be
cached at all. Without an external store, the HybridCache-only path remains
available. See [option defaults](/docs/hybrid-cache-handler/configuration/).

With external storage enabled, cacheable origin bodies can stream to the caller
while the handler stages a copy in bounded memory and temporary files. A completed
body is uploaded before metadata is published. Early disposal, incomplete
responses, or cancellation do not populate the cache. Admission limits must not
truncate the origin response.

Use `HttpCompletionOption.ResponseHeadersRead` and consume/dispose the response
stream to benefit from streaming. The default `ResponseContentRead`,
`ReadAsStringAsync`, and `ReadAsByteArrayAsync` can still buffer the entire
response in the caller. Cold streaming requests do not share a live response
stream; simultaneous misses can make independent origin requests. Completing
consumption can include cache upload latency.

## Temporary staging

Temporary spool storage and persistent body retention are separate concerns.
Configure handler staging independently of the body store:

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

Defaults are 64 KiB staging memory per spool, a 1 GiB aggregate active disk
budget, 32 concurrent disk spools, and the system temporary directory.
Compression may need a second spool, counted against the same disk limits.
Reservations are process-wide; use consistent limits across handlers. Exhaustion
abandons caching while origin delivery continues. Limits exclude caller buffers,
fixed transfer buffers, provider SDK buffers, and persistent body storage.

Use a trusted private staging parent. Provisioning it is an application/deployment
responsibility, including when `SpoolDirectory` is null and uses `Path.GetTempPath()`.
Verify access for the process account rather than assuming the default directory
is private. On Windows, provision the parent with inheritable ACLs restricting
access to trusted principals. The handler inherits those ACLs; it does not
restrict or validate them. A shared/public parent is not made private by the
handler. Unique names and leases manage lifetime, not access control.

Each spill owns a unique leased directory;
cleanup releases completed spools and can reclaim abandoned leased directories
without deleting a live owner's files. HTTP cache metadata remains the caller's
HybridCache configuration responsibility.

On Linux and macOS, the `netstandard2.0` and `net472` builds create private staging
directories through native POSIX `mkdir` (`libc`), with a UTF-8 path and owner-only
mode `0700`. The return code is checked: there is no weaker-permission fallback
or narrowing of permissions after writing response data. This OS call does not
inspect runtime internals. The `net10.0` build uses managed
`Directory.CreateDirectory` with `UnixFileMode`. Windows uses the managed API
and the trusted parent's ACLs. Other operating systems are unsupported for
older-target disk spooling. These are platform requirements, not claims of
completed macOS or actual Framework 4.7.2 runtime testing.

## Retention

Configure cloud lifecycle policies for cached objects and incomplete uploads, and
age/size cleanup for filesystem storage. Retention is not HTTP freshness: deletion
becomes a cache miss even when metadata is fresh. Revalidation does not necessarily
refresh object creation time. Do not delete shared content-addressed bodies merely
because one URL or variant was invalidated. Provider guides document their
different incomplete-upload behavior and retention limitations.

## Versioning, ownership, and migration

The handler, ContentStore, and each adapter have independent versions and release
tags. ContentStore uses `cache-contentstore-v`. Release compatible ContentStore
versions before consumers; SDK updates need not force a handler release.

The interfaces retain their namespace but now live in the ContentStore assembly.
Implementations migrate from a materialized sequence write to a seekable,
caller-owned input stream with an explicit stored length. The adapter must leave
the input open and finish consuming it before returning. Returned read streams
are owned by the response/caller. This is a breaking change from the initial
pre-release Stowage implementation, **not a binary-compatible replacement**.

Build targets are `pack-handler`, `pack-contentstore`, `pack-azureblob`, `pack-s3`,
`pack-gcs`, and `pack-filesystem`; `pack-all` creates the local bundle. The release
workflow selects one package and publishes only its exact artifact. Consumers
declare the compatible ContentStore dependency floor through
`HttpCacheContentStorePackageVersion` (initially `0.1.0`), rather than leaking
another project's computed prerelease version.
