---
title: Forward proxy deployment safety
sidebarLabel: Deployment safety
description: Avoid open proxies and SSRF when deploying the forward proxy.
order: 6
section: Forward Proxy
---

# Deployment safety

## Never run an open proxy

Without an authorization policy, anyone who can reach the listener can use your
network identity and reach whatever your server can. Open proxies on the
internet are found and abused within hours. Require authentication, or bind
the listener to a private network that only trusted clients can reach, and
preferably both.

## Keep internal networks blocked

`BlockNonPublicAddresses` stops clients from reaching loopback, private
networks and cloud metadata endpoints (`169.254.169.254`, `fd00:ec2::254`). If
the proxy must reach internal services, add only the needed ranges to
`AllowedNetworks`, and consider an `AllowedHosts` list as well. Setting
`BlockNonPublicAddresses = false` makes every internal address reachable.

The proxy also refuses `localhost` and other names resolving to blocked
addresses, so it cannot be used to reach the host's other loopback services or
loop back into itself.

## Restrict ports

The defaults (`443` for `CONNECT`, `80` for `http://`) stop clients tunnelling
to SSH, SMTP, databases and other services. Add ports only when required.

## Protect credentials

Proxy Basic credentials are clear text on a plain-text listener. Use a TLS
listener (`listen.UseHttps()` before `listen.UseForwardProxy()`) when clients
cross untrusted networks. Clients configure an `https://` proxy URL for this.

## Protect the interception CA

With [TLS interception](../tls-interception/), clients trust a CA whose private
key the proxy holds. Anyone with that key can impersonate any site to those
clients. Use a CA dedicated to the proxy, keep its key in a secret store or
HSM, and select only the destinations you need to decrypt. For stronger key
isolation, keep the CA in a separate certificate service and issue destination
certificates to the proxy over an enrollment protocol such as EST. The
[interception sample](https://github.com/damianh/http-libs/tree/main/forward-proxy/samples/EstInterception)
demonstrates this pattern; it is an example, not a production CA deployment.

## Limit resources

Each tunnel, `CONNECT` or WebSocket upgrade, holds two connections. Tune `MaxConcurrentTunnels`,
`TunnelIdleTimeout`, and Kestrel's `MaxConcurrentConnections` and
`MaxConcurrentUpgradedConnections` for your capacity. Monitor
`forward_proxy.tunnels.active`.

## What the proxy does not do

- It does not inspect tunnelled traffic unless you enable
  [TLS interception](../tls-interception/) for the tunnel. Other `CONNECT`
  tunnels are opaque; policy applies to the requested host and port only. A
  client can tunnel to an allowed host and send a different TLS SNI or `Host`
  header within the tunnel.
- It does not cache responses or add `Forwarded`/`X-Forwarded-For` headers.
- It does not support HTTP/3 `CONNECT`, `CONNECT-UDP` or `CONNECT-IP`.
