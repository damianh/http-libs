---
title: "Library directory"
description: "Find the HTTP library and NuGet package for your .NET application."
order: 0
section: "Getting started"
---

## Find your library

| Library guide | What it does | NuGet |
| --- | --- | --- |
| [HTTP hybrid cache handler](/docs/hybrid-cache-handler/overview/) | RFC 9111 client-side HTTP caching for `HttpClient`, backed by .NET's `HybridCache`. | [![DamianH.HttpHybridCacheHandler on NuGet](https://img.shields.io/nuget/v/DamianH.HttpHybridCacheHandler.svg)](https://www.nuget.org/packages/DamianH.HttpHybridCacheHandler/) |
| [File distributed cache](/docs/file-distributed-cache/overview/) | Persistent local-file `IDistributedCache` / `IBufferDistributedCache` for a single process. | [![DamianH.FileDistributedCache on NuGet](https://img.shields.io/nuget/v/DamianH.FileDistributedCache.svg)](https://www.nuget.org/packages/DamianH.FileDistributedCache/) |
| [Structured field values](/docs/structured-field-values/overview/) | RFC 8941/9651 parsing, serialization, and POCO mapping for HTTP Structured Field Values. | [![DamianH.Http.StructuredFieldValues on NuGet](https://img.shields.io/nuget/v/DamianH.Http.StructuredFieldValues.svg)](https://www.nuget.org/packages/DamianH.Http.StructuredFieldValues/) |
| [HTTP signatures](/docs/signatures/overview/) | RFC 9421 HTTP Message Signatures for signing and verifying HTTP messages. | [![DamianH.Http.HttpSignatures on NuGet](https://img.shields.io/nuget/v/DamianH.Http.HttpSignatures.svg)](https://www.nuget.org/packages/DamianH.Http.HttpSignatures/) |
| [Forwarded headers](/docs/forwarded-headers/overview/) | RFC 7239 `Forwarded` parsing and trusted-proxy middleware for ASP.NET Core. | [![DamianH.Http.ForwardedHeaders on NuGet](https://img.shields.io/nuget/v/DamianH.Http.ForwardedHeaders.svg)](https://www.nuget.org/packages/DamianH.Http.ForwardedHeaders/) |

The HTTP cache also supports separately versioned
[content-store packages](/docs/hybrid-cache-handler/content-stores/overview/) for
Azure Blob Storage, Amazon S3, Google Cloud Storage, and local files. Cloud
adapters use official SDKs.

Start with [package selection and installation](/docs/getting-started/), or see
[Contributing](/docs/contributing/) to build, test, and work on these guides.
