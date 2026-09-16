---
title: Forwarded Headers overview
sidebarLabel: Overview
description: Install RFC 7239 parsing and trusted-proxy middleware, configure a local proxy, and run the sample.
order: 1
section: Forwarded Headers
---

# Forwarded Headers overview

`DamianH.Http.ForwardedHeaders` provides standalone
[RFC 7239](https://www.rfc-editor.org/rfc/rfc7239.html) `Forwarded`
header parsing and trusted-proxy middleware for ASP.NET Core on .NET 10.

ASP.NET Core's built-in `UseForwardedHeaders` reads `X-Forwarded-*`, not RFC 7239.
This library processes `Forwarded` directly, keeping each hop's parameters
together. It is **not** a legacy-header adapter and never translates, merges,
prefers, or falls back to `X-Forwarded-*`. RFC 7239 is not an RFC 8941 Structured
Field; this package does not depend on the StructuredFieldValues library.

## Installation

Run in the consuming project directory:

```shell
dotnet add package DamianH.Http.ForwardedHeaders
```

The parser and middleware ship in the same package, which has a
`Microsoft.AspNetCore.App` framework reference. Even a parser-only application
requires the ASP.NET Core runtime. There is no public serializer in v1.

## One local proxy: quick start

```csharp
using DamianH.Http.ForwardedHeaders;
using Microsoft.AspNetCore.Http.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddForwarded(options =>
{
    options.Parameters = ForwardedParameters.For | ForwardedParameters.Host
        | ForwardedParameters.Proto;
    options.ForwardLimit = 1;
    // Retain the defaults: KnownProxies = ::1, KnownIPNetworks = 127.0.0.0/8.
    options.AllowedHosts.Add("localhost");
});

var app = builder.Build();
app.UseForwarded();
app.UseRouting();
app.MapGet("/request", (HttpRequest request) => request.GetEncodedUrl());
app.Run();
```

Use this trust configuration only for a local proxy and control direct client
access. Register forwarding early, before routing, HTTPS redirection,
authentication, authorization, and anything that generates external URLs.
`AddForwarded` uses the options system and validates at startup; middleware
snapshots its configuration. Alternatively, use
`app.UseForwarded(new ForwardedOptions { Parameters = ForwardedParameters.Proto })`
with explicitly configured trust for your deployment. Do not register both forms.

Before deployment, read [proxy configuration](/docs/forwarded-headers/configuration/)
and [deployment safety](/docs/forwarded-headers/deployment-safety/), especially
trust-all behavior and incompatibility with `UseForwardedHeaders` on the same
request. [PathBase](/docs/forwarded-headers/pathbase/) is a separate, nonstandard
opt-in; [diagnostics](/docs/forwarded-headers/diagnostics/) describes malformed
input handling and trusted observations.

## Run the sample

The [ASP.NET Core sample](https://github.com/damianh/http-libs/blob/main/forwarded-headers/samples/AspNetCoreSample/Program.cs) enables
`For | Host | Proto | PathBase`, trusts the loopback defaults, accepts one hop,
and allowlists `localhost`. From the repository root:

```powershell
dotnet run --project forwarded-headers\samples\AspNetCoreSample --urls http://127.0.0.1:5080
```

In another PowerShell 7.3+ window (with standard native argument passing),
simulate a local proxy using `curl.exe`:

```powershell
curl.exe -H 'Forwarded: for=192.0.2.60;host="localhost:8443";proto=https;pathbase="/gateway"' http://127.0.0.1:5080/request
```

The response URL is `https://localhost:8443/gateway/request`, with effective
remote IP `192.0.2.60` and port `0`. The actual endpoint path remains `/request`.
The sample returns neither the raw header nor the proxy chain.

Build or test this product using the shared repository build helpers, from the
repository root:

```powershell
dotnet run forwarded-headers\build.cs -- build
dotnet run forwarded-headers\build.cs -- test
```

For parsing without middleware, see [direct parser use](/docs/forwarded-headers/parser/).
