# HTTP cache content-store contracts

`DamianH.HttpHybridCacheHandler.ContentStore` provides provider-independent contracts for
`DamianH.HttpHybridCacheHandler` and its content-store adapters.
No cloud SDK or HybridCache dependency is required to implement a store.

## Frameworks

This package and all five consuming HTTP cache packages (the handler and four
adapters) ship `net10.0`, `netstandard2.0`, and `net472` assets. Older-target
contracts use NuGet support packages for the public `ValueTask`/async-interface
types where required; let NuGet restore these transitive dependencies and enable
automatic binding redirects in Framework applications.

Compatibility tests assert the selected assemblies for all six packages, including
an explicitly forced .NET Standard asset on a .NET 10 host. Framework executables
run on Windows's installed runtime, commonly .NET Framework 4.8; targeting `net472`
does not establish testing on an actual 4.7.2 installation.

The contracts themselves have no HybridCache dependency. Consumers using the
handler should note that its current `Microsoft.Extensions.Caching.Hybrid` 10.8.0
dependency includes older-target assemblies but emits an upstream
unsupported/untested `net472` warning. That warning is not suppressed, and
downstream coverage does not alter upstream support. See the handler's
[framework compatibility documentation](https://github.com/damianh/http-lib/tree/main/hybrid-cache-handler#framework-compatibility)
for the public-enumeration header contract and target-specific handler examples.

## Storage contract

`IHttpCacheContentStore` provides complete streaming writes, independently owned read streams,
and single-key removal. `ILargeHttpCacheContentStore` identifies the optional external body store.
The handler retains responsibility for HTTP freshness, variant selection, and metadata invalidation.
Storage retention is separate from HTTP freshness.

Recognized SDK write failures are propagated as `HttpCacheContentStoreException`
(an `IOException`) with the original provider exception attached. Cancellation and
programming errors are not converted. This lets the handler log and abandon a failed
cache fill without depending on cloud SDK exception types or corrupting the origin
response. Missing read results alone return null; other read failures propagate.

This package is versioned independently using `cache-contentstore-v` tags.
