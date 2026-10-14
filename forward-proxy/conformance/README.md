# Forward proxy HTTP/1.1 conformance fixtures

A reproducible request-direction regression suite for `http://` absolute-form
forwarding. It is written entirely in C#: `ForwardProxyConformance` hosts the
proxy in-process, replays the repo-owned corpus in `fixtures.json` over raw
sockets to an echo origin, and gates the results against `expected-results.json`.
Results are regression signals, not standards certification.

The default origin is built in. `--garden-origin` swaps in the
[HTTP Garden](https://github.com/narfindustries/http-garden) echo server at a
pinned commit (`b417e806c1b15e8ea0b9312f81a91fbdcbc7a83e`) as an independent
third-party origin; both must produce the same baseline.

**[Full guide](https://damianh.github.io/http-libs/docs/forward-proxy/conformance/)**
covers the fixture format, expectations, known failures and limitations.

Requires the .NET 10 SDK. `--garden-origin` also needs Git and Python 3.12+
(standard library only); Docker is never needed. From the repository root:

```bash
dotnet test --project forward-proxy/conformance/ForwardProxyConformance.Tests   # harness tests + full gate

cd forward-proxy/conformance
dotnet run --project ForwardProxyConformance -c Release                              # full run, gated
dotnet run --project ForwardProxyConformance -c Release -- --test-id host-missing    # diagnostic, does not gate
dotnet run --project ForwardProxyConformance -c Release -- --update                  # rewrite the baseline after review
dotnet run --project ForwardProxyConformance -c Release -- --garden-origin           # gate using HTTP Garden's origin
dotnet run --project ForwardProxyConformance -c Release -- --serve                   # keep proxy and origin up for the Garden REPL
```

`results-*.json`, logs and `.http-garden/` are ignored by Git. HTTP Garden is
GPL-3.0 and is cloned at run time, never vendored; no Garden code is ported.
