# Forward proxy HTTP/1.1 conformance fixtures

A reproducible request-direction regression suite for `http://` absolute-form
forwarding, built on a pinned [HTTP Garden](https://github.com/narfindustries/http-garden)
checkout (`b417e806c1b15e8ea0b9312f81a91fbdcbc7a83e`).

HTTP Garden is a differential-testing toolkit, not a conformance suite: it has no
fixture corpus or pass/fail oracle. This harness uses Garden's echo origin, HTTP/1
parsers and `HTTPRequest.is_valid()` rules, plus a repo-owned corpus in
`fixtures.json` with RFC 9110/9112 expectations. Results are regression signals,
not standards certification.

**[Full guide](https://damianh.github.io/http-libs/docs/forward-proxy/conformance/)**
covers the fixture format, expectations, known failures and limitations.

Requires .NET 10 SDK, Node.js 22+, Python 3.12+ (standard library only) and Git.
Docker is not needed. Run from this directory:

```bash
./run-conformance.sh                       # full run, gated against expected-results.json
./run-conformance.sh --test-id host-missing # diagnostic, does not gate
./run-conformance.sh --update               # rewrite the baseline after review
./run-conformance.sh --serve                # keep the proxy and echo origin up for the Garden REPL
```

```powershell
.\run-conformance.ps1
.\run-conformance.ps1 -TestId host-missing
```

`results-*.json`, logs and `.http-garden/` are ignored by Git. HTTP Garden is
GPL-3.0 and is cloned at run time, never vendored. Harness tests:

```bash
node --test compare-results.test.mjs
```
