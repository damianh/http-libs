# Forward proxy TLS interception with EST

This sample keeps the CA private key in a separate OpenCertServer process. The proxy requests
short-lived destination certificates over EST, and the CA host only issues certificates for the
configured host allowlist. The proxy never receives the CA private key.

> **Sample only:** OpenCertServer's own deployment notice says its server is not a hardened,
> production-ready CA. Configure and validate a CA deployment against your organization's certificate
> policy and threat model before using it for real traffic. The sample uses an in-memory CA and
> demonstration credentials.

## Run with Aspire

```shell
dotnet run --project forward-proxy/samples/EstInterception/EstInterception.AppHost
```

Enter a demo password when prompted for `est-password`. The AppHost starts the EST CA and proxy,
then opens the Aspire dashboard. The dashboard includes the proxy's HTTP, `HttpClient`, and
`DamianH.Http.ForwardProxy` metrics and traces. The proxy listens on `localhost:8080`; the CA
certificate path is printed by the CA host.

```shell
curl --proxy http://demo:change-me@localhost:8080 \
  --proxy-user demo:change-me \
  --cacert /tmp/http-libs-est-interception-ca.pem \
  https://httpbin.org/headers
```

The response includes `X-Sample-Proxy-User`. The default intercepted-host allowlist is
`httpbin.org`; other HTTPS destinations remain opaque tunnels. Configure a comma-separated
`intercepted-hosts` AppHost parameter to change the allowlist.

## Run the apps standalone

The standalone CA command below listens at `https://localhost:5443` and writes its public CA
certificate to `/tmp/http-libs-est-interception-ca.pem`. It accepts the EST Basic user `proxy`,
with the password from the `Est__Password` setting.

```shell
ASPNETCORE_URLS=https://localhost:5443 \
Est__Password=change-me \
Interception__AllowedHosts__0=httpbin.org \
Interception__CaCertificatePath=/tmp/http-libs-est-interception-ca.pem \
dotnet run --project forward-proxy/samples/EstInterception/EstInterception.CaHost
```

In a second terminal:

```shell
Est__Url=https://localhost:5443 \
Est__User=proxy \
Est__Password=change-me \
Est__CaCertificatePath=/tmp/http-libs-est-interception-ca.pem \
Interception__Hosts__0=httpbin.org \
dotnet run --project forward-proxy/samples/EstInterception/EstInterception.Proxy
```

Then use the same `curl` command shown above. For actual use, provide credentials through a secret
store, persist the CA and issued certificates appropriately, and enable OTLP export by setting
`OTEL_EXPORTER_OTLP_ENDPOINT` if desired. The optional interception path is documented in
[TLS interception](https://damianh.github.io/http-libs/docs/forward-proxy/tls-interception/).
