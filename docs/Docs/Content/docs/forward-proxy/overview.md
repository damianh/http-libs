---
title: Forward proxy overview
sidebarLabel: Overview
description: Host an HTTP forward (egress) proxy on Kestrel with CONNECT tunnels, http:// forwarding and proxy authentication.
order: 1
section: Forward Proxy
---

# Forward proxy overview

`DamianH.Http.ForwardProxy` turns an ASP.NET Core application into an HTTP
forward proxy. A forward proxy acts on behalf of **clients**: they opt in by
configuring it (`HTTPS_PROXY`, `HttpClient.Proxy`, `curl --proxy`) and then ask
it to reach arbitrary destinations. A reverse proxy such as YARP acts on behalf
of **servers** and routes to a fixed set of backends.

| | Forward proxy | Reverse proxy |
|---|---|---|
| Destination chosen by | Client (request target) | Proxy configuration |
| `https://` traffic | Opaque `CONNECT` tunnel | Terminated TLS |
| Authentication | `Proxy-Authorization` / `407` | `Authorization` / `401` |
| Main risk | SSRF and open-proxy abuse | Backend exposure |

## Installation

```shell
dotnet add package DamianH.Http.ForwardProxy
```

## Request types

| Request | Example | Handled by |
|---|---|---|
| HTTP/1.1 `CONNECT` | `CONNECT example.com:443 HTTP/1.1` | `ListenOptions.UseForwardProxy()` |
| HTTP/2 `CONNECT` | `:method CONNECT`, `:authority example.com:443` | `app.UseForwardProxy()` |
| Absolute-form `http://` | `GET http://example.com/ HTTP/1.1` | `app.UseForwardProxy()` |

Kestrel has no API to turn an HTTP/1.1 request into a raw tunnel, so HTTP/1.1
`CONNECT` is handled by connection middleware that inspects the first request
on each connection. `CONNECT` must be the first request on an HTTP/1.1
connection, which is what proxy clients do. A later `CONNECT` on a keep-alive
connection gets `501`. Other traffic passes to Kestrel unchanged, so the same
endpoint can serve health checks and other endpoints.

HTTP/2 `CONNECT` needs TLS with ALPN (`h2`) on the listener. Extended `CONNECT`
(WebSockets over HTTP/2, `:protocol`) is not intercepted. Absolute-form
`https://` targets are rejected with `400`; clients must use `CONNECT`. HTTP/3
`CONNECT` is not supported and returns `501`.

## Hosting

```csharp
using DamianH.Http.ForwardProxy;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    // Plain-text proxy endpoint (HTTP/1.1 only).
    kestrel.ListenAnyIP(8080, listen => listen.UseForwardProxy());

    // TLS proxy endpoint with HTTP/2 CONNECT. UseForwardProxy must follow UseHttps.
    kestrel.ListenAnyIP(8443, listen =>
    {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
        listen.UseHttps("proxy.pfx", "password");
        listen.UseForwardProxy();
    });
});

builder.Services.AddForwardProxy(options =>
{
    // See Authentication and Destination policy.
});

var app = builder.Build();
app.UseForwardProxy();
app.MapGet("/healthz", () => "ok");
app.Run();
```

Call `app.UseForwardProxy()` before routing, authentication and authorization
middleware. Proxy requests are handled there and never reach endpoint routing,
so `GET http://example.com/healthz` is forwarded, not served by your
`/healthz` endpoint. Do not put `[Authorize]` or `RequireAuthorization()` on the
proxy; it runs its own policy (see [Authentication](../authentication/)).

## Sample

`forward-proxy/samples/ForwardProxySample` runs a Basic-authenticated proxy on
`localhost:8080`:

```shell
dotnet run --project forward-proxy/samples/ForwardProxySample
curl --proxy http://demo:change-me@localhost:8080 https://example.com/
```
