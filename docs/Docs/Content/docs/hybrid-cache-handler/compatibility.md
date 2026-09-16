---
title: HTTP cache framework compatibility
sidebarLabel: Framework compatibility
description: Supported package assets, older-target header behavior, dependency support warnings, and compatibility validation.
order: 9
section: HTTP hybrid caching
topics:
  - .NET Standard
  - .NET Framework
---

# HTTP cache framework compatibility

All six HTTP cache packages ship `net10.0`, `netstandard2.0`, and `net472` assets:
the handler, ContentStore contracts, and the Azure Blob, S3, Google Cloud Storage,
and filesystem adapters. The .NET Standard build is a library asset, not an
executable runtime. The existing samples, benchmarks, and YARP conformance
front-end remain .NET 10 applications. The separate
[FileDistributedCache](/docs/file-distributed-cache/overview/) product is not
part of this target expansion.

NuGet restores target-appropriate dependencies, including support packages for
async interfaces, `TimeProvider`, memory, and diagnostics where required.
.NET Framework applications should enable automatic binding redirect generation.
Cloud adapters retain their existing SDK versions; check the SDK vendors'
support policies as well as the library target.

**Upstream dependency warning:** `Microsoft.Extensions.Caching.Hybrid` 10.8.0
contains .NET Standard 2.0 and Framework-compatible assemblies, but its
`buildTransitive` targets explicitly warn that `net472` is unsupported and
untested by that package and recommend .NET 8 or later. The warning is not
suppressed. Downstream compatibility coverage does not change Microsoft's
support commitment. The ContentStore contracts themselves do not depend on
HybridCache.

Use the [runtime-specific handler setup](/docs/hybrid-cache-handler/pipeline/):
`SocketsHttpHandler` on .NET 10, or `HttpClientHandler` with GZip/Deflate
decompression on Framework. Modern pooling options and `DecompressionMethods.All`
are not available on Framework. Returned content-store streams can be disposed
with `using` on all targets; for async disposal on older targets, cast to
`IAsyncDisposable`.

## Header representation

- `net10.0` retains the `HttpHeaders.NonValidated` snapshot path.
- `netstandard2.0` and `net472` use supported public header enumeration,
  preserving the values and value boundaries it exposes. No reflection,
  private runtime fields, serialized-header reparsing, or comma-joining
  workaround is used.
- Older-target builds can normalize casing, whitespace, dates, ETags, and
  parsed value grouping, including duplicate parsed directives. Exact original
  header representation is not guaranteed. The `netstandard2.0` build uses this
  path **even when loaded on .NET 10**.
- Standards-compliant freshness, privacy/no-store protection, validators, Vary,
  invalidation, and streaming publication rules are intended to match.
  Malformed headers may be rejected more conservatively. For example,
  Framework does not normalize an `Expires` date ending in `UTC` to the
  required `GMT`, so the strict date parser rejects it. Without another
  freshness rule, a future malformed date does not make a response fresh.

For older-target disk-spooling platform requirements and Windows parent ACL
responsibilities, see
[temporary staging](/docs/hybrid-cache-handler/content-stores/overview/#temporary-staging).

## Compatibility validation

The dedicated compatibility suite runs the `net10.0` baseline, explicitly forced
`netstandard2.0` library assets on a .NET 10 host on Windows/Linux, and `net472`
executables on Windows. It asserts `TargetFrameworkAttribute` on all six loaded
assemblies rather than assuming a modern test host selected older assets.
Tests use an in-process origin and no new live cloud credentials.

The default Microsoft HybridCache implementation and default serializers are
also exercised across independent DI containers sharing a
`MemoryDistributedCache` backend. These cases require actual L2 metadata/body
reads, including Vary, validators, header-value boundaries, Age, and
compressed/uncompressed content. They do not substitute a test HybridCache
or custom metadata serializer.

From the repository root:

```powershell
dotnet run hybrid-cache-handler/build.cs -- test-compatibility-modern
dotnet run hybrid-cache-handler/build.cs -- test-compatibility-standard
# Windows only
dotnet run hybrid-cache-handler/build.cs -- test-compatibility-framework
# All six packages: asset/dependency checks and package-only consumers
dotnet run hybrid-cache-handler/build.cs -- check-packages
```

Framework test executables target 4.7.2, but Windows runners generally have
an in-place .NET Framework 4.8 runtime. The suite reports the installed runtime;
this is not a claim that an actual 4.7.2 runtime was tested. Package checks build
all three consumer targets, run .NET 10 consumers on Windows/Linux and Framework
consumers on Windows, and inspect selected compile/runtime package assets.
The .NET Standard consumer is build-only; the forced-asset test host provides
its executable behavioral coverage.

The [RFC conformance matrix](/docs/hybrid-cache-handler/conformance/) also
exercises all three implementations in default HybridCache and filesystem modes.

## Package dependency floor

Development ContentStore prereleases can sort below the deliberately released
dependency floor (`0.1.0`). In that case, the package checker repacks the identical
contract binaries at the floor in a disposable, private feed for consumer testing.
It does not rewrite original release artifacts or suppress downgrade errors.
Release ContentStore at the declared floor before releasing its consumers.
