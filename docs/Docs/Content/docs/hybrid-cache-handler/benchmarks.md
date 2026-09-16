---
title: HTTP cache benchmarks
sidebarLabel: Benchmarks
description: Benchmark workloads, reproducible run commands, allocation interpretation, and measurement limitations.
order: 8
section: HTTP hybrid caching
---

# HTTP cache benchmarks

The [benchmark project](https://github.com/damianh/http-libs/tree/main/hybrid-cache-handler/benchmarks/Benchmarks)
analyzes the performance and memory allocation characteristics of
`HttpHybridCacheHandler`.

## Measurement scope

The buffered-path benchmarks are in-process L1 benchmarks with no network or
distributed cache. The fake origin shares preallocated, highly compressible byte
payloads. Hit entries are primed and verified in `GlobalSetup`. Requests use
`ResponseHeadersRead` and copy the body directly to `Stream.Null`, without
caller-side buffering or string conversion. Requests explicitly created by the
benchmarks, responses, clients, and service providers are disposed.

Miss benchmarks rebuild their cache in targeted `IterationSetup`, which makes
BenchmarkDotNet default to one invocation with no unrolling for those methods.
Cleanup verifies one benchmark invocation, one origin call, and a subsequent
cache hit. The invocation check rejects iterations mixing a miss with later hits,
even if the origin count is still one. This bounds retained cache state rather
than accumulating unique keys.
Do not override invocation count for these methods. Single-operation iterations
can produce noisy timing and allocation measurements; they are not steady-state
throughput tests.

Concurrent benchmarks dispatch each request with `Task.Run`. Their results include
thread-pool scheduling and task allocations, and are **per batch** (5 or 10 requests),
not per request. Hot hits may complete synchronously, so simply collecting async
calls in a loop would not exercise concurrent callers. These are not cold-cache
stampede tests.

The Vary benchmark measures retrieval of a primed variant with shared content,
not retained-memory savings from deduplication. No single-lookup control exists,
so these benchmarks do not isolate the cost of the second lookup.

`MemoryDiagnoser` reports managed allocations per operation and collections per
1,000 operations. It does not measure working set, peak/retained memory, native
allocations, or prove the absence of LOH objects when Gen2 is zero. Previous reports
using buffered reads or priming inside the measured method are not comparable.

## Run benchmarks

All commands below run from the repository root.

### All benchmarks

```powershell
dotnet run --project hybrid-cache-handler\benchmarks\Benchmarks\Benchmarks.csproj -c Release -- --filter "*"
```

### Specific benchmark classes

```powershell
dotnet run --project hybrid-cache-handler\benchmarks\Benchmarks\Benchmarks.csproj -c Release -- --filter "*MemoryAllocationBenchmarks*"
dotnet run --project hybrid-cache-handler\benchmarks\Benchmarks\Benchmarks.csproj -c Release -- --filter "*ContentSeparationBenchmarks*"
dotnet run --project hybrid-cache-handler\benchmarks\Benchmarks\Benchmarks.csproj -c Release -- --filter "*LohBenchmarks*"
```

### Original benchmarks

```powershell
dotnet run --project hybrid-cache-handler\benchmarks\Benchmarks\Benchmarks.csproj -c Release -- --filter "*CachingBenchmarks*"
```

To use BenchmarkDotNet's interactive selection instead, omit `-- --filter "*"`
from the all-benchmarks command.

## Benchmark categories

### StreamingFillBenchmarks

```powershell
dotnet run --project hybrid-cache-handler\benchmarks\Benchmarks\Benchmarks.csproj -c Release -- --filter "*StreamingFillBenchmarks*"
```

Generates 1, 32, and 128 MiB responses without a preallocated body, requests
headers-first, and drains to `Stream.Null`. Runs with internal compression on
and off. A discard-only external store deliberately returns misses: this isolates
handler staging and upload streaming from SDK allocation/network costs and
persistent body retention.

Compare allocation growth, throughput, and collections across sizes. Total
allocations can grow with the number of asynchronous reads even when live
buffers remain bounded; `MemoryDiagnoser` does not measure peak working set or
spool-disk usage. These benchmarks do not establish real-cloud interoperability
or cloud-provider memory bounds.

### MemoryAllocationBenchmarks

**Focus**: Memory allocation patterns across different response sizes.

**Key metrics:**

- `Allocated`: Total memory allocated per operation.
- `Gen0`: Minor GC collections (young generation).
- `Gen1`: Intermediate GC collections.
- `Gen2`: Full GC collections (LOH pressure can contribute).

**Workloads:**

- Cache miss (initial store) allocations for various sizes.
- Cache hit (retrieval) allocations.
- Batched concurrent hits.
- Impact of response size on memory pressure.

**Expected behavior:**

- Small responses generally create less allocation pressure than large responses.
- Large contiguous allocations can reach the LOH.
- Compression reduces stored size but adds decompression allocations on hits.

### ContentSeparationBenchmarks

**Focus**: Overhead and benefits of content/metadata separation.

**Key metrics:**

- End-to-end hit latency with two lookups (metadata and content).
- Allocations for small versus large responses.
- Vary-variant retrieval with shared content.

**Workloads:**

- Hit cost for different response sizes.
- Retrieval of primed variants sharing content.
- Concurrent access patterns with separated storage.

**Expected behavior:**

- Compare hit costs across sizes; a separate control is needed to isolate lookup overhead.
- Deduplication savings require retained-memory measurements, not allocations per hit.
- Concurrent hot hits do not exercise cold-cache stampede protection.

### LohBenchmarks

**Focus**: Large Object Heap (LOH) behavior around the default 85,000-byte object threshold.

**Key metrics:**

- Gen2 collections, not a direct count of LOH allocations.
- Allocated memory for responses around the LOH threshold.
- Request-path allocation cost with and without compression.

**Workloads:**

- 80 KiB (81,920 bytes): Payload below threshold.
- 85 KiB (87,040 bytes): Already above threshold, not an exact boundary test.
- 100 KiB+: Large contiguous payload arrays can reach LOH.
- Compression: Stored bytes may shrink while decompressed output still reaches LOH.

**Expected behavior:** Object headers, intermediate buffers, and pool bucket sizes
also affect LOH placement. Gen2 counts depend on GC activity over the run, not just
payload size.

## Interpret results

### Memory allocation numbers

Illustrative output, **not a measured baseline**:

```text
|                  Method |  Mean |     Allocated |  Gen0 | Gen1 | Gen2 |
|------------------------ |------:|--------------:|------:|-----:|-----:|
| SmallResponse_1KB       | 50us  |      5.2 KB   |  0.01 |    - |    - |
| LargeResponse_100KB     | 150us |    102.4 KB   |  0.05 |    - | 0.01 |
```

**Good signs:**

- Low `Allocated` values for hits.
- Lower allocations for comparable hit workloads.

**Expected behavior:**

- Large contiguous arrays can allocate on LOH.
- Compressed hits can allocate more than misses because of decompression.

**Concerning signs:**

- Excessive allocations on hits.
- Regressions against the same workload on the same runtime and machine.

### LOH mitigation strategies

If LOH becomes a problem, such as frequent Gen2 collections or memory fragmentation:

1. **Lower MaxCacheableContentSize:**

   ```csharp
   MaxCacheableContentSize = 80 * 1024 // Limit payload size, not all intermediate allocations
   ```

2. **Compression for stored-size reduction:**

   ```csharp
   CompressionThreshold = 512 // Compress smaller responses
   ```

   This can increase request-path allocation pressure during decompression.

3. **Content-type filtering:**

   ```csharp
   CacheableContentTypes = ["application/json", "text/*"] // Only cache compressible types
   ```

## Architecture considerations

### Why LOH usage can be acceptable

The original SOA/distributed-system design rationale prioritizes reliability:

- Caching large responses reduces load on target systems.
- Large responses may be infrequent; the original rationale assumed most API
  calls were small (under 10 KB), not a measured guarantee for your workload.
- Gen2 collection cost can be an acceptable trade-off for system reliability.
- Text-based responses compress well.

### Content and metadata separation benefits

1. **Zero Base64 overhead**: Raw bytes without Base64 expansion.
2. **Content deduplication**: Same content hash shared across entries.
3. **Efficient 304 updates**: Only metadata changes; content is untouched.

### Accepted trade-offs

- Separate metadata and content lookups per hit.
- LOH for large responses, acceptable for the design's reliability goals.
- Compression trades CPU and temporary allocations for smaller stored content.

## Baseline expectations

Establish baselines from the current suite on the target runtime and machine.
There are no fixed allocation or Gen2 pass/fail thresholds. Compression trades
stored bytes for CPU and temporary allocations; it is not a guarantee of lower
request-path memory use.

## Continuous monitoring

Run these benchmarks:

- Before major architectural changes.
- When adding new caching features.
- If production shows memory pressure.
- To validate LOH mitigation strategies.

## Contributing

When adding benchmarks:

1. Use `[MemoryDiagnoser]`.
2. Document what is tested and why.
3. Include expected behavior in comments.
4. Consider both memory and performance metrics.
