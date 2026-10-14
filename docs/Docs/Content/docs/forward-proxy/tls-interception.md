---
title: Forward proxy TLS interception
sidebarLabel: TLS interception
description: Decrypt selected CONNECT tunnels to inspect and modify HTTPS requests, such as injecting credentials.
order: 5
section: Forward Proxy
---

# TLS interception

By default `CONNECT` tunnels are opaque: the proxy relays encrypted bytes and
never sees the requests inside them. TLS interception (also called TLS
inspection, or man-in-the-middle) lets the proxy decrypt the tunnels you
select. It acts as the TLS server for the client and as the TLS client for the
destination, so it can read and modify each request and response.

The main use is changing headers on egress traffic, for example:

- injecting credentials (`Authorization`, API keys) so clients and agents never
  hold them;
- removing or rewriting headers the destination should not see;
- adding correlation or tenant headers.

Request and response bodies are streamed unchanged. That includes server-sent
events, so streaming protocols such as MCP's Streamable HTTP work through an
intercepted tunnel.

> **Interception breaks TLS's end-to-end guarantee.** Clients must trust a
> certificate authority (CA) you control, and anyone holding that CA's private
> key can impersonate any site to those clients. Keep the key in a secret store
> or HSM, use a dedicated CA, intercept only the destinations you need, and
> tell the people whose traffic you decrypt.

## How it works

1. The client sends `CONNECT example.com:443` (HTTP/1.1 or HTTP/2). The proxy
   authorizes it and applies the [destination policy](../destination-policy/)
   as usual.
2. Your `IForwardProxyInterceptionSelector` decides whether to intercept. If
   not, the tunnel is relayed as normal.
3. The proxy answers `200` and terminates the client's TLS with a certificate
   for `example.com` from your `IForwardProxyCertificateProvider`. HTTP/1.1 and
   HTTP/2 are negotiated with ALPN.
4. Each request inside the tunnel runs through your application's middleware
   pipeline. `app.UseForwardProxy()` then sends it to `https://example.com:443`
   over a new TLS connection, validating the destination's certificate.

Kestrel does the TLS and HTTP work through an in-memory endpoint, so intercepted
requests get Kestrel's limits, HTTP/2 support and request pipeline.

## Setup

```csharp
using System.Net.Security;
using DamianH.Http.ForwardProxy;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(8080, listen => listen.UseForwardProxy()));

builder.Services.AddForwardProxy(options => { /* authorization, destination policy */ });
builder.Services.AddForwardProxyTlsInterception<ApiHostSelector, CaCertificateProvider>();

var app = builder.Build();

// Runs for intercepted requests before they are forwarded.
app.Use((context, next) =>
{
    if (context.Features.Get<IForwardProxyInterceptionFeature>() is { } interception
        && interception.Destination.Host == "api.example.com")
    {
        context.Request.Headers.Authorization = $"Bearer {GetToken(context.User)}";
    }

    return next(context);
});

app.UseForwardProxy();
app.Run();

sealed class ApiHostSelector : IForwardProxyInterceptionSelector
{
    public ValueTask<bool> ShouldInterceptAsync(HttpContext context, ForwardProxyDestination destination, CancellationToken cancellationToken) =>
        ValueTask.FromResult(destination.Host == "api.example.com");
}
```

`AddForwardProxyTlsInterception<TSelector, TCertificateProvider>()` registers
both as singletons. The non-generic `AddForwardProxyTlsInterception()` registers
only the interception services; register an `IForwardProxyCertificateProvider`
and an `IForwardProxyInterceptionSelector` yourself. Without a selector nothing
is intercepted. Without a certificate provider the application fails to start.

## Selecting tunnels

`IForwardProxyInterceptionSelector.ShouldInterceptAsync` runs once per tunnel,
after authorization, the destination policy and any
`IForwardProxyDestinationFilter` have allowed it. `context.User` is the
authenticated proxy user, so you can intercept per user or tenant. The
selector is resolved from the request's services, so it can be singleton,
scoped or transient.

