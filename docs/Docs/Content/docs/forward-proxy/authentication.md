---
title: Forward proxy authentication
sidebarLabel: Authentication
description: Authenticate and authorize proxy clients with ASP.NET Core authentication handlers, policies and the ProxyBasic handler.
order: 3
section: Forward Proxy
---

# Authentication and authorization

The proxy evaluates one ASP.NET Core authorization policy for every proxy
request, with the requested `ForwardProxyDestination` as the authorization
resource. Set it with `ForwardProxyOptions.AuthorizationPolicy` or
`AuthorizationPolicyName`. With neither set, the proxy allows anonymous clients
and logs a warning.

## Proxy Basic authentication

`AddProxyBasic` implements RFC 7617 Basic authentication over
`Proxy-Authorization`, challenging with `407` and
`Proxy-Authenticate: Basic realm="...", charset="UTF-8"`. Credentials are
decoded as strict UTF-8.

```csharp
builder.Services.AddAuthentication().AddProxyBasic(options =>
{
    options.Realm = "egress";
    options.ValidateCredentials = async (context, user, password) =>
    {
        var store = context.RequestServices.GetRequiredService<IProxyUserStore>();
        return await store.ValidateAsync(user, password, context.RequestAborted);
    };
});

builder.Services.AddForwardProxy(options =>
{
    options.AuthorizationPolicy = new AuthorizationPolicyBuilder(ProxyBasicDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .Build();
});
```

Compare secrets in constant time (`CryptographicOperations.FixedTimeEquals`) or
use a password hasher. Basic credentials are sent in clear text on a plain-text
listener; use a TLS proxy listener or a trusted network.

The proxy removes `Proxy-Authorization` before forwarding `http://` requests,
so credentials are never sent to destinations.

## Authorizing by destination

The destination is passed as the policy resource, so requirements can restrict
who may reach what:

```csharp
options.AuthorizationPolicy = new AuthorizationPolicyBuilder(ProxyBasicDefaults.AuthenticationScheme)
    .RequireAuthenticatedUser()
    .RequireAssertion(context =>
        context.Resource is ForwardProxyDestination { Host: var host }
        && (context.User.IsInRole("admin") || host.EndsWith(".example.com", StringComparison.Ordinal)))
    .Build();
```

An authenticated user who fails the policy gets `403`.

## Other authentication schemes

Any `AuthenticationHandler` works: client certificates (the TLS client
certificate is available for HTTP/1.1 `CONNECT`), API keys or custom tokens.
Most handlers are written for origin servers and challenge with `401` and
`WWW-Authenticate`, which proxy clients ignore. With `TranslateChallenges`
(default `true`) the proxy turns a `401` into `407` and moves
`WWW-Authenticate` values to `Proxy-Authenticate`. The handler must read
credentials from `Proxy-Authorization`, not `Authorization`.

## Client behavior

Clients send `Proxy-Authorization` after a `407` challenge or preemptively.
After a `407` on an HTTP/1.1 `CONNECT` the proxy closes the connection; clients
such as `HttpClient` and curl retry on a new connection with credentials.

```csharp
var handler = new SocketsHttpHandler
{
    Proxy = new WebProxy("http://proxy.internal:8080")
    {
        Credentials = new NetworkCredential("user", "password"),
    },
};
```
