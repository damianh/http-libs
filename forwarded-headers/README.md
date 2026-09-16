# DamianH.Http.ForwardedHeaders

RFC 7239 `Forwarded` parsing and trusted-proxy middleware for ASP.NET Core on
.NET 10. Reads `Forwarded` only: no translation, merging, preference, or fallback
to `X-Forwarded-*`. RFC 7239 is not a Structured Field. Even parser-only use
requires the ASP.NET Core runtime; there is no public serializer in v1.

## Installation

Run in the consuming project directory:

```shell
dotnet add package DamianH.Http.ForwardedHeaders
```

## One local proxy: quick start

```csharp
using DamianH.Http.ForwardedHeaders;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddForwarded(options =>
{
    options.Parameters = ForwardedParameters.For | ForwardedParameters.Host
        | ForwardedParameters.Proto;
    options.ForwardLimit = 1;
    options.AllowedHosts.Add("localhost");
    // Retain loopback trust defaults: ::1 and 127.0.0.0/8.
});

var app = builder.Build();
app.UseForwarded();
app.UseRouting();
app.MapGet("/", () => "Hello");
app.Run();
```

Use this configuration only for a local proxy; restrict direct client access.
Register forwarding before routing, HTTPS redirection, authentication,
authorization, and external URL generation. Production requires explicit trusted
peers and proxies that overwrite untrusted claims or append correctly.

`Parameters` defaults to `None`; `PathBase` is a separate nonstandard opt-in,
excluded from `All`. Emptying **both** trusted-proxy collections means **trust
all**, not trust nobody. **Do not combine `UseForwarded` and
`UseForwardedHeaders` on the same request**, including host-enabled integrations.
Parsed or forwarded values are proxy claims, not authenticated user identity.

## Documentation

- [Overview, local quick start, and runnable sample](https://damianh.github.io/http-libs/docs/forwarded-headers/overview/)
- [Defaults, proxy chains, and host allowlisting](https://damianh.github.io/http-libs/docs/forwarded-headers/configuration/)
- [Nonstandard PathBase extension](https://damianh.github.io/http-libs/docs/forwarded-headers/pathbase/)
- [Malformed input, consumption, and diagnostics](https://damianh.github.io/http-libs/docs/forwarded-headers/diagnostics/)
- [Deployment safety and trust boundaries](https://damianh.github.io/http-libs/docs/forwarded-headers/deployment-safety/)
- [Direct parser use](https://damianh.github.io/http-libs/docs/forwarded-headers/parser/)
