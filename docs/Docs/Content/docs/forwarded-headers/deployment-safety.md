---
title: Forwarded Headers deployment safety
sidebarLabel: Deployment safety
description: Enforce proxy trust boundaries and avoid spoofed claims or competing forwarding middleware.
order: 5
section: Forwarded Headers
---

# Forwarded Headers deployment safety

- Proxies must overwrite untrusted incoming `Forwarded` or append correctly
  while preserving an enforced trust boundary. **A trusted proxy can pass through
  a client-spoofed header unchanged**; its configured IP alone cannot make that
  header authentic. Restrict direct access to the application.
- For ASP.NET Core compatibility, emptying **both** `KnownProxies` and
  `KnownIPNetworks` disables address trust checks: it means **trust all**, not
  trust nobody. Use only with an independently enforced boundary.
- A null transport `RemoteIpAddress` can allow the first hop to be considered for
  compatibility with servers that cannot supply it. This provides no
  authenticated proxy identity. Without a concrete `for`, traversal stops after
  that hop; protect this deployment boundary externally.
- **Do not combine `UseForwarded` and `UseForwardedHeaders` on the same request.**
  Check automatic IIS and environment-enabled integrations (including
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED`) as well as explicit registrations.
  This library does not disable host integrations. If both formats are needed,
  select separate configured pipelines/trust boundaries, not a fallback chain.
- Forwarded values describe proxy claims, not authenticated users. Avoid exposing
  raw headers, original headers, or a full proxy chain in public diagnostics.

This library reads RFC 7239 `Forwarded`, never translates, merges, prefers, or
falls back to `X-Forwarded-*`. Use explicit
[proxy configuration](/docs/forwarded-headers/configuration/) and follow the
[middleware ordering guidance](/docs/forwarded-headers/overview/#one-local-proxy-quick-start).
