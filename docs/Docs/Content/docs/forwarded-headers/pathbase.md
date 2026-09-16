---
title: Forwarded Headers PathBase extension
sidebarLabel: PathBase extension
description: Opt into the nonstandard pathbase parameter without rewriting the request path.
order: 3
section: Forwarded Headers
---

# Forwarded Headers PathBase extension

## Nonstandard `pathbase` extension

Explicitly add `ForwardedParameters.PathBase` to `Parameters` to interpret
`pathbase`; RFC 7239 itself does **not** define this parameter.

```http
Forwarded: for=192.0.2.60;host="app.example.com:8443";proto=https;pathbase="/gateway"
```

The value replaces `Request.PathBase` using ASP.NET Core `PathString` URI
conversion. It never appends to PathBase, strips a prefix from `Request.Path`,
or rewrites Path. The proxy must already forward the intended application path.
`pathbase=""` clears PathBase; a missing parameter leaves the staged value alone.

Use a rooted URI path and quote it as required by HTTP token syntax (a slash
requires quoting). Network-path references (`//host`), non-rooted values,
query/fragment suffixes, backslashes, invalid escapes, and controls are invalid.
The extension follows the same trust, hop-limit, and error policies as the
standard parameters. When disabled it is uninterpreted metadata. Neither
`X-Forwarded-Prefix` nor `X-Forwarded-PathBase` is read.

`ForwardedParameters.All` excludes `PathBase`. See
[configuration](/docs/forwarded-headers/configuration/),
[malformed input handling](/docs/forwarded-headers/diagnostics/#malformed-input-and-trust-boundaries),
and the [runnable sample](/docs/forwarded-headers/overview/#run-the-sample).
