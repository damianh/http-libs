# HttpHybridCacheHandler benchmarks

BenchmarkDotNet workloads for cache hit/miss costs, content separation, LOH
behavior, and streaming fills.

**[Full benchmark guide](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/benchmarks/)**

From this directory:

```powershell
dotnet run -c Release -- --filter "*"
```

Or from the repository root:

```powershell
dotnet run --project hybrid-cache-handler\benchmarks\Benchmarks\Benchmarks.csproj -c Release -- --filter "*"
```

Buffered-path measurements use an in-process L1 cache and fake origin, not real
network or distributed cache. Streaming fills use a discard-only store, not a
cloud provider. `MemoryDiagnoser` measures managed allocations, not peak live
memory, retained memory, native allocations, or spool-disk usage.

Miss methods use single-operation iterations: **do not override invocation count**.
Concurrent results are per batch of 5 or 10, include scheduling/task costs, and
are not cold-cache stampede tests. There are no fixed allocation or Gen2 pass/fail
thresholds. See the full guide for workload selectors, illustrative output,
interpretation, mitigation strategies, and contribution guidance.
