---
title: Forward proxy errors and diagnostics
sidebarLabel: Errors and diagnostics
description: Proxy-Status error responses, logging, metrics and tracing for the forward proxy.
order: 4
section: Forward Proxy
---

# Errors and diagnostics

## Error responses

Proxy-generated errors carry an [RFC 9209](https://www.rfc-editor.org/rfc/rfc9209.html)
`Proxy-Status` header naming the proxy (`ProxyName`, default `forward-proxy`)
and the error type, without internal details. `ProxyName` is also the `Via`
pseudonym, so it must be an HTTP token starting with a letter (no `:` or `/`):

```http
HTTP/1.1 403 Forbidden
Proxy-Status: egress; error=destination_ip_prohibited
Content-Length: 0
Connection: close
```

| Status | `error=` | Cause |
|---|---|---|
| 400 | `http_request_error` | Malformed request or target, absolute-form `https://`, userinfo in target |
| 403 | `http_request_denied` | Port, host rule or `IForwardProxyDestinationFilter` denied |
| 421 | `http_request_denied` | Intercepted request's `Host` does not match the `CONNECT` target |
| 403 | `destination_ip_prohibited` | Target IP or every resolved address is blocked |
| 502 | `http_request_denied` | Forwarding loop: the request's `Via` already has an entry received by `ProxyName` (`details="forwarding loop detected"`) |
| 502 | `dns_error` | Name did not resolve |
| 502 | `connection_refused` | Destination refused the connection |
| 502 | `destination_ip_unroutable`, `destination_unavailable` | Network unreachable or other connect failure |
| 502 | `http_response_incomplete` | Destination response was invalid (`http://` and intercepted requests) |
| 502 | `tls_certificate_error` | Destination's certificate failed validation (intercepted requests) |
| 502 | `tls_protocol_error` | TLS with the destination failed (intercepted requests) |
| 500 | `proxy_internal_error` | Unexpected proxy failure, including an upstream client-certificate selector exception or invalid selection (logged) |
| 503 | `connection_limit_reached` | `MaxConcurrentTunnels` reached (`CONNECT` or WebSocket upgrade), or all upstream client-certificate pools are active at their configured limit |
| 504 | `connection_timeout` | `ConnectTimeout` elapsed (including DNS and upstream client-certificate selection), or `HttpActivityTimeout` for `http://` and intercepted requests |

`407` and authorization `403` responses come from the authentication handler
and do not carry `Proxy-Status`. Set `AddProxyStatusHeader = false` to omit the
header. Malformed HTTP/1.1 `CONNECT` heads get `400` (or `431` when too large)
and the connection is closed.

### Forwarding loops

Forwarded `http://` and intercepted requests get a `Via` entry with
`ProxyName` as the received-by pseudonym (set `AddViaHeader = false` to omit
it). When a request arrives whose `Via` already contains an entry received by
`ProxyName` (compared case-insensitively), it has looped back through this
proxy: it gets `502` without contacting the destination:

```http
HTTP/1.1 502 Bad Gateway
Proxy-Status: egress; error=http_request_denied; details="forwarding loop detected"
```

Give each proxy in a chain a distinct `ProxyName`. Set
`RejectForwardingLoops = false` to forward such requests anyway. `CONNECT`
requests carry no `Via` and are not checked.

## Logging

Logs use the categories `DamianH.Http.ForwardProxy.Internal.*`. Rejections
(`Information`) include the request kind, host, port, status and reason.
Tunnel closure (`Information`) includes duration and bytes in each direction.
Upgraded WebSocket connections are tunnels too, in logs, metrics and traces.
Intercepted tunnels log at `Information` when they open, and a missing or
failing interception certificate logs a `Warning`.
An open proxy logs a `Warning` at startup.

## Metrics

Meter `DamianH.Http.ForwardProxy`. An intercepted tunnel counts as
`connect`/`intercepted`, and each request inside it as `intercepted`:

| Instrument | Type | Tags |
|---|---|---|
| `forward_proxy.requests` | Counter | `forward_proxy.kind` (`connect`, `http`, `intercepted`), `forward_proxy.outcome` (`allowed`, `intercepted`, `unauthorized`, `denied`, `error`) |
| `forward_proxy.tunnels.active` | UpDownCounter | |
| `forward_proxy.tunnel.bytes` | Counter (bytes) | `forward_proxy.direction` (`upstream`, `downstream`) |
| `forward_proxy.tunnel.duration` | Histogram (s) | |

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("DamianH.Http.ForwardProxy"));
```

## Tracing

Each tunnel creates a `ForwardProxy.Tunnel` activity from the
`DamianH.Http.ForwardProxy` activity source, tagged with `server.address` and
`server.port`. `http://` forwarding and intercepted requests are traced by
`HttpClient`.
