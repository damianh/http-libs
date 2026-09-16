# http-libs

[![CI](https://github.com/damianh/http-libs/actions/workflows/hybrid-cache-handler-ci.yml/badge.svg)](https://github.com/damianh/http-libs/actions)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![GitHub Stars](https://img.shields.io/github/stars/damianh/http-libs.svg)](https://github.com/damianh/http-libs/stargazers)

A collection of .NET libraries for HTTP caching, structured field values, message signatures, and trusted-proxy forwarding.

**[Documentation](https://damianh.github.io/http-libs/)** ·
[Get started](https://damianh.github.io/http-libs/docs/getting-started/) ·
[Contributing](https://damianh.github.io/http-libs/docs/contributing/)

## Packages

| Package | Description | NuGet | Downloads |
|---------|-------------|-------|-----------|
| [DamianH.HttpHybridCacheHandler](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/overview/) | RFC 9111 client-side HTTP caching handler for `HttpClient` | [![NuGet](https://img.shields.io/nuget/v/DamianH.HttpHybridCacheHandler.svg)](https://www.nuget.org/packages/DamianH.HttpHybridCacheHandler/) | [![Downloads](https://img.shields.io/nuget/dt/DamianH.HttpHybridCacheHandler.svg)](https://www.nuget.org/packages/DamianH.HttpHybridCacheHandler/) |
| [DamianH.FileDistributedCache](https://damianh.github.io/http-libs/docs/file-distributed-cache/overview/) | Local-file `IDistributedCache` / `IBufferDistributedCache` for single-process persistent caching | [![NuGet](https://img.shields.io/nuget/v/DamianH.FileDistributedCache.svg)](https://www.nuget.org/packages/DamianH.FileDistributedCache/) | [![Downloads](https://img.shields.io/nuget/dt/DamianH.FileDistributedCache.svg)](https://www.nuget.org/packages/DamianH.FileDistributedCache/) |
| [DamianH.Http.StructuredFieldValues](https://damianh.github.io/http-libs/docs/structured-field-values/overview/) | RFC 8941/9651 parser, serializer, and POCO mapper for HTTP Structured Field Values | [![NuGet](https://img.shields.io/nuget/v/DamianH.Http.StructuredFieldValues.svg)](https://www.nuget.org/packages/DamianH.Http.StructuredFieldValues/) | [![Downloads](https://img.shields.io/nuget/dt/DamianH.Http.StructuredFieldValues.svg)](https://www.nuget.org/packages/DamianH.Http.StructuredFieldValues/) |
| [DamianH.Http.HttpSignatures](https://damianh.github.io/http-libs/docs/signatures/overview/) | RFC 9421 HTTP Message Signatures for signing and verifying HTTP messages | [![NuGet](https://img.shields.io/nuget/v/DamianH.Http.HttpSignatures.svg)](https://www.nuget.org/packages/DamianH.Http.HttpSignatures/) | [![Downloads](https://img.shields.io/nuget/dt/DamianH.Http.HttpSignatures.svg)](https://www.nuget.org/packages/DamianH.Http.HttpSignatures/) |
| [DamianH.Http.ForwardedHeaders](https://damianh.github.io/http-libs/docs/forwarded-headers/overview/) | RFC 7239 `Forwarded` parser and trusted-proxy middleware for ASP.NET Core | [![NuGet](https://img.shields.io/nuget/v/DamianH.Http.ForwardedHeaders.svg)](https://www.nuget.org/packages/DamianH.Http.ForwardedHeaders/) | [![Downloads](https://img.shields.io/nuget/dt/DamianH.Http.ForwardedHeaders.svg)](https://www.nuget.org/packages/DamianH.Http.ForwardedHeaders/) |

## Repository Structure

The HTTP cache also has separately versioned [content-store packages](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/content-stores/overview/)
for Azure Blob Storage, Amazon S3, Google Cloud Storage, and local files, sharing a
provider-independent ContentStore package. Cloud adapters use official SDKs.

```
hybrid-cache-handler/         # RFC 9111 HTTP caching DelegatingHandler
file-distributed-cache/       # File-based IDistributedCache implementation
structured-field-values/      # RFC 8941/9651 structured field values
signatures/                   # RFC 9421 HTTP message signatures
forwarded-headers/            # RFC 7239 Forwarded parser and ASP.NET Core middleware
docs/Docs/Content/docs/        # Detailed documentation, grouped by library
```

## Build and test

Requires the **.NET 10 SDK**. Run from the repository root; each library has its own build script and solution filter:

```bash
dotnet run signatures/build.cs -- build   # or: forwarded-headers, structured-field-values, hybrid-cache-handler, file-distributed-cache
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