Do not intercept destinations whose clients pin certificates. Those clients
reject your certificate and the connection fails.

## Certificates

`IForwardProxyCertificateProvider.GetCertificateAsync` returns the certificate
the client sees for a destination, as an `SslStreamCertificateContext`
(certificate with private key, plus any intermediates). It is called during
every intercepted TLS handshake, so cache the certificates. Returning `null`
fails the handshake. There is no built-in CA; issue certificates from a CA you
control, for example:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DamianH.Http.ForwardProxy;

sealed class CaCertificateProvider(X509Certificate2 ca) : IForwardProxyCertificateProvider
{
    // Bounded by what the selector intercepts. Expire entries if certificates are short-lived.
    private readonly ConcurrentDictionary<string, Lazy<SslStreamCertificateContext>> _cache = new();

    public ValueTask<SslStreamCertificateContext?> GetCertificateAsync(ForwardProxyDestination destination, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SslStreamCertificateContext?>(_cache.GetOrAdd(destination.Host, host => new(() => Issue(host))).Value);

    private SslStreamCertificateContext Issue(string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var address))
        {
            san.AddIpAddress(address);
        }
        else
        {
            san.AddDnsName(host);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var certificate = request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7), serial);
        using var withKey = certificate.CopyWithPrivateKey(key);

        // Round-trip through PKCS#12 so the private key is usable by SslStream on every platform.
        var leaf = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
        return SslStreamCertificateContext.Create(leaf, additionalCertificates: null, offline: true);
    }
}
```

Clients must trust the CA. For example, add it to the operating system's trust
store, or point the client at it with `SSL_CERT_FILE`, `NODE_EXTRA_CA_CERTS`,
`REQUESTS_CA_BUNDLE` or `curl --cacert`.

### Keep the CA key out of the proxy

For a production design, keep the CA private key in a dedicated certificate
service and let the proxy request destination certificates over an enrollment
protocol such as EST. The proxy then needs only enrollment credentials and the
CA's public trust certificate; compromise of the proxy does not directly expose
the CA signing key.

The [EST interception sample](https://github.com/damianh/http-libs/tree/main/forward-proxy/samples/EstInterception)
uses [OpenCertServer](https://github.com/jjrdk/opencertserver) as the CA service.
Its proxy posts a PKCS#10 request with the destination's SAN to EST, caches the
issued leaf certificate, and never receives the CA private key. The CA host
restricts issuance to a configured host allowlist.

The sample is a development example, not a hardened CA deployment. OpenCertServer
also [documents](https://github.com/jjrdk/opencertserver) that its CA server is
not production-ready without additional deployment-specific policy and hardening.
Review its certificate policy and security posture before using it for real
traffic.

## Modifying requests

There are two places to change an intercepted request.

**Middleware** before `app.UseForwardProxy()` sees the decrypted request as an
`HttpContext`. Change `context.Request.Headers`, or short-circuit with your own
response. `context.User` is the proxy user who opened the tunnel, and
`IForwardProxyInterceptionFeature` gives the tunnel's destination. Middleware
also runs for the proxy's other requests (`http://` forwarding, HTTP/2
`CONNECT`, your own endpoints), so check for the feature.

**`IForwardProxyRequestTransform`** edits the outgoing `HttpRequestMessage`
after the proxy has built it (hop-by-hop headers and `Proxy-Authorization`
removed, `Via` added). It also edits the `HttpResponseMessage` before its head
is copied to the client. Transforms run in registration order and also apply
to `http://` forwarding. They are resolved from the request's services.

```csharp
builder.Services.AddSingleton<IForwardProxyRequestTransform, ApiKeyTransform>();

sealed class ApiKeyTransform(ISecretStore secrets) : IForwardProxyRequestTransform
{
    public async ValueTask TransformRequestAsync(HttpContext context, ForwardProxyDestination destination, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (destination.Host == "api.example.com")
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", await secrets.GetAsync("api-key", cancellationToken));
        }
    }

    public ValueTask TransformResponseAsync(HttpContext context, ForwardProxyDestination destination, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.Headers.Remove("Set-Cookie");
        return ValueTask.CompletedTask;
    }
}
```

