---
title: Forwarded Headers parser
sidebarLabel: Parser
description: Parse RFC 7239 fields and node identifiers without making middleware trust decisions.
order: 6
section: Forwarded Headers
---

# Forwarded Headers parser

## Direct parser use

```csharp
using DamianH.Http.ForwardedHeaders;

var header = ForwardedHeaderParser.Parse(
    "for=192.0.2.60;proto=https;host=\"app.example.com:8443\"");
var first = header.Elements[0];
var host = first.Host;
var parameters = first.Parameters;

if (ForwardedHeaderParser.TryParse(
    new string?[] { "for=192.0.2.60", "for=10.0.0.10;proto=https" },
    out var parsed, out var error))
{
    var hopCount = parsed.Elements.Count;
}

if (ForwardedNodeIdentifier.TryParse("[2001:db8::1]:443", out var node))
{
    // A parsed node is not evidence that this address is a trusted proxy.
}
```

`Parse` and `TryParse` accept either one string or ordered `IEnumerable<string?>`
field values. `ForwardedHeader.Value` retains the combined field text and
`Elements` preserves wire order. Each immutable `ForwardedElement` has a
case-insensitive `Parameters` dictionary and `For`, `By`, `Host`, `Proto`
convenience properties. Parameter values are unquoted/unescaped; unknown
extensions are retained.

The parser handles quoted separators, quoted-pair escapes, and empty list or
semicolon slots; it rejects duplicate parameter names (including case variants
and extensions). Syntax failures report an error position/reason; use `TryParse`
for untrusted input without exceptions. Field parsing is not host/scheme
validation or a trust decision. The separate node parser distinguishes concrete
IP, unknown, and obfuscated identities and numeric/obfuscated ports; RFC-valid
numeric port syntax is not necessarily a usable socket port.

The parser shares the middleware package's `Microsoft.AspNetCore.App` framework
reference: even parser-only applications require the ASP.NET Core runtime.
There is no public serializer in v1. See
[installation](/docs/forwarded-headers/overview/#installation) and
[proxy trust configuration](/docs/forwarded-headers/configuration/).
