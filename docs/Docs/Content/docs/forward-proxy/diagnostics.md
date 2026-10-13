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
and the error type, without internal details:

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
| 403 | `destination_ip_prohibited` | Target IP or every resolved address is blocked |
| 502 | `dns_error` | Name did not resolve |
| 502 | `connection_refused` | Destination refused the connection |
| 502 | `destination_ip_unroutable`, `destination_unavailable` | Network unreachable or other connect failure |
| 502 | `http_response_incomplete` | Destination response was invalid (`http://` only) |
| 500 | `proxy_internal_error` | Unexpected proxy failure (logged) |
| 503 | `connection_limit_reached` | `MaxConcurrentTunnels` reached |
| 504 | `connection_timeout` | `ConnectTimeout` elapsed (including DNS), or `HttpActivityTimeout` for `http://` |

`407` and authorization `403` responses come from the authentication handler
and do not carry `Proxy-Status`. Set `AddProxyStatusHeader = false` to omit the
header. Malformed HTTP/1.1 `CONNECT` heads get `400` (or `431` when too large)
and the connection is closed.

## Logging

Logs use the categories `DamianH.Http.ForwardProxy.Internal.*`. Rejections
(`Information`) include the request kind, host, port, status and reason.
Tunnel closure (`Information`) includes duration and bytes in each direction.
An open proxy logs a `Warning` at startup.

## Metrics

Meter `DamianH.Http.ForwardProxy`:

| Instrument | Type | Tags |
|---|---|---|
| `forward_proxy.requests` | Counter | `forward_proxy.kind` (`connect`, `http`), `forward_proxy.outcome` (`allowed`, `unauthorized`, `denied`, `error`) |
| `forward_proxy.tunnels.active` | UpDownCounter | |
| `forward_proxy.tunnel.bytes` | Counter (bytes) | `forward_proxy.direction` (`upstream`, `downstream`) |
| `forward_proxy.tunnel.duration` | Histogram (s) | |

```csharp
builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("DamianH.Http.ForwardProxy"));
```

## Tracing

Each tunnel creates a `ForwardProxy.Tunnel` activity from the
`DamianH.Http.ForwardProxy` activity source, tagged with `server.address` and
`server.port`. `http://` forwarding is traced by YARP and `HttpClient`.
