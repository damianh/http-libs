---
title: Forward proxy destination policy
sidebarLabel: Destination policy
description: Control which hosts, ports and IP addresses the forward proxy can reach, and block SSRF to internal networks.
order: 2
section: Forward Proxy
---

# Destination policy

A forward proxy that can reach anything your server can reach is an SSRF
vector. The defaults are restrictive. Loosen them deliberately.

## Order of checks

1. **Authorization** (see [Authentication](../authentication/)).
2. **Ports**: `AllowedConnectPorts` (default `{ 443 }`) for `CONNECT`,
   `AllowedHttpPorts` (default `{ 80 }`) for `http://`. Set to `null` to allow
   any port.
3. **Host names**: `DeniedHosts` wins, then `AllowedHosts` (if non-empty, the
   host must match).
4. **IP literal targets** are checked against the network rules immediately.
5. **`IForwardProxyDestinationFilter`** services, in registration order.
6. **Concurrency**: `MaxConcurrentTunnels` (default 10,000) for `CONNECT`
   tunnels and WebSocket upgrades.
7. **DNS resolution and connect**: every resolved address is checked against
   the network rules, and the proxy connects to that exact address.

Step 7 makes the IP rules immune to DNS rebinding: a name that resolves to a
blocked address is refused (`403`, `destination_ip_prohibited`). This applies
to both `CONNECT` and `http://` forwarding.

## Host patterns

| Pattern | Matches |
|---|---|
| `*` | Any host |
| `example.com` | `example.com` only (case-insensitive, trailing dot ignored) |
| `*.example.com` | Subdomains such as `api.example.com`, not `example.com` |
| `192.0.2.10`, `[2001:db8::1]` | That IP literal (IPv4-mapped IPv6 is equivalent) |

Host rules match the name the client asked for. Use network rules to control
where names may resolve.

## Network rules

```csharp
builder.Services.AddForwardProxy(options =>
{
    options.AllowedHosts.Add("*.github.com");
    options.AllowedHosts.Add("api.nuget.org");
    options.DeniedNetworks.Add(IPNetwork.Parse("203.0.113.0/24"));
    // Allow one internal service range despite BlockNonPublicAddresses.
    options.AllowedNetworks.Add(IPNetwork.Parse("10.20.0.0/16"));
});
```

An address is checked in this order: `DeniedNetworks` (deny), `AllowedNetworks`
(allow), then `BlockNonPublicAddresses` (default `true`). The built-in block
list covers:

- IPv4: `0.0.0.0/8`, `10.0.0.0/8`, `100.64.0.0/10`, `127.0.0.0/8`,
  `169.254.0.0/16` (including `169.254.169.254` cloud metadata),
  `172.16.0.0/12`, `192.0.0.0/24`, `192.0.2.0/24`, `192.88.99.0/24`,
  `192.168.0.0/16`, `198.18.0.0/15`, `198.51.100.0/24`, `203.0.113.0/24`,
  `224.0.0.0/4` and `240.0.0.0/4`.
- IPv6: unspecified, loopback, unique-local `fc00::/7` (including AWS
  `fd00:ec2::254`), link-local, multicast, documentation, discard and other
  special-purpose ranges.
- IPv4-mapped, NAT64 (`64:ff9b::/96`) and 6to4 (`2002::/16`) addresses are
  judged by the IPv4 address they embed. Local-use NAT64 (`64:ff9b:1::/48`) is
  blocked.

## Custom filters

Implement `IForwardProxyDestinationFilter` for dynamic rules, such as per-tenant
allow lists. Filters run after the built-in host and port checks, and the
authenticated user is available on `context.User`.

```csharp
public sealed class TenantFilter(ITenantStore tenants) : IForwardProxyDestinationFilter
{
    public async ValueTask<bool> IsAllowedAsync(HttpContext context, ForwardProxyDestination destination, CancellationToken cancellationToken)
    {
        var tenant = await tenants.FindAsync(context.User, cancellationToken);
        return tenant?.AllowedHosts.Contains(destination.Host) == true;
    }
}

builder.Services.AddSingleton<IForwardProxyDestinationFilter, TenantFilter>();
```

For HTTP/1.1 `CONNECT`, `context` is a synthetic `HttpContext` built from the
`CONNECT` request: headers, connection information, TLS client certificate,
`User` and `RequestServices` are available. There is no request body and the
response is controlled by the proxy.

## Timeouts and limits

| Option | Default | Purpose |
|---|---|---|
| `ConnectTimeout` | 10 s | DNS resolution plus TCP connect to the destination |
| `TunnelIdleTimeout` | 5 min | Closes tunnels (including upgraded WebSockets) with no traffic in either direction |
| `HttpActivityTimeout` | 100 s | Maximum time without progress when forwarding `http://` requests |
| `ConnectRequestHeadersTimeout` | 30 s | Time allowed to receive an HTTP/1.1 `CONNECT` head |
| `MaxConnectRequestHeadersSize` | 16 KiB | Larger HTTP/1.1 `CONNECT` heads get `431` |
| `MaxConcurrentTunnels` | 10,000 | Further tunnels (`CONNECT` and WebSocket upgrades) get `503`; `null` for no limit |

HTTP/2 tunnels disable Kestrel's minimum data rate and maximum request body
size for that stream; `TunnelIdleTimeout` governs them instead.
