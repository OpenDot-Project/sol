<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Development and validation

## Prerequisites and scope

Use Windows and PowerShell 7. `global.json` selects .NET SDK 10.0.402 with roll-forward disabled;
check `dotnet --version` from the checkout before building. All managed projects target
`net10.0-windows` and C# 14. NuGet uses the configured official source and committed dependency
lockfiles. First restore requires network access.

Managed work requires neither LLVM nor C++/CLI. To build the native component launcher, install
Visual Studio C++ tools, a Windows SDK, and MSVC v143 or v145 with C++20 support. The build helper
uses `vswhere` to locate an installation and an approved installed platform toolset.
The LLVM/C++/CLI bootstrap has its own [backend qualification](fir-native.md); a working launcher
build does not establish that qualification. [Native tooling](native-tooling.md) documents the
shared formatting configuration, JetBrains cleanup/inspection, MSVC Core Check, and Doxygen.

## Restore, build, and test

From the repository root:

```powershell
dotnet restore Sol.slnx --locked-mode
dotnet build Sol.slnx --no-restore --configuration Debug
dotnet test --solution Sol.slnx --no-restore --configuration Debug --report-xunit-trx --results-directory out/test-results/debug
./eng/test-cli.ps1 -Configuration Debug
```

The root MSBuild entry point can run the same managed graph:

```powershell
dotnet msbuild Build.proj /t:Test /p:Configuration=Debug
```

Tests use xUnit with Microsoft.Testing.Platform. Release coverage uses the Coverlet MTP integration:

```powershell
dotnet test --solution Sol.slnx --no-restore --configuration Release --coverlet --coverlet-output-format cobertura --report-xunit-trx --results-directory out/test-results/release
./eng/test-cli.ps1 -Configuration Release
```

Inspect the produced TRX and Cobertura files under `out/test-results`. A passing CLI smoke test
checks version output, CST structure, rejection of a forbidden subcommand, and a malformed source
that must not produce output. Its negative cases intentionally invoke nonzero child exit codes;
the script checks those values before resetting its final native status for the CI wrapper.

Run the managed command directly while developing:

```powershell
dotnet ./src/managed/OpenDot.Sol.Cli/bin/Debug/net10.0-windows/OpenDot.Sol.Cli.dll tests/fixtures/main.sol --emit=cst -o out/main.cst.json
```

This direct invocation uses the selected development runtime. The release launcher instead uses
the private runtime distributed inside the component.

## Build and package the launcher

```powershell
./eng/build-launcher.ps1
./eng/package.ps1
```

Restore the solution first. Packaging publishes the managed application framework-dependently,
builds the native launcher, downloads the runtime pinned in `eng/runtime.json`, verifies SHA-512,
assembles licenses and payloads, runs version/CST/AST/FIR smoke tests, and writes a file inventory with
SHA-256 values in `component.json`. It outputs a versioned ZIP and `.sha256` companion in `out/packages`.

This development package contains the frontend and private .NET runtime. Its capability inventory
does not advertise LLVM output: the LLVM C++ component, bridge, and native execution dependencies
are not included. Packaging checks that an LLVM output request fails without creating an artifact,
even when the packaging machine has a development backend installed. Native compilation and LLVM
inspection are qualified separately by the native backend workflow; backend distribution remains pending.

The script refuses to overwrite an existing versioned ZIP. For another local verification, select
a fresh output directory, for example `./eng/package.ps1 -OutputDirectory out/packages-check-2`.
Do not delete or replace a published release to rerun a local check. Packaging uses the component's
private runtime rather than an installed system runtime; NativeAOT, SDK self-contained, single-file,
trimming, and ReadyToRun publication are disabled.

## Build the documentation

```powershell
dotnet tool restore
dotnet build Sol.slnx --no-restore --configuration Release
./eng/test-documentation.ps1
dotnet docfx docfx.json --warningsAsErrors
```

DocFX is pinned by `.config/dotnet-tools.json`. The build combines the Markdown guides with generated
API metadata for Text, Diagnostics, CST, PSI, AST, Analysis, and Driver. The resulting site is `out/docfx/site`.
Open `out/docfx/site/index.html` or use `dotnet docfx serve out/docfx/site` for local browsing.
The CI workflow validates and archives the site; it does not deploy GitHub Pages.

Keep root overview/contribution files and the guides synchronized. Use relative Markdown links
inside this repository, and avoid links to generated files that only exist after a local build.
Do not copy private design documents into public guides without reviewing their scope and claims.

## CI responsibilities

Every repository workflow runs on Windows 2025. Jobs are separated by responsibility:

| Workflow | Validation |
|---|---|
| `build.yml` | Debug/Release tests, process smoke, TRX/Cobertura artifacts, separate OIDC Codecov upload. |
| `docfx.yml` | Locked restore, metadata/site build with warnings as errors, output boundary checks. |
| `doxygen.yml` | Digest-pinned Doxygen, native/C++/CLI HTML and XML, documentation warnings as errors. |
| `native.yml` | AOT/ORC and native library consumers, SolRT model/ASan/concurrency harness, JetBrains inspection, and required managed native-orchestration coverage. |
| `codeql.yml` | Manual traced builds and security-extended analysis for C# and native C++. |
| `security.yml` | Digest-verified actionlint, offline zizmor audit, and OSV scanning of tracked NuGet lockfiles. |
| `package.yml` | Component assembly, private-runtime smoke, and archive artifact. |

CodeQL currently covers the implemented C# and plain C++ launcher, not a hypothetical mixed-mode
bridge. OSV's current workflow scans NuGet lockfiles; it is not a complete runtime/native SBOM audit.
Codacy source analysis is configured separately from these workflows. Coverage upload success is
tracked independently of whether tests passed. Fork pull requests do not run the trusted Codecov
upload job. Dependency updates are proposed through Dependabot for NuGet and Actions.

Current arbitrary-byte regressions exercise losslessness, limits, and termination. Native fuzzing,
sanitizer harnesses, full-stage compilation, and GC tests will be added with those implementations;
they are not provided by a green managed test run today.

## Release process

Changes enter `main` through protected pull requests and required checks. After the merged commit
passes CI, maintainers create a version tag such as `v0.0.1`. The tag triggers fresh test and package
runs. Release assets come from that exact successful tag Package run, rather than a developer's
local archive or a pull-request artifact.

Before publishing, verify archive SHA-256, component identity, every inventory hash/size, and launcher
smoke from the extracted package. The release title is `Sol X.Y.Z`; notes state actual capabilities
and remaining limitations. Version tags are protected from update/deletion. This process publishes only the Sol compiler component owned by this repository.