An exception from a transform fails the request with `500`
`proxy_internal_error`. Changing the outgoing request URI or overriding `Host`
with a different normalized host or port is rejected with the same error;
transforms cannot reroute requests after destination policy checks. Equivalent
authorities, including omitted default ports, are allowed.

## Behaviour

- Only origin-form requests (`GET /path`) are accepted inside an intercepted
  tunnel. A `Host` (or `:authority`) that does not match the `CONNECT` target
  gets `421 Misdirected Request`, so a client cannot use a tunnel to reach
  another host.
- The tunnel is authorized once, when it opens. Requests inside it are not
  authorized again, but the destination's resolved addresses are checked again
  whenever the proxy connects.
- Requests go to the destination over HTTP/1.1, whatever the client used.
- The destination's certificate is validated against the system trust store.
  A failure gets `502` `tls_certificate_error`; other TLS failures get `502`
  `tls_protocol_error`. Set `DestinationCertificateValidation` to change the
  validation, for example to trust a private CA.
- WebSocket upgrades over HTTP/1.1 (`wss://`) are relayed. WebSockets over
  HTTP/2 (extended `CONNECT`) are rejected with `400`.
- An intercepted tunnel counts against `MaxConcurrentTunnels` and closes after
  `TunnelIdleTimeout` without traffic. Responses with
  `Content-Type: text/event-stream` use `TunnelIdleTimeout` rather than
  `HttpActivityTimeout`, so quiet event streams stay open.

## Options

| Option | Default | Description |
|---|---|---|
| `ClientProtocols` | `Http1AndHttp2` | HTTP versions offered to the client with ALPN: `Http1`, `Http2` or `Http1AndHttp2`. |
| `HandshakeTimeout` | 10 s | Time allowed for the client's TLS handshake. |
| `DestinationCertificateValidation` | `null` | Validates the destination's certificate. `null` accepts only certificates without errors. |

```csharp
builder.Services.AddForwardProxyTlsInterception<ApiHostSelector, CaCertificateProvider>(options =>
{
    options.ClientProtocols = HttpProtocols.Http1;
    options.HandshakeTimeout = TimeSpan.FromSeconds(5);
});
```

## Hosting requirements

- Interception requires Kestrel. Call `AddForwardProxyTlsInterception` after
  the host is configured to use Kestrel, as `WebApplication.CreateBuilder` and
  `CreateSlimBuilder` do.
- Intercepted tunnels are served by an in-memory Kestrel endpoint. It is listed
  in the server's addresses as `https://forward-proxy-tls-interception`.
- Because that endpoint is configured in code, Kestrel ignores
  `ASPNETCORE_URLS`, `--urls` and `UseUrls`. Configure the proxy's endpoints in
  code (as `ListenOptions.UseForwardProxy()` needs anyway) or in the
  `Kestrel:Endpoints` configuration section.

## Sample

`forward-proxy/samples/ForwardProxySample` intercepts the hosts you name,
issuing certificates from a CA it creates at startup, and adds an
`X-Sample-Proxy-User` header to intercepted requests:

```shell
dotnet run --project forward-proxy/samples/ForwardProxySample -- --Interception:Hosts:0=httpbin.org
curl --proxy http://demo:change-me@localhost:8080 --cacert /tmp/forward-proxy-sample-ca.pem https://httpbin.org/headers
```

The CA certificate path is logged at startup.

## Diagnostics

An intercepted tunnel counts in `forward_proxy.requests` with
`forward_proxy.kind=connect` and `forward_proxy.outcome=intercepted`. Each
request inside it counts with `forward_proxy.kind=intercepted`. Tunnel metrics,
logs and traces work as for other tunnels. See
[Errors and diagnostics](../diagnostics/).
