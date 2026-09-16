---
title: "Getting started"
description: "Choose and install the library that matches your HTTP caching or protocol needs."
order: 1
section: "Getting started"
---

# Getting started

http-libs is a collection of individual .NET libraries, not an umbrella package.
Install only the packages your application needs. Use the **.NET 10 SDK** to build
the repository or run its samples. The HTTP cache handler, ContentStore contracts,
and four adapters also ship .NET Standard 2.0 and .NET Framework 4.7.2 assets;
the other libraries, including FileDistributedCache, remain .NET 10.
Read [framework compatibility](/docs/hybrid-cache-handler/compatibility/) before
choosing older targets, including the upstream HybridCache `net472` support warning.

## Choose a package

| Your application needs | Choose | Important distinction |
| --- | --- | --- |
| Cache outbound HTTP responses through `HttpClient`. | [DamianH.HttpHybridCacheHandler](/docs/hybrid-cache-handler/overview/) | An RFC 9111 caching `DelegatingHandler`, using `HybridCache` for L1 memory and optional L2 storage. |
| Persist cache entries locally across process restarts without external infrastructure. | [DamianH.FileDistributedCache](/docs/file-distributed-cache/overview/) | An `IDistributedCache` / `IBufferDistributedCache` backend, not an HTTP handler or a cache shared across instances. |
| Parse, serialize, or map HTTP Structured Field Values to POCOs. | [DamianH.Http.StructuredFieldValues](/docs/structured-field-values/overview/) | For RFC 8941/9651 structured fields, not arbitrary HTTP header syntax. |
| Sign HTTP messages or verify message signatures. | [DamianH.Http.HttpSignatures](/docs/signatures/overview/) | RFC 9421 cryptographic verification is not a substitute for application acceptance policy or replay protection. |
| Interpret RFC 7239 `Forwarded` headers behind trusted proxies in ASP.NET Core. | [DamianH.Http.ForwardedHeaders](/docs/forwarded-headers/overview/) | Configure proxy trust before accepting forwarded values. This is not the built-in `X-Forwarded-*` middleware. |

## Install in your application

Run the relevant command from the directory containing your application project:

```sh
dotnet add package DamianH.HttpHybridCacheHandler
```

```sh
dotnet add package DamianH.FileDistributedCache
```

```sh
dotnet add package DamianH.Http.StructuredFieldValues
```

```sh
dotnet add package DamianH.Http.HttpSignatures
```

```sh
dotnet add package DamianH.Http.ForwardedHeaders
```

These are alternatives, not an instruction to install all five. Follow the
linked library guide for its quick start, configuration, and limitations.

## Choose cache storage separately

The HTTP caching handler and its storage backends serve different roles:

- **HybridCache L2:** FileDistributedCache can supply persistent, local L2
  storage to `HybridCache`. It is suitable for desktop/mobile applications, CLI
  tools, agents, and single-instance services. Do not share its cache directory
  between processes. For multi-instance services needing a shared cache, choose
  Redis, SQL Server, or another distributed backend.
- **HTTP response content stores:** The handler has separately versioned
  [content-store packages](/docs/hybrid-cache-handler/content-stores/overview/)
  for Azure Blob Storage, Amazon S3, Google Cloud Storage, and local files,
  sharing provider-independent ContentStore contracts. These are distinct from
  FileDistributedCache. Cloud adapters use their official SDKs; follow each
  provider guide for setup and ownership requirements.

For a local persistent HTTP cache, follow
[FileDistributedCache configuration and HybridCache integration](/docs/file-distributed-cache/configuration/).

## Work with the repository

The source, samples, and tests are in
[damianh/http-libs](https://github.com/damianh/http-libs). See
[Contributing](/docs/contributing/) for repository-root build and test commands,
the documentation source of truth, and local preview instructions.
