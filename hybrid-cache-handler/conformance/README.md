# RFC 9111 conformance matrix

The pinned public `http-tests/cache-tests` suite exercises actual `net10.0`,
`netstandard2.0`, and `net472` handler assets in default HybridCache and streaming
filesystem modes. Modern and Standard assets run in .NET 10 hosts; Framework
assets run in a separate Windows .NET Framework worker, not on modern .NET.

**[Full conformance guide](https://damianh.github.io/http-libs/docs/hybrid-cache-handler/conformance/)**
covers prerequisites, the IPC bridge, runtime provenance, exact regression
gating, result artifacts, and harness tests.

Requires .NET 10 SDK, Node.js 22+, npm, and Git. Framework runs additionally
require Windows with .NET Framework 4.7.2 or newer. Run from this directory:

```powershell
.\run-conformance.ps1 -Framework net10.0
.\run-conformance.ps1 -Framework netstandard2.0 -FileSystem
.\run-conformance.ps1 -Framework net472 -FileSystem
```

```bash
./run-conformance.sh --framework netstandard2.0 --file-system
```

Omit `-FileSystem`/`--file-system` for default HybridCache. Single-fixture
`-TestId`/`--test-id` runs are diagnostics only, not a full-suite gate.
Results and provenance are target/mode-qualified and ignored by Git.
Every baseline fixture must be present. Exact reviewed legacy header-format
exceptions do not inflate raw pass counts or apply to modern runs. Only full
modern runs may update `expected-results.json` with `-Update`/`--update`.

HybridCache 10.8.0's upstream `net472` unsupported/untested warning remains
applicable and visible. Windows generally executes these targets on Framework
4.8, not an actual 4.7.2 runtime. These runs are not conformance certification.
Complete initial clone/install and builds before parallel local runs.

```powershell
node --test compare-results.test.mjs worker-lifecycle.test.mjs
dotnet run --project ConformanceProtocol.Tests\ConformanceProtocol.Tests.csproj -c Release
```
