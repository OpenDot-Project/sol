<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Native implementation tooling

The native solution `src/native/Sol.Native.sln` groups the Windows launcher, LLVM C++ component/bridge, ORC host, and SolRT
projects for C++ and C++/CLI tooling. The managed solution remains independent of Visual C++ build
targets. Build the managed dependencies and backend as described in [native AOT](fir-native.md)
before inspecting the native solution.

`eng/build-backend.ps1` prepares the LLVM C++ static-link dependencies through the exact vcpkg
commit/archive and Windows static triplet in `eng/llvm-native-dependencies.json`. The selected
compression/XML libraries are zlib, zstd, and libxml2 core. Downloads are digest-checked; upstream
package builds stay under `out/dependencies`. Their license inventory is copied under
`out/native/llvm-native/dependency-licenses`. First-party native projects remain MSBuild projects.

The asset provider tries a bounded upstream MSYS2 mirror list when the primary host fails or returns
bytes with the wrong digest. Every candidate uses HTTPS and the same pinned SHA-512. Downloads are
staged in a temporary file; only verified bytes replace the destination. A verification page or an
exhausted mirror list fails acquisition rather than entering the dependency cache.

## Formatting and inspections

The root `.clang-format` is the formatting contract: Microsoft style with `LineEnding: DeriveLF`.
JetBrains tools are pinned in `.config/dotnet-tools.json`. From the repository root:

```powershell
dotnet tool restore
./eng/native-tools.ps1 -Mode Cleanup
./eng/native-tools.ps1 -Mode Inspect
```

Cleanup invokes `jb cleanupcode` with the formatting-only profile against the native solution;
review its diff before committing. Inspect invokes `jb inspectcode` and produces
`out/jetbrains/native.sarif.json`. It reports warning-level findings and fails on errors. Personal
settings layers are disabled so machine-specific IDE preferences do not change repository results.
These commands require MSVC, the .NET 10 reference packs, built managed bridge dependencies, and
`LLVM_ROOT`. They select the same supported MSVC platform toolsets as the backend build.

## MSVC Core Guidelines analysis

Native build helpers pass these properties explicitly:

```text
/p:EnableCppCoreCheck=true
/p:RunCodeAnalysis=true
/p:CodeAnalysisRuleSet=CppCoreCheckRules.ruleset
```

`src/native/NativeAnalysis.props` also enables them for direct project builds and requires `/W4 /WX`
for first-party compiler diagnostics. MSVC analyzes the plain native launcher, runtime, ORC host,
and native LLVM C++ component. Upstream SDK headers have a separate diagnostic scope; this does not
disable first-party warnings. The managed `Compiler.cpp` adapter does
not run MSVC native Core Check; it is covered by InspectCode's C++/CLI analysis. A skipped managed
translation unit is not evidence of native Core Guidelines validation. See Microsoft's
[Core Check documentation](https://learn.microsoft.com/en-us/cpp/code-quality/using-the-cpp-core-guidelines-checkers)
and JetBrains' [command-line C++ inspections](https://www.jetbrains.com/resharper-cpp/features/).

MSVC findings remain visible in the build log, including ABI pointer/conversion diagnostics.
Enabling the ruleset does not imply a finding-free result; inspection errors and warnings are reported separately.

## Native API documentation

```powershell
./eng/test-doxygen.ps1
```

The helper downloads the exact Windows Doxygen version in `eng/doxygen.json`, verifies SHA-256,
checks the executable version, and builds `Doxyfile`. HTML and XML go to `out/doxygen`; documentation
warnings fail the build. Inputs are restricted to `src/native` and its README, including C++/CLI
syntax. Extraction includes the bootstrap implementation surface; it does not certify ABI stability,
collector readiness, or complete comments on every symbol.

The separate Doxygen workflow runs on Windows 2025 and archives HTML/XML. It does not publish a site.
DocFX continues to own the managed APIs and architecture guides. The native backend workflow runs
InspectCode after a real backend build and retains its SARIF report.
