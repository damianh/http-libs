# Filesystem HTTP cache content store

`DamianH.HttpHybridCacheHandler.ContentStore.FileSystem` stores large HTTP cache
bodies using local files and bounded 64 KiB streaming copies. It is independent
of the handler and FileDistributedCache, with `cache-filesystem-v` release tags.

Targets are `net10.0`, `netstandard2.0`, and `net472`. Retention uses the supplied
`TimeProvider` on every target, including fake clocks. Legacy cleanup uses a
coalescing timer and is cancelled and joined on disposal.

**[Full filesystem guide](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/content-stores/file-system/)**

Install from your application project directory:

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
    options.RootDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyApp", "http-bodies", processId);
    options.MaximumAge = TimeSpan.FromDays(7);
    options.MaximumTotalBytes = 10L * 1024 * 1024 * 1024;
});
```

Configure handler offload separately and only one backend. Use **one instance in
one process per trusted private root**. Network/shared filesystems and distributed
locking are unsupported. Protect every ancestor with OS permissions; reparse-point
checks are not a security sandbox. PID-based roots need operator cleanup on restart.

Both retention limits default to unset; the example explicitly enables soft limits.
Cleanup defaults to five minutes. Retention is not HTTP freshness. Do not eagerly
delete shared bodies. Writes require seekable caller-owned input and exact remaining
length. The provider owns the singleton; callers must dispose returned independent
readers. Local atomic rename is required; crash/power-loss durability is not promised.
See the full guide for cancellation, serialized writes, cleanup, and error semantics.
