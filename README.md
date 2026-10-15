# http-libs

[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![GitHub Stars](https://img.shields.io/github/stars/damianh/http-libs.svg)](https://github.com/damianh/http-libs/stargazers)

A collection of .NET libraries for HTTP caching, structured field values, message signatures, and trusted-proxy forwarding.

**[Documentation](https://damianh.github.io/http-libs/)** ·
[Get started](https://damianh.github.io/http-libs/docs/getting-started/) ·
[Contributing](https://damianh.github.io/http-libs/docs/contributing/)

## Packages

| Package | Badges |
|---------|--------|
| **[DamianH.Http.HybridCacheHandler](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/overview/)**<br>RFC 9111 client-side HTTP caching handler for `HttpClient` | [![CI](https://github.com/damianh/http-libs/actions/workflows/hybrid-cache-handler-ci.yml/badge.svg)](https://github.com/damianh/http-libs/actions/workflows/hybrid-cache-handler-ci.yml)<br>[![NuGet](https://img.shields.io/nuget/v/DamianH.Http.HybridCacheHandler.svg)](https://www.nuget.org/packages/DamianH.Http.HybridCacheHandler/)<br>[![Downloads](https://img.shields.io/nuget/dt/DamianH.Http.HybridCacheHandler.svg)](https://www.nuget.org/packages/DamianH.Http.HybridCacheHandler/) |
| **[DamianH.Http.HybridCacheHandler.ContentStore](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/content-stores/overview/)**<br>Provider-independent streaming contracts for HTTP cache content stores | [![CI](https://github.com/damianh/http-libs/actions/workflows/hybrid-cache-handler-ci.yml/badge.svg)](https://github.com/damianh/http-libs/actions/workflows/hybrid-cache-handler-ci.yml)<br>[![NuGet](https://img.shields.io/nuget/v/DamianH.Http.HybridCacheHandler.ContentStore.svg)](https://www.nuget.org/packages/DamianH.Http.HybridCacheHandler.ContentStore/)<br>[![Downloads](https://img.shields.io/nuget/dt/DamianH.Http.HybridCacheHandler.ContentStore.svg)](https://www.nuget.org/packages/DamianH.Http.HybridCacheHandler.ContentStore/) |
| **[DamianH.Http.HybridCacheHandler.ContentStore.AzureBlob](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/content-stores/azure-blob/)**<br>HTTP cache body storage using Azure Blob Storage and the official Azure SDK | [![CI](https://github.com/damianh/http-libs/actions/workflows/hybrid-cache-handler-ci.yml/badge.svg)](https://github.com/damianh/http-libs/actions/workflows/hybrid-cache-handler-ci.yml)<br>[![NuGet](https://img.shields.io/nuget/v/DamianH.Http.HybridCacheHandler.ContentStore.AzureBlob.svg)](https://www.nuget.org/packages/DamianH.Http.HybridCacheHandler.ContentStore.AzureBlob/)<br>[![Downloads](https://img.shields.io/nuget/dt/DamianH.Http.HybridCacheHandler.ContentStore.AzureBlob.svg)](https://www.nuget.org/packages/DamianH.Http.HybridCacheHandler.ContentStore.AzureBlob/) |
| **[DamianH.Http.HybridCacheHandler.ContentStore.S3](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/content-stores/s3/)**<br>HTTP cache body storage using Amazon S3 and the official AWS SDK | [![CI](https://github.com/damianh/http-libs/actions/workflows/hybrid-cache-handler-ci.yml/badge.svg)](https://github.com/damianh/http-libs/actions/workflows/hybrid-cache-handler-ci.yml)<br>[![NuGet](https://img.shields.io/nuget/v/DamianH.Http.HybridCacheHandler.ContentStore.S3.svg)](https://www.nuget.org/packages/DamianH.Http.HybridCacheHandler.ContentStore.S3/)<br>[![Downloads](https://img.shields.io/nuget/dt/DamianH.Http.HybridCacheHandler.ContentStore.S3.svg)](https://www.nuget.org/packages/DamianH.Http.HybridCacheHandler.ContentStore.S3/) |
| **[DamianH.Http.HybridCacheHandler.ContentStore.GoogleCloudStorage](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/content-stores/google-cloud-storage/)**<br>HTTP cache body storage using Google Cloud Storage and the official Google SDK | [![CI](https://github.com/damianh/http-libs/actions/workflows/hybrid-cache-handler-ci.yml/badge.svg)](https://github.com/damianh/http-libs/actions/workflows/hybrid-cache-handler-ci.yml)<br>[![NuGet](https://img.shields.io/nuget/v/DamianH.Http.HybridCacheHandler.ContentStore.GoogleCloudStorage.svg)](https://www.nuget.org/packages/DamianH.Http.HybridCacheHandler.ContentStore.GoogleCloudStorage/)<br>[![Downloads](https://img.shields.io/nuget/dt/DamianH.Http.HybridCacheHandler.ContentStore.GoogleCloudStorage.svg)](https://www.nuget.org/packages/DamianH.Http.HybridCacheHandler.ContentStore.GoogleCloudStorage/) |
| **[DamianH.Http.HybridCacheHandler.ContentStore.FileSystem](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/content-stores/file-system/)**<br>HTTP cache body storage using local files | [![CI](https://github.com/damianh/http-libs/actions/workflows/hybrid-cache-handler-ci.yml/badge.svg)](https://github.com/damianh/http-libs/actions/workflows/hybrid-cache-handler-ci.yml)<br>[![NuGet](https://img.shields.io/nuget/v/DamianH.Http.HybridCacheHandler.ContentStore.FileSystem.svg)](https://www.nuget.org/packages/DamianH.Http.HybridCacheHandler.ContentStore.FileSystem/)<br>[![Downloads](https://img.shields.io/nuget/dt/DamianH.Http.HybridCacheHandler.ContentStore.FileSystem.svg)](https://www.nuget.org/packages/DamianH.Http.HybridCacheHandler.ContentStore.FileSystem/) |
| **[DamianH.FileDistributedCache](https://damianh.github.io/http-libs/docs/file-distributed-cache/overview/)**<br>Local-file `IDistributedCache` / `IBufferDistributedCache` for single-process persistent caching | [![CI](https://github.com/damianh/http-libs/actions/workflows/file-distributed-cache-ci.yml/badge.svg)](https://github.com/damianh/http-libs/actions/workflows/file-distributed-cache-ci.yml)<br>[![NuGet](https://img.shields.io/nuget/v/DamianH.FileDistributedCache.svg)](https://www.nuget.org/packages/DamianH.FileDistributedCache/)<br>[![Downloads](https://img.shields.io/nuget/dt/DamianH.FileDistributedCache.svg)](https://www.nuget.org/packages/DamianH.FileDistributedCache/) |
| **[DamianH.Http.StructuredFieldValues](https://damianh.github.io/http-libs/docs/structured-field-values/overview/)**<br>RFC 8941/9651 parser, serializer, and POCO mapper for HTTP Structured Field Values | [![CI](https://github.com/damianh/http-libs/actions/workflows/structured-field-values-ci.yml/badge.svg)](https://github.com/damianh/http-libs/actions/workflows/structured-field-values-ci.yml)<br>[![NuGet](https://img.shields.io/nuget/v/DamianH.Http.StructuredFieldValues.svg)](https://www.nuget.org/packages/DamianH.Http.StructuredFieldValues/)<br>[![Downloads](https://img.shields.io/nuget/dt/DamianH.Http.StructuredFieldValues.svg)](https://www.nuget.org/packages/DamianH.Http.StructuredFieldValues/) |
| **[DamianH.Http.HttpSignatures](https://damianh.github.io/http-libs/docs/signatures/overview/)**<br>RFC 9421 HTTP Message Signatures for signing and verifying HTTP messages | [![CI](https://github.com/damianh/http-libs/actions/workflows/signatures-ci.yml/badge.svg)](https://github.com/damianh/http-libs/actions/workflows/signatures-ci.yml)<br>[![NuGet](https://img.shields.io/nuget/v/DamianH.Http.HttpSignatures.svg)](https://www.nuget.org/packages/DamianH.Http.HttpSignatures/)<br>[![Downloads](https://img.shields.io/nuget/dt/DamianH.Http.HttpSignatures.svg)](https://www.nuget.org/packages/DamianH.Http.HttpSignatures/) |
| **[DamianH.Http.ForwardedHeaders](https://damianh.github.io/http-libs/docs/forwarded-headers/overview/)**<br>RFC 7239 `Forwarded` parser and trusted-proxy middleware for ASP.NET Core | [![CI](https://github.com/damianh/http-libs/actions/workflows/forwarded-headers-ci.yml/badge.svg)](https://github.com/damianh/http-libs/actions/workflows/forwarded-headers-ci.yml)<br>[![NuGet](https://img.shields.io/nuget/v/DamianH.Http.ForwardedHeaders.svg)](https://www.nuget.org/packages/DamianH.Http.ForwardedHeaders/)<br>[![Downloads](https://img.shields.io/nuget/dt/DamianH.Http.ForwardedHeaders.svg)](https://www.nuget.org/packages/DamianH.Http.ForwardedHeaders/) |
| **[DamianH.Http.ForwardProxy](https://damianh.github.io/http-libs/docs/forward-proxy/overview/)**<br>HTTP forward (egress) proxy for Kestrel with CONNECT tunnels, proxy authentication and SSRF protection | [![CI](https://github.com/damianh/http-libs/actions/workflows/forward-proxy-ci.yml/badge.svg)](https://github.com/damianh/http-libs/actions/workflows/forward-proxy-ci.yml)<br>[![NuGet](https://img.shields.io/nuget/v/DamianH.Http.ForwardProxy.svg)](https://www.nuget.org/packages/DamianH.Http.ForwardProxy/)<br>[![Downloads](https://img.shields.io/nuget/dt/DamianH.Http.ForwardProxy.svg)](https://www.nuget.org/packages/DamianH.Http.ForwardProxy/) |

## Build and test

Requires the **.NET 10 SDK**. Run from the repository root; each library has its own build script and solution filter:

```bash
dotnet run signatures/build.cs -- build   # or: forwarded-headers, forward-proxy, structured-field-values, hybrid-cache-handler, file-distributed-cache
dotnet run signatures/build.cs -- test    # per library
dotnet build http-lib.slnx                # whole solution
dotnet test --solution http-lib.slnx      # all tests
```

## Local documentation

From the repository root:

```sh
dotnet run docs/build.cs -- build     # restore, build, generate, and check
dotnet run docs/build.cs -- check     # check existing output and run negative self-tests
dotnet run docs/build.cs -- preview   # build, check, and serve locally
```

The build restores the repository-local Atoll CLI. The site uses the same Lagoon
theme as [microstack](https://github.com/damianh/microstack).
See the [authoring and preview guide](https://damianh.github.io/http-libs/docs/contributing/)
for source locations and link conventions.

## License

MIT — see [LICENSE](LICENSE).

## Contributing

Bug reports should be accompanied by a reproducible test case in a pull request.
See [Contributing](https://damianh.github.io/http-libs/docs/contributing/) for the full workflow.
