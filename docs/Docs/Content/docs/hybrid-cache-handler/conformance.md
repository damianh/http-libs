---
title: HTTP cache RFC 9111 conformance
sidebarLabel: RFC 9111 conformance
description: Run the multi-target cache-tests matrix, inspect runtime provenance, and enforce exact regression baselines.
order: 7
section: HTTP hybrid caching
---

# HTTP cache RFC 9111 conformance

The pinned public [http-tests/cache-tests](https://github.com/http-tests/cache-tests)
suite (`b55b8bda3dbb8c927c04e85bd8d496a8caa3e4ba`), behind
[cache-tests.fyi](https://cache-tests.fyi), runs against three **actual
implementation assets**, in default HybridCache and streaming filesystem modes.
The front-end always uses .NET 10, ASP.NET Core, and YARP 2.3.0; it does not use
HttpListener, URL ACL registration, an obsolete ASP.NET server, or a custom HTTP
parser. The handler runs in `Shared` mode.

| Implementation | Cache execution | Platforms |
| --- | --- | --- |
| `net10.0` | Direct handler, contracts, and filesystem .NET 10 assemblies | Windows, Linux |
| `netstandard2.0` | Direct .NET Standard 2.0 assemblies in a .NET 10 host | Windows, Linux |
| `net472` | Separate .NET Framework console worker using the Framework CLR and all three `net472` assemblies | Windows only |

The Standard host is a separate project with independent `bin`/`obj` directories.
Its references explicitly select `netstandard2.0`; test-only
`Microsoft.Bcl.AsyncInterfaces` and `Microsoft.Bcl.TimeProvider` dependencies
supply forwarding assemblies needed by that host. Startup asserts
`TargetFrameworkAttribute` on the loaded handler, contracts, and filesystem
adapter. The Framework worker also asserts that
`RuntimeInformation.FrameworkDescription` starts with `.NET Framework`.
Loading a `net472` assembly on modern .NET does **not** satisfy this harness.

**Upstream support warning:** HybridCache 10.8.0 reports that `net472` is
unsupported and untested upstream. The warning remains visible in build logs;
these runs do not change upstream support. See
[framework compatibility](/docs/hybrid-cache-handler/compatibility/).

## Run locally

Requires .NET 10 SDK, Node.js 22+, npm, and Git. Framework runs also require a
Windows host with .NET Framework 4.7.2 or newer. Reference assemblies are restored
from NuGet; the harness does not install machine-wide SDKs or modify machine ACLs.

From the repository root on Windows:

```powershell
.\hybrid-cache-handler\conformance\run-conformance.ps1 -Framework net10.0
.\hybrid-cache-handler\conformance\run-conformance.ps1 -Framework netstandard2.0 -FileSystem
.\hybrid-cache-handler\conformance\run-conformance.ps1 -Framework net472 -FileSystem
```

On Linux/macOS, from the repository root:

```bash
./hybrid-cache-handler/conformance/run-conformance.sh --framework netstandard2.0 --file-system
```

Omit the framework to use `net10.0`; omit filesystem mode to use default
HybridCache. `-TestId ID` / `--test-id ID` runs a single diagnostic fixture and
**does not gate** or produce a full-suite results file. Ports default to
available loopback ports; `-OriginPort`/`-ProxyPort`,
`--origin-port`/`--proxy-port`, or the Bash wrapper's
`ORIGIN_PORT`/`PROXY_PORT` environment variables override them. An occupied
requested port fails rather than reusing an unrelated listener.

Both shell entrypoints delegate to the same Node launcher. It clones the public
suite if missing, verifies the pinned revision and unmodified fixture source,
and installs dependencies only when `node_modules` is missing. It runs the pinned
npm scripts' Node entrypoints with their npm configuration environment directly,
so process ownership is unambiguous and paths containing spaces do not need shell
escaping. Complete initial clone/install and builds before parallel local runs;
distinct framework/mode cells have separate result/log filenames.

Shared options preserve the direct modern baseline: shared-cache mode,
50 MiB maximum cacheable content, no enabled compression, threshold 1 MiB in
default mode or 1 byte in filesystem mode, no redirects/cookies/system proxy,
and HTTP/1.1 forwarding. The worker uses Framework's `HttpClientHandler`;
direct hosts retain `SocketsHttpHandler`. No header-reflection workaround is
used. Legacy public header enumeration can normalize case, spacing, dates, and
value boundaries. Investigate these differences rather than silently changing
the modern baseline.

Framework transport isolates connection pools by the suite's fixture UUID,
while retaining one shared cache handler and HybridCache. Some pinned fixtures
deliberately send a body longer than Content-Length. HttpWebRequest can reuse
that contaminated connection for another fixture, yielding spurious
`ResponseStatusLine` failures or retries. Each fixture reuses its own
HttpClientHandler; requests and response bytes are not rewritten. At most 1,024
fixture pools are allowed, all owned by the worker. Modern/Standard pooling is
unchanged.

## Framework bridge

Each request gets its own randomly named-pipe connection to the persistent
worker-owned cache. A bounded, length-prefixed binary envelope carries method,
absolute URI, HTTP version, separate HTTP/content header arrays, binary body,
status, and reason phrase. Header values, notably `Set-Cookie`, are not joined.
Explicit Content-Length values are retained, including HEAD responses; buffering
does not invent Content-Length when none was declared. This is an IPC envelope
around framework HTTP messages, **not an HTTP parser**.

Frames are bounded to 64 MiB, bodies to 60 MiB, individual strings to 1 MiB,
header entries to 512, and values per entry to 4,096. Encoding checks frame/body
budgets before each write and caps buffer capacity accordingly; string byte
lengths are checked before allocating UTF-8 payloads. The worker accepts at most
32 concurrent connections. Invalid lengths, truncated frames, unexpected message
kinds, misplaced headers, and trailing data are rejected. This test-only bridge
buffers bodies; it is not a production streaming transport.

Cancellation closes the request pipe and cancels the worker's origin request.
Worker/protocol errors poison front-end health rather than becoming successful
fallback responses. Readiness verifies the launched front-end PID, exact
implementation provenance, and worker handshake. Health and process ownership
are checked again after the suite, before accepting results. The worker exits
if its parent dies; normal cleanup stops only explicitly launched PID trees
and removes only that run's uniquely created content directory.

Origin transport failures are a distinct IPC message: deliberate closed-origin
fixtures exercise YARP's normal error behavior without misclassifying a healthy
worker as failed. Cancelling a response-body copy disposes its content to
interrupt blocked legacy reads, as well as cancelling the origin request.

## Results and gating

`results-<framework>-<default|filesystem>.json`, matching `*-provenance.json`,
and build/origin/proxy/client/comparison `*.log` files are written in
[the conformance directory](https://github.com/damianh/http-libs/tree/main/hybrid-cache-handler/conformance)
and ignored by Git. The checks workflow uploads them even on failure.

Every full run uses the unchanged
[`expected-results.json`](https://github.com/damianh/http-libs/blob/main/hybrid-cache-handler/conformance/expected-results.json):
previously passing fixtures must still pass, and **every baseline fixture must
be present**, including known failures. New passes and changed known-failure
categories are reported. Single-fixture diagnostics cannot masquerade as a full
run. `-Update` / `--update` is an explicit maintenance operation restricted to
full modern runs; review differences before using it.

[`legacy-expectations.json`](https://github.com/damianh/http-libs/blob/main/hybrid-cache-handler/conformance/legacy-expectations.json)
is a reviewed, target-scoped **exact-outcome overlay**, not a rewritten baseline
or modified suite. It accepts only recorded header-formatting messages:
Cache-Control directive ordering/case and Content-Type optional whitespace.
A different outcome on a previously passing fixture, or any missing baseline
fixture, still fails. Raw pass/failure counts are not inflated by these
exceptions. Modern runs cannot use the overlay.

The pinned suite checks most origin-header string equality after request
sequences and cache assertions; diagnostics also demonstrate cached responses
for these formatting-only cases. Explicit header assertions can stop a fixture
earlier, so an accepted formatting outcome does not establish that every later
assertion ran.

The compatibility change recorded six local Windows runs of 365 fixtures each:
344 raw passes per modern mode and 324 per legacy mode, with no unaccepted
baseline regressions. Framework execution used .NET Framework 4.8, not an actual
4.7.2 runtime. These are recorded results, not a conformance certification;
the suite also measures optional optimizations and does not expect every
fixture to pass. The CI matrix covers all six Windows cells and four
modern/Standard Linux cells. Linux execution was configured in CI but not
verified locally as part of that change.

## Debug and maintain the harness

From the repository root, debug one Framework fixture:

```powershell
.\hybrid-cache-handler\conformance\run-conformance.ps1 -Framework net472 -TestId <test-id>
```

After reviewing new passes from a full modern run, ratchet the baseline:

```powershell
.\hybrid-cache-handler\conformance\run-conformance.ps1 -Framework net10.0 -Update
```

Or on Linux/macOS:

```bash
./hybrid-cache-handler/conformance/run-conformance.sh --framework net10.0 --update
```

Review and commit `expected-results.json`. Run harness tests from the repository
root:

```powershell
node --test hybrid-cache-handler/conformance/compare-results.test.mjs hybrid-cache-handler/conformance/worker-lifecycle.test.mjs
dotnet run --project hybrid-cache-handler/conformance/ConformanceProtocol.Tests/ConformanceProtocol.Tests.csproj -c Release
```

Comparison tests cover missing passing/known-failure fixtures, empty output,
regressions, and improvements. Protocol tests cover metadata/body fidelity,
cookie boundaries, content separation, HEAD lengths, fragmented reads, invalid
lengths, truncation, trailing data, and cancellation. Size tests cover exact
frame/string boundaries, combined metadata/header/body budgets, and rejection
before buffer growth or later fields.

The Windows-only lifecycle test additionally exercises binary PATCH forwarding,
multiple Set-Cookie fields, origin disconnects, cancellation during a stalled
body, and worker termination. Its diagnostic output is saved in
`worker-lifecycle.log`.
