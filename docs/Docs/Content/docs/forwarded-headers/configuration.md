---
title: Forwarded Headers proxy configuration
sidebarLabel: Proxy configuration
description: Defaults, independent parameter opt-ins, trusted proxy chains, hop limits, and host allowlisting.
order: 2
section: Forwarded Headers
---

# Forwarded Headers proxy configuration

Start with the [local proxy quick start](/docs/forwarded-headers/overview/#one-local-proxy-quick-start),
then configure the actual transport peers for your deployment.

## Defaults and independent opt-ins

| Option | Default / meaning |
|---|---|
| `Parameters` | `None`: no forwarding, even from a known proxy |
| `ForwardLimit` | `1` accepted hop; `0` disables consumption; `null` removes the count limit |
| `KnownProxies` | `IPAddress.IPv6Loopback` (`::1`) |
| `KnownIPNetworks` | `System.Net.IPNetwork.Parse("127.0.0.0/8")` |
| `AllowedHosts` | Empty: any syntactically valid forwarded host |
| `MalformedHeaderBehavior` | `Ignore` |
| `HeaderName` | `Forwarded` |
| `OriginalForHeaderName` | `X-Original-For` |
| `OriginalHostHeaderName` | `X-Original-Host` |
| `OriginalProtoHeaderName` | `X-Original-Proto` |
| `OriginalPrefixHeaderName` | `X-Original-Prefix` |

`For`, `Host`, and `Proto` independently update the effective remote endpoint,
request host, and scheme. `By` enables validation of the `by` node identifier
as metadata only: **it never establishes proxy trust**.
`All` combines these four standard flags, but **excludes `PathBase`**.
Header names must be distinct valid HTTP tokens.

## Explicit production proxies and multiple hops

For `client -> edge (10.0.0.10) -> ingress (10.0.0.20) -> app`:

```csharp
using System.Net;
using DamianH.Http.ForwardedHeaders;

builder.Services.AddForwarded(options =>
{
    options.Parameters = ForwardedParameters.For | ForwardedParameters.Host
        | ForwardedParameters.Proto;
    options.ForwardLimit = 2;
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Parse("10.0.0.20"));
    options.KnownProxies.Add(IPAddress.Parse("10.0.0.10"));
    options.KnownIPNetworks.Clear();
    options.AllowedHosts.Add("app.example.com");
});
```

Replace these example addresses with the actual transport peers. For one remote
proxy, configure only that address and set `ForwardLimit = 1`. If addresses rotate,
`KnownIPNetworks.Add(IPNetwork.Parse("10.0.0.0/24"))` can trust a dedicated proxy
subnet; do not trust a broad client-accessible network merely for convenience.

Traversal is right-to-left (nearest hop first), with one shared hop limit.
The transport peer must be trusted before its claims are examined. At each
subsequent hop, the previous hop's concrete `for` address must identify a trusted
proxy. This trust traversal still happens when `For` rewriting is disabled.
IPv4-mapped IPv6 peers also match IPv4 proxy/network entries.

A missing, `unknown`, or obfuscated `for` accepts the current trusted hop's usable
enabled values, then stops traversal. It does not change the remote endpoint.
A concrete IP with an omitted or obfuscated port can advance trust; applying it
sets `RemotePort` to **0**, not the previous node's port. The effective
`Connection.RemoteIpAddress`/`RemotePort` after forwarding are not necessarily
the physical connection peer.

RFC node syntax permits numeric ports up to 99999. Rewriting a concrete remote
endpoint additionally requires a socket port in the range 0-65535; metadata-only
ports do not establish or prevent address-based proxy trust.

## Host allowlisting

`AllowedHosts` entries omit ports. Matching is case-insensitive, ignores the
forwarded port, and uses ASP.NET Core `HostString` matching, including IDN
configuration and `*.example.com` subdomain wildcards (not the parent domain).
An empty list or the unrestricted wildcard entries `*`, `0.0.0.0`, and `[::]`
allow all valid hosts. Incoming hosts use a deliberately safe ASP.NET Core-like
ASCII host-authority validator, not every RFC URI host form; use punycode for
internationalized names. Bracketed IPv6 is supported. Malformed and out-of-range
ports are rejected. Host allowlisting applies only when `Host` is enabled and
is not a replacement for filtering ordinary incoming `Host` headers.

## Trust boundary requirements

Emptying **both** `KnownProxies` and `KnownIPNetworks` means **trust all**, not
trust nobody. A null transport `RemoteIpAddress` provides no authenticated proxy
identity. Read [deployment safety](/docs/forwarded-headers/deployment-safety/)
before enabling forwarding outside a controlled local setup.
