# RFC 9111 conformance matrix

The pinned public [`http-tests/cache-tests`](https://github.com/http-tests/cache-tests)
suite (`b55b8bda3dbb8c927c04e85bd8d496a8caa3e4ba`) runs against three **actual
implementation assets**, in both default HybridCache and streaming filesystem modes.
The front-end always uses .NET 10, ASP.NET Core, and YARP 2.3.0; it does not use
HttpListener, URL ACL registration, an obsolete ASP.NET server, or a custom HTTP parser.

| Implementation | Cache execution | Platforms |
| --- | --- | --- |
| `net10.0` | Direct handler, contracts, and filesystem .NET 10 assemblies | Windows, Linux |
| `netstandard2.0` | Direct .NET Standard 2.0 assemblies in a .NET 10 host | Windows, Linux |
| `net472` | Separate .NET Framework console worker using the .NET Framework CLR and all three `net472` assemblies | Windows only |

The Standard host is a separate project with independent `bin`/`obj` directories.
Its project references explicitly select `netstandard2.0`; test-only
`Microsoft.Bcl.AsyncInterfaces` and `Microsoft.Bcl.TimeProvider` dependencies supply
the forwarding assemblies needed by that host. Startup asserts
`TargetFrameworkAttribute` on the loaded handler, content-store contracts, and
filesystem adapter. The Framework worker additionally asserts
`RuntimeInformation.FrameworkDescription` starts with `.NET Framework`.
Loading a `net472` assembly on modern .NET does **not** satisfy this harness.

**Upstream support warning:** Microsoft.Extensions.Caching.Hybrid 10.8.0 reports
that `net472` is unsupported and untested upstream. The warning is intentionally
visible in build logs; these conformance runs do not change upstream support.

## Running

Requires .NET 10 SDK, Node.js 22+, npm, and Git. Framework runs also require a
Windows host with .NET Framework 4.7.2 or newer. Reference assemblies are restored
from NuGet; the harness does not install machine-wide SDKs or modify machine ACLs.

```powershell
.\run-conformance.ps1 -Framework net10.0
.\run-conformance.ps1 -Framework netstandard2.0 -FileSystem
.\run-conformance.ps1 -Framework net472 -FileSystem
```

```bash
./run-conformance.sh --framework netstandard2.0 --file-system
```

Omit the framework to use `net10.0`; omit filesystem mode to use default HybridCache.
`-TestId ID` / `--test-id ID` runs a single fixture with the suite's diagnostic
output and **does not gate** or produce a full-suite results file. Ports default
to available loopback ports; `-OriginPort`/`-ProxyPort`, `--origin-port`/`--proxy-port`,
or the Bash wrapper's `ORIGIN_PORT`/`PROXY_PORT` environment variables override them.
An occupied requested port fails instead of reusing an unrelated listener.

Both shell entrypoints delegate to the same Node launcher. It clones the public
suite if missing, verifies the pinned revision and unmodified fixture source,
and installs dependencies only when `node_modules` is missing. It runs the pinned
npm scripts' Node entrypoints with their npm configuration environment directly,
so process ownership is unambiguous and paths containing spaces do not need shell
escaping. Initial clone/install and builds should be completed before parallel
local runs; distinct framework/mode cells have separate result/log filenames.

The shared options preserve the direct modern baseline: shared-cache mode,
50 MiB maximum cacheable content, no enabled compression, threshold 1 MiB in
default mode or 1 byte in filesystem mode, no redirects/cookies/system proxy,
and HTTP/1.1 forwarding. The worker uses the framework's `HttpClientHandler`;
direct hosts retain `SocketsHttpHandler`. No header-reflection workaround is used.
Legacy public `HttpHeaders` enumeration can normalize header case, spacing,
dates, and value boundaries. These transport/formatting differences must be
investigated rather than silently changing the modern baseline.

The Framework transport isolates connection pools by the suite's fixture UUID,
while retaining one shared cache handler and HybridCache. Some pinned fixtures
deliberately send a body longer than their Content-Length. HttpWebRequest can
reuse that contaminated connection for an unrelated fixture, yielding spurious
`ResponseStatusLine` failures or retries. Each fixture still reuses its own
HttpClientHandler; requests and response bytes are not rewritten. At most 1,024
fixture pools are allowed and all are owned by the worker. Modern/Standard
SocketsHttpHandler pooling remains unchanged.

## Framework bridge

Each request gets its own randomly named-pipe connection to the persistent
worker-owned cache. A bounded, length-prefixed binary envelope carries method,
absolute URI, HTTP version, separate HTTP/content header arrays, binary body,
status, and reason phrase. Header values (notably `Set-Cookie`) are not joined.
Explicit Content-Length values are retained (including HEAD responses); buffering
does not invent a Content-Length when none was declared.
This is an IPC envelope around framework HTTP messages, **not** an HTTP parser.

Frames are bounded to 64 MiB, bodies to 60 MiB, individual strings to 1 MiB,
header entries to 512, and values per entry to 4,096. Encoding checks frame and
body budgets before each write and caps buffer capacity at the corresponding
limit; string byte lengths are checked before allocating their UTF-8 payloads.
The worker accepts at most 32 concurrent connections. Invalid lengths, truncated
frames, unexpected message kinds, misplaced headers, and trailing data are
rejected. This test-only bridge buffers bodies; it is not a production streaming
transport.

Cancellation closes the request pipe and cancels the worker's origin request.
Worker/protocol errors poison front-end health instead of becoming successful
fallback responses. Readiness verifies the launched front-end PID, exact
implementation provenance, and worker handshake. Health and process ownership
are checked again after the suite, before accepting results. The worker exits
if its parent dies; normal cleanup stops only explicitly launched PID trees
and removes only that run's uniquely created content directory.
Origin transport failures are a distinct IPC message: deliberate closed-origin
fixtures still exercise YARP's normal error behavior without misclassifying a
healthy worker as failed. Cancelling a response-body copy disposes its content
to interrupt blocked legacy reads, as well as cancelling the origin request.

## Results and gating

`results-<framework>-<default|filesystem>.json`, matching `*-provenance.json`,
and matching build/origin/proxy/client/comparison `*.log` files are written here
and ignored by Git. The checks workflow uploads them even on failure.

Every full run uses `expected-results.json` without rewriting it: previously
passing fixtures must still pass, and **every baseline fixture must be present**,
including known failures. New passes and changed known-failure categories are
reported. Single-fixture diagnostics cannot masquerade as a complete run.
`-Update` / `--update` is an explicit maintenance operation restricted to full
modern runs; review differences before using it.

`legacy-expectations.json` is a reviewed, target-scoped **exact-outcome overlay**,
not a rewritten baseline or modified suite. It accepts only the recorded header
formatting messages: Cache-Control directive ordering/case and Content-Type
optional whitespace. A different reported outcome on a previously passing
fixture, or any missing baseline fixture, still fails. Raw pass/failure counts are
not inflated by these accepted exceptions. Modern runs cannot use the overlay.
The pinned suite checks most origin-header string equality after its request
sequence and cache assertions; diagnostics also demonstrate cached responses
for these formatting-only cases. Explicit header assertions can stop a fixture
earlier, so an accepted formatting outcome does not establish that every later
assertion ran.

```powershell
node --test compare-results.test.mjs worker-lifecycle.test.mjs
dotnet run --project ConformanceProtocol.Tests\ConformanceProtocol.Tests.csproj -c Release
```

The comparison tests cover missing passing/known-failure fixtures, empty output,
regressions, and improvements. Protocol tests cover metadata/body fidelity,
cookie boundaries, content separation, HEAD lengths, fragmented reads, invalid
lengths, truncation, trailing data, and cancellation.
Protocol size tests also cover exact frame/string boundaries, combined
metadata/header/body budgets, and rejection before buffer growth or later fields.
CI runs all six Windows cells and the four modern/Standard Linux cells.
The Windows-only lifecycle test additionally exercises binary PATCH forwarding,
multiple Set-Cookie fields, origin disconnects, cancellation during a stalled
body, and worker termination.
Its diagnostic output is saved in `worker-lifecycle.log`.
