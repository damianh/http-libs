---
title: Forward proxy HTTP/1.1 conformance fixtures
sidebarLabel: Conformance fixtures
description: Run the request-direction conformance fixtures, read results, and update the regression baseline.
order: 7
section: Forward Proxy
---

# Forward proxy HTTP/1.1 conformance fixtures

`forward-proxy/conformance` replays raw HTTP/1.1 requests through the forward
proxy and inspects the exact bytes an origin receives. It complements the unit
and integration tests with wire-level checks of request-target handling, `Host`,
hop-by-hop fields, `Via`, `Max-Forwards`, message framing and field syntax.

It is a regression gate against a reviewed baseline. It is **not** a
certification of RFC 9110 or RFC 9112 conformance: the corpus is repo-owned,
covers selected requirements only, and some fixtures accept more than one
compliant behaviour.

## How it works

The harness is a C# console app, `forward-proxy/conformance/ForwardProxyConformance`.
It starts an echo origin and the forward proxy in-process on free loopback
ports, sends each fixture's raw bytes to the proxy over a socket, and parses
what comes back. The proxy is an open HTTP/1.1 proxy that may reach loopback
destinations on any port; never expose this configuration.

The echo origin replies `200` with the bytes it received as the body, so the
harness sees exactly what the proxy forwarded. Its behaviour matches HTTP
Garden's `echo_server.py`: it reads until 100 ms pass without input, echoes,
and closes the connection after an idle read.

Forwarded bytes are parsed permissively so malformed output stays visible, then
checked strictly: the request line must be `token SP target SP HTTP/1.x CRLF`,
nothing may be left unparsed, the method and field names must be tokens and
field values must not contain CR, LF or NUL (RFC 9110 §5.1, §5.5, §9.1).

## HTTP Garden

[HTTP Garden](https://github.com/narfindustries/http-garden) is a differential
fuzzing toolkit: it runs many servers and reverse proxies in Docker and compares
how they interpret the same bytes. It does not ship a fixture corpus, a
pass/fail oracle or forward-proxy targets.

`--garden-origin` replaces the built-in origin with Garden's
`tools/echo_server.py` at pinned commit
`b417e806c1b15e8ea0b9312f81a91fbdcbc7a83e`. The harness clones it into the
ignored `.http-garden/` directory (it is GPL-3.0, so it is never vendored or
ported) and checks that `tools/` matches the pin. CI gates both origins against
the same baseline, so a third-party origin independently confirms the results.

## Running

Requires the .NET 10 SDK. `--garden-origin` also needs Git and Python 3.12+
(standard library only; set `PYTHON` or pass `--python <path>`).

```bash
# Harness unit tests plus a full gated run with the built-in origin
dotnet test --project forward-proxy/conformance/ForwardProxyConformance.Tests

cd forward-proxy/conformance
dotnet run --project ForwardProxyConformance -c Release                            # full run and gate
dotnet run --project ForwardProxyConformance -c Release -- --test-id host-missing  # one fixture, diagnostic only
dotnet run --project ForwardProxyConformance -c Release -- --update                # rewrite expected-results.json
dotnet run --project ForwardProxyConformance -c Release -- --garden-origin         # gate using HTTP Garden's origin
```

Runs write `results-full.json` (`results-garden-full.json` with
`--garden-origin`) and proxy and origin logs. Exit codes: `0` no regressions,
`1` regressions, missing fixtures or run failure, `2` usage error.

## Fixtures and outcomes

Each fixture in `fixtures.json` has an `id`, `reference`, raw `payload` (with
`{origin}` replaced by the echo origin's `host:port`) and an expectation:

| `expect` | Passes when |
| --- | --- |
| `forward` | The origin receives the expected number of requests (default 1), every check holds, no proxy error is returned, and the forwarded bytes have a strict request line, parse without leftovers and have valid method and field syntax. |
| `reject` | Nothing reaches the origin and the proxy responds with a listed status (default any 4xx). |
| `not-forwarded` | Nothing reaches the origin. |
| `reject-or-forward` | Either `reject` or `forward` passes, for requirements that allow rejecting or repairing a message. |

Checks address request `request` (default 0) by `method`, `uri`, `version`,
`body`, or `header` with `absent`, `count`, `equals`, `notEquals`, `matches` or
`notMatches`. Results record the outcome, reasons and observed responses,
parsed requests and raw origin bytes.

`expected-results.json` stores `pass`/`fail`/`error` per fixture. The gate fails
on a missing baseline fixture or a `pass` that no longer passes. New passes,
changed failure types and fixtures not yet in the baseline are reported without
gating. Only full runs may use `--update`; review the diff before committing it.

## Known failures in the baseline

| Fixture | Observed |
| --- | --- |
| `target-empty-path`, `target-options-empty-path` | `http://host` is forwarded with an empty request-target (`GET  HTTP/1.1`) instead of `/` (RFC 9112 §3.2.1), or `*` for `OPTIONS` with no query: §3.2.4 requires the last proxy to send asterisk-form here, overriding the general origin-form rule. |
| `target-fragment` | `#fragment` is forwarded in the request-target. |
| `target-uppercase-scheme` | `HTTP://` is rejected with `400` by Kestrel before the proxy runs. |
| `max-forwards-zero-options`, `max-forwards-decremented` | `Max-Forwards` is not processed (RFC 9110 §7.6.2). |
| `framing-content-length-plus-sign` | `Content-Length: +5` is accepted by Kestrel and forwarded as `5`. |
| `framing-te-in-http10` | An HTTP/1.0 request with `Transfer-Encoding` is forwarded rather than treated as faulty framing. |

Fixing a defect turns its fixture into a new pass; update the baseline in the
same change.

## Exploring with the Garden REPL

`dotnet run --project ForwardProxyConformance -c Release -- --serve` keeps the
proxy and origin running until Ctrl+C and prints an entry to add to
`.http-garden/external-services.yml`. With Garden's Python dependencies
installed, its REPL can then transduce absolute-form payloads through the proxy
and, with Garden origins running in Docker, `fanout` the forwarded bytes for
differential comparison:

```text
garden> payload 'GET http://127.0.0.1:<origin-port>/ HTTP/1.1\r\nHost: a\r\n\r\n' | transduce dotnet_forward_proxy
```

## Limitations

- Request direction, HTTP/1.1 `http://` forwarding only. `CONNECT` tunnels,
  HTTP/2, TLS interception, WebSocket upgrades and response handling (the echo
  origin always returns a well-formed `200`) are not covered.
- The echo origin replies after 100 ms without input, so bodies that the proxy
  streams with longer gaps are not observable; fixtures send each message in a
  single write.
- Kestrel parses requests before the proxy middleware runs, so several results
  reflect Kestrel behaviour.
- HTTP Garden upstream supports x86-64 and AArch64 Linux. `--garden-origin`
  uses only its portable echo server, but Windows runs are untested upstream.
