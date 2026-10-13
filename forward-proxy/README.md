# DamianH.Http.ForwardProxy

An HTTP forward (egress) proxy for ASP.NET Core and Kestrel on .NET 10. Clients
configure it as their proxy (`HTTPS_PROXY`, `HttpClient.Proxy`, `curl --proxy`)
to reach arbitrary destinations, subject to your authentication, authorization
and destination rules.

- HTTP/1.1 `CONNECT` tunnels (connection middleware) and HTTP/2 `CONNECT`
  tunnels (RFC 9113 §8.5).
- Absolute-form `http://` forwarding, including WebSocket upgrades. No reverse-proxy package dependency; the
  forwarding core is adapted from [YARP](https://github.com/dotnet/yarp) (MIT, see `THIRD-PARTY-NOTICES.txt`).
- Proxy authentication through ASP.NET Core authentication handlers and
  authorization policies, with `407`/`Proxy-Authenticate`. A `ProxyBasic`
  handler is included.
- Secure by default: only ports 443 (`CONNECT`) and 80 (`http://`), and private,
  loopback, link-local (cloud metadata) and other non-public addresses are
  blocked after DNS resolution, so DNS rebinding cannot bypass them.
- RFC 9209 `Proxy-Status` errors, `Via`, logs, metrics and traces.

## Installation

```shell
dotnet add package DamianH.Http.ForwardProxy
```

## Quick start

```csharp
using DamianH.Http.ForwardProxy;
using DamianH.Http.ForwardProxy.Authentication;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(8080, listen =>
{
    listen.UseForwardProxy(); // HTTP/1.1 CONNECT
}));

builder.Services.AddAuthentication().AddProxyBasic(options =>
{
    options.ValidateCredentials = (context, user, password) => ValidateAsync(user, password);
});

builder.Services.AddForwardProxy(options =>
{
    options.AuthorizationPolicy = new AuthorizationPolicyBuilder(ProxyBasicDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build();
});

var app = builder.Build();
app.UseForwardProxy(); // http:// forwarding and HTTP/2 CONNECT; before routing and auth middleware
app.MapGet("/healthz", () => "ok");
app.Run();
```

```shell
curl --proxy http://user:password@localhost:8080 https://example.com/
```

Without an authorization policy the proxy is **open to anyone who can reach
it** and logs a warning at startup. Never expose an open proxy to the internet.

## Documentation

- [Overview and hosting](https://damianh.github.io/http-libs/docs/forward-proxy/overview/)
- [Destination policy](https://damianh.github.io/http-libs/docs/forward-proxy/destination-policy/)
- [Authentication and authorization](https://damianh.github.io/http-libs/docs/forward-proxy/authentication/)
- [Errors and diagnostics](https://damianh.github.io/http-libs/docs/forward-proxy/diagnostics/)
- [Deployment safety](https://damianh.github.io/http-libs/docs/forward-proxy/deployment-safety/)
