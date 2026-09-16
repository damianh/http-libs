---
title: HTTP cache performance and memory
sidebarLabel: Performance
description: Content and metadata separation, request coalescing, streaming memory bounds, and benchmark limitations.
order: 6
section: HTTP hybrid caching
---

# HTTP cache performance and memory

## Content and metadata separation

The architecture avoids Base64 overhead in distributed cache:

- **Metadata** (small, approximately 1-2 KB): Status, headers, timestamps, and
  variant metadata stored as JSON.
- **Content** (variable): Separate bytes in HybridCache, or a streamed object in
  the external store.
  - No Base64 encoding avoids its approximately 33% expansion over raw bytes.
  - SHA256 content hashing enables deduplication.
  - Identical content can be shared across cache variants with different `Vary`
    selections.

Trade-offs:

- Two cache lookups, metadata and content, rather than one.
- L1 memory makes the second lookup fast (the original guidance described
  microsecond-scale access, not an isolated measured baseline).
- All cached content avoids Base64 encoding overhead.

## Memory efficiency

- **Buffered fills** use HybridCache request coalescing.
- **Streaming fills** give each caller an independent origin stream. Staging
  memory and temporary disk admission are bounded. Completed bodies are uploaded
  before metadata becomes reusable.
- **Content addressing** permits variants to share bodies. Concurrent streaming
  misses do not thereby share an origin request or upload.
- The default HybridCache body store still materializes byte arrays. External
  storage avoids full-body allocations during fills and ordinary full-body
  replay; existing range-response construction can still buffer a cached
  representation. Caller buffering and fixed SDK buffers remain separate costs.

See [streaming, spooling, and retention](/docs/hybrid-cache-handler/content-stores/overview/)
for defaults, admission behavior, and response ownership.

## Efficient caching

- **L1/L2 strategy**: Fast in-memory L1 and optional distributed L2 via HybridCache.
- **Size limits**: Configurable per-item limits (default 10 MB) help limit memory use.
- **Conditional requests**: ETags and Last-Modified enable efficient 304 responses.

## Benchmark guidance

The [benchmark guide](/docs/hybrid-cache-handler/benchmarks/) covers buffered-path
benchmarks and `StreamingFillBenchmarks`. The latter generates 1, 32, and 128 MiB
bodies without upfront allocation and drains them via `ResponseHeadersRead`,
with compression on and off. Its discard-only content store measures handler
staging rather than cloud SDK or network costs. Reported allocations are not
measurements of peak live memory or disk usage.
