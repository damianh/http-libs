---
title: Filesystem HTTP cache content store
sidebarLabel: File system
description: Local-file body storage, soft age and size retention limits, trusted roots, and single-process ownership.
order: 5
section: HTTP cache content stores
---

# Filesystem HTTP cache content store

`DamianH.HttpHybridCacheHandler.ContentStore.FileSystem` implements
`ILargeHttpCacheContentStore` using local files and bounded (64 KiB) streaming
copies. It depends on the abstractions and Microsoft extensions, not on the
handler or `FileDistributedCache`. It is independently versioned using
`cache-filesystem-v` tags.

Targets are `net10.0`, `netstandard2.0`, and `net472` (.NET Framework 4.7.2+).
Retention uses the supplied `TimeProvider` on every target, including fake clocks.
Older targets use a coalescing periodic timer based on `TimeProvider.CreateTimer`;
disposal cancels and joins cleanup just as on .NET 10.

[NuGet](https://www.nuget.org/packages/DamianH.HttpHybridCacheHandler.ContentStore.FileSystem/) |
[Source](https://github.com/damianh/http-libs/tree/main/hybrid-cache-handler/src/HttpHybridCacheHandler.ContentStore.FileSystem)

## Registration with bounded retention

Install in the application project directory:

```powershell
dotnet add package DamianH.HttpHybridCacheHandler.ContentStore.FileSystem
```

```csharp
using DamianH.HttpHybridCacheHandler.ContentStore.FileSystem;
using System.Diagnostics;

using var process = Process.GetCurrentProcess();
var processId = process.Id.ToString();
services.AddHttpHybridCacheFileSystemContentStore(options =>
{
    // Supply an absolute, private, dedicated directory for THIS process.
    options.RootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyApp", "http-bodies", processId);
    options.MaximumAge = TimeSpan.FromDays(7);
    options.MaximumTotalBytes = 10L * 1024 * 1024 * 1024; // soft 10 GiB
    options.CleanupInterval = TimeSpan.FromMinutes(5);
});
```

Configure the [handler's large-body offload](/docs/hybrid-cache-handler/content-stores/overview/)
separately. This helper registers the large-store contract directly; only one
backend per handler setup is supported.
Register `TimeProvider` and logging normally to override the default clock and
enable diagnostics. The service provider owns and disposes the singleton.

**Both retention limits default to unset: there is no automatic body expiry or
quota unless you configure a limit.** The interval defaults to five minutes.
Age is measured since successful publication (file last-write time), not since
last access, HTTP freshness, or metadata revalidation. Quota cleanup removes the
oldest bodies first. Limits are soft: a write can exceed the quota until cleanup;
active readers can retain disk blocks after logical eviction. Retention may
remove referenced content, which must then be treated as a cache miss.

## Filesystem and ownership contract

- Use **one store instance in one process per root**. Shared roots across
  processes, network filesystems, and distributed locking are not supported.
  Use a stable private per-worker root if you need cache reuse and abandoned
  staging cleanup across process restarts. PID-based example roots require
  operator cleanup of directories left by previous processes.
- The configured directory and every ancestor must be trusted and protected
  against external modification. Symlinks/junctions/reparse points in the path
  or entry are rejected. These checks are not a security sandbox against an
  attacker concurrently replacing directory entries or injecting hard links.
  Apply OS permissions; do not share the root with untrusted applications.
- Keys are SHA-256 hashed into `http-cache-content-v1/hhc-<hash>.body`.
  Raw keys never become paths. Only precisely named adapter-owned body/temp
  files in this namespace are considered for cleanup. Unrelated files and
  subdirectories are not recursively deleted.
- Writes accept a caller-owned readable, seekable stream at the start of exactly
  `contentLength` remaining bytes. No whole-body byte array is allocated.
  Cancellation/failure never publishes incomplete data and leaves existing
  content intact. A same-directory temporary file is closed before atomic
  replacement. Local atomic rename semantics are required; crash/power-loss
  durability is not promised.
- A process-wide operation gate coordinates writers, opens, removals, and
  cleanup. Writes are serialized (including different keys). Returned independent
  readers hold no gate, and use Windows `FileShare.Delete`; replacement, eviction,
  removal, or store disposal leaves existing readers usable until they close.
- Abandoned owned temporary files are cleaned at startup, and on enabled cleanup
  ticks, independently of body retention. The gate prevents deletion of active
  staging files. Synchronous and async disposal cancel and join background
  cleanup and active writes. Callers must provide streams honoring cancellation.
- Missing bodies alone return null; missing roots, access denial and I/O errors
  propagate and are logged. Recognized write failures are wrapped in
  `HttpCacheContentStoreException` with their original cause. Expected cleanup
  failures are logged and retried on later ticks; programming errors are not
  silently swallowed.
- Removal is idempotent for one key, never recursive. Tags are advisory, ignored,
  and not stored. Do not eagerly remove shared bodies when invalidating metadata.

This body store is distinct from [FileDistributedCache](/docs/file-distributed-cache/overview/),
which implements an L2 cache rather than the large-body store contract.
