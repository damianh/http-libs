---
title: "Contributing"
description: "Build and test the libraries, edit authoritative documentation, and preview the Atoll and Lagoon site."
order: 100
section: "Contributing"
---

# Contributing

Bug reports should be accompanied by a reproducible test case in a pull request.
The repository is licensed under
[MIT](https://github.com/damianh/http-libs/blob/main/LICENSE).

## Build and test

Install the **.NET 10 SDK**. Run every command in this guide from the repository
root, not from a library or documentation subdirectory.

Each library has its own `build.cs` and solution filter:

- [`hybrid-cache-handler`](https://github.com/damianh/http-libs/tree/main/hybrid-cache-handler)
- [`file-distributed-cache`](https://github.com/damianh/http-libs/tree/main/file-distributed-cache)
- [`structured-field-values`](https://github.com/damianh/http-libs/tree/main/structured-field-values)
- [`signatures`](https://github.com/damianh/http-libs/tree/main/signatures)
- [`forwarded-headers`](https://github.com/damianh/http-libs/tree/main/forwarded-headers)

For example, build and test the signatures library:

```sh
dotnet run signatures/build.cs -- build
dotnet run signatures/build.cs -- test
```

Replace `signatures` with another library directory to use its build script.
The scripts restore dependencies and use the shared
[BuildHelpers](https://github.com/damianh/http-libs/tree/main/.github/BuildHelpers).

To build the whole solution and run all tests:

```sh
dotnet build http-lib.slnx
dotnet test --solution http-lib.slnx
```

The repository selects Microsoft.Testing.Platform in
[`global.json`](https://github.com/damianh/http-libs/blob/main/global.json), so use
the explicit `--solution` option when testing the solution.

The six HTTP cache packages also ship `netstandard2.0` and `net472` assets.
See [framework compatibility](/docs/hybrid-cache-handler/compatibility/#compatibility-validation)
for forced-asset tests, Windows Framework execution, and package-only consumers.
The [conformance harness](/docs/hybrid-cache-handler/conformance/) additionally
requires Node.js 22+, npm, and Git; those are not requirements for building
the documentation site.

## Documentation sources of truth

Detailed documentation lives in
[`docs/Docs/Content/docs`](https://github.com/damianh/http-libs/tree/main/docs/Docs/Content/docs),
grouped by library and topic. Edit those Markdown pages for usage, configuration,
reference, limitations, and sample guidance.

- Root and package READMEs are concise entry points for GitHub and NuGet. Keep
  their installation examples and critical caveats useful, and link to the full
  site instead of maintaining duplicate detailed guides.
- Library APIs and defaults are defined by the source and tests in each
  library's `src` and `test` directories. Verify examples and option tables
  against them when changing documentation.
- Runnable samples, benchmarks, and conformance tools stay in their existing
  source directories. Link to them rather than copying or moving implementations
  into the documentation site.
- The site shell, schema, routes, and theme configuration live in
  [`docs/Docs`](https://github.com/damianh/http-libs/tree/main/docs/Docs). The site
  uses the .NET-native Atoll generator and the same Lagoon documentation theme as
  [microstack](https://github.com/damianh/microstack/tree/main/docs/Docs).

The site describes the current main-branch libraries; it is not a separately
versioned documentation set. This setup needs no Node toolchain, custom domain,
or generated API-documentation pipeline.

## Author a page

Use a descriptive, stable filename under the appropriate library group. Every
page must start with frontmatter containing `title`, `description`, an integer
`order`, and a string `section`. Use optional `sidebarLabel` for a short menu
label without shortening the page title or search results; `topics` is an
optional list:

```yaml
---
title: "File distributed cache configuration"
sidebarLabel: "Configuration"
description: "Configure local cache storage, expiration, eviction, and HybridCache integration."
order: 2
section: "File distributed cache"
topics:
  - caching
  - configuration
---
```

Follow frontmatter with a Markdown H1 and focused sections. The exception is
`home.md`: its content appears below the Lagoon Hero, so it must not repeat the
hero title as an H1 or add a second hero. Lagoon supplies navigation and a table
of contents; do not add manual page tables of contents.

### Links

- **Within site Markdown:** use `/docs/<slug>/`, with a trailing slash. For
  example, `/docs/file-distributed-cache/overview/`. Do not include `/http-libs`
  in these source links: the site configuration applies the deployment prefix.
- **From READMEs:** use absolute URLs, such as
  `https://damianh.github.io/http-libs/docs/file-distributed-cache/overview/`,
  so links work on both GitHub and NuGet.
- **Source files and folders:** use absolute GitHub `blob/main` or `tree/main`
  URLs pointing at real repository paths. For example, the file-cache sample
  is at `file-distributed-cache/samples/FileDistributedCacheSample`, not in a
  root-level `samples` directory.
- When splitting a guide, update its incoming links and moved section anchors.
  Existing external README fragment links cannot be redirected by the site.

## Build, check, and preview the site

From the repository root:

```sh
dotnet run docs/build.cs -- build
```

This restores the repository-local Atoll CLI, builds the docs project, generates
the static site, and checks the generated output. You do not need a global Atoll
installation. The pinned tool is recorded in
[`.config/dotnet-tools.json`](https://github.com/damianh/http-libs/blob/main/.config/dotnet-tools.json).

To validate an existing generated artifact without rebuilding it:

```sh
dotnet run docs/build.cs -- check
```

The check command also runs negative self-tests to verify that invalid output is
rejected. Run `build` first when no artifact exists or source content has changed.

To build, check, and serve the local preview:

```sh
dotnet run docs/build.cs -- preview
```

Open the URL printed by the preview command, under the actual `/http-libs/`
deployment prefix. Check the landing page, a nested guide and direct reload,
navigation, search, code blocks, and light/dark and narrow-screen layouts.
Stop the preview with Ctrl+C.

Generated output lives in `docs/Docs/dist`; `docs/Docs/.atoll` is generator state.
Do not edit or commit either directory. The publishing destination is
[https://damianh.github.io/http-libs/](https://damianh.github.io/http-libs/).
