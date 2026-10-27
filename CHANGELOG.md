<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Changelog

All notable changes to Sol are recorded here. Release names use `Sol X.Y.Z` and Git tags use `vX.Y.Z`.

## [Unreleased]

The next release target is **Sol 0.0.2**, tag `v0.0.2`. Source version metadata has advanced;
publication still requires the protected-branch, merged-commit and tag-build validation gates.
The published Sol 0.0.1 assets remain immutable.

### Added

- Explicit native upstream-commit OSV queries, strict paginated advisory evidence validation,
  and a separate Windows advisory workflow. A pinned upstream zlib fix, libxml2 HTML/XInclude
  removal and hash-bound ASan/symbol qualification harden the native dependency inputs.
  The two libxml2 records still require shared-function applicability review and remain blocking;
  no CodeQL suppression or risk exemption is applied.
  Native staging also rejects missing/stale build bindings and changed payload/report/catalog
  bytes, preserving a hash inventory with the package's qualification evidence.

- Resident span mark/gray bitmaps and a coalescing ready-region queue, resumable descriptor scans,
  bounded allocation assists, Idle/Sweep barrier fast paths, native diagnostic counters/histograms
  and an opt-in bounded event ring. Native model, descriptor, concurrency and profiling fixtures
  cover these experimental paths; no comparative performance or production-readiness claim is made.

- Bounded thread-owned small-allocation slot batches, epoch/detach/shutdown cache return and
  native cache diagnostics. Small refills prepare empty spans before managed slot publication.

- Active strong-handle root enumeration and allocation-identity validation for weak targets, avoiding
  scans of closed handle history in handshakes and of all weak handles per reclaimed object.
  Native inventory counters and ASan fixtures cover address reuse, concurrent close and identity exhaustion.

- Yellow Tea region preparation and release outside the heap mutex, with pending commit permits,
  conservative retirement accounting and native-safe refill. Deterministic ASan harness probes cover
  reserve/commit failure, epoch changes during preparation and collector progress during a paused refill.

- Experimental Yellow Tea span storage with 44 size classes, reusable slots, epoch-sealed sweep
  publication, dedicated Windows large regions and committed/reserved accounting. Heap limits now
  cover physical managed commitment; native ASan checks exercise slot reuse and poisoned slack.

- Optional native-component assembly with pinned upstream notices, payload hash verification and
  relocated private-runtime AOT/ORC qualification. Distributions omit build-machine SDK paths;
  native EXE/DLL linking uses an x64 developer shell's installed libraries.

- Embedded LLD.COFF and LLD.Common 23.1.3 linking, including static archives, serialized driver
  re-entry, and diagnostics without external linker executables.
- Dedicated bounded anonymous-pipe bitcode transfer to the native ORC guest, with malformed-frame
  rejection and child cleanup on transport or output initialization failure.
- Direct LLVM 23.1.3 C++ API construction through a thin C++/CLI operation bridge, session-scoped
  handles, binary bitcode transfer to ORC and Sol containers, and LLVM-printer-only inspection.
  Native and mixed-mode CRT ownership is separated; first-party MSVC projects use `/W4 /WX`.
- Experimental native concurrent mark/sweep baseline with precise descriptors, generated root slots,
  insertion barriers, cooperative handshakes, strong/weak handles, and heap-limit validation.
- Collectible scalar/String array literals and UTF-8 String operations, source integral math
  overloads, native Throwable carrier roots/suppression, and native model/ASan/concurrency checks.
- Process-isolated native ORC bin execution, scalar C-ABI DLL/static-library output, deterministic
  closed-subset Sol library containers, and host/guest runtime import checks. General source EH,
  the span allocator, external Sol library consumption and the full standard library remain pending.

- Pinned JetBrains native/C++/CLI cleanup and inspection, MSVC CppCoreCheck build settings,
  and a separate digest-verified Doxygen HTML/XML validation workflow for Windows.

- Verified native-subset FIR, independent type/call/literal and CFG assignment verification,
  `--emit=fir`/`--emit=llvm-ir`, and a qualified LLVM 23.1.3 AOT executable path.
- Closed module-level generic free-function FIR specialization with declaration-ordered type
  arguments, bounded instance/body expansion, source-origin and selected-target verification, and
  FIR JSON schema 5 metadata. Pure free-function defaults now evaluate after explicit arguments in
  parameter order and can read earlier captured values. Effectful/unsupported defaults, per-instance
  semantic rebinding, generic owners/members, broader layouts and external generic exports remain pending.
- C++20/CLI LLVM bridge with native RAII, pre/post-pass verification, and COFF object generation.
- Provisional native SolRT with dynamic/static linking, UTF-8 arguments, scalar/String output,
  checked arithmetic/bounds failures, native execution/import tests, and an ASan ABI harness.
- Separate Windows native-backend CI with pinned LLVM acquisition and native CodeQL build coverage.
- Optional source-owned body analysis: receiver/call/lambda typing, access and override diagnostics,
  constructor initialization, structured assignment/null/effect checks, delegation contracts, and
  explicit unverified proof locations. This API alone does not certify native eligibility.
- Yellow Tea design documentation covering allocation, roots, barriers, collection phases,
  weak/sweep races, ABI, pacing, validation, and an explicit current implementation inventory.
- `--static-solrt-lib` selection; SolRT defaults to dynamic linkage, with both modes implemented
  by the qualified native AOT bootstrap.
- Commented source language contracts in `spec/*.sol` and usage examples in `spec/examples/**/*.sol`,
  including planned semantics, implementation boundaries, syntax/AST tests, and published DocFX resources.
- Default-expression ownership and inherited override-default queries, parameter scope diagnostics,
  empty-vararg/default distinction, and original default expression origins in call inspection.
- Generic substitution workspace, parameter owner/ordinal queries, substituted base/constraint checks,
  and optional direct-function call analysis with candidate diagnostics and inspection JSON.
- Named/default/vararg argument mapping, exact local ref/out checks, structural generic inference,
  intrinsic conversion ranking, and explicit deferred outcomes for unsupported call paths.
- Written type identity and lexical binding SDKs, explicit base/cycle checks, source generic parameters,
  ref-like argument gates, scope/capture inspection, and source-analysis driver composition.
- Structural AST library and `--emit=ast` inspection output, with grouped-parameter expansion,
  source ownership, normalized type shapes, explicit body/argument models, and invariant verification.
- Declaration-analysis SDK for explicit source distributions: module/member/import lookup,
  public re-exports and aliases, dependency SCCs, partial composition, and structural signature checks.
- Nominal/member CST syntax: class, struct/ref struct, interface, enum payloads, constructors,
  properties/accessors/indexers, annotations, delegation, generics/constraints, and extern signatures.
- Contextual generic closing and explicit generic applications without rewriting lexer tokens.
- `OpenDot.Sol.Psi` typed immutable source views with snapshot identity, navigation, range lookup,
  parameter/declaration facades, cancellation, and explicit logical-module grouping.
- Development CST support for braced control flow, four for-loop forms, switch cases, try/catch/finally,
  defer/unsafe blocks, typed and labelled lambdas, anonymous functions, and structured call arguments.
- Control-flow recovery, structural source-span checks, token-deletion mutations, and bounded random-token regressions.
- Project README and contribution guide, with installation, build, validation, and review instructions.
- DocFX guides covering the released CLI, frontend APIs, architecture, and implementation status.

### Changed

- Clarified the conventional Sol source layout: lowercase directories, PascalCase filenames,
  and module-matching paths under the source root, while explicit alternative paths remain valid.
- Accept ordered named/positional mixing and a single vararg at any parameter position; named varargs
  require arrays and vararg defaults are used only when omitted. Preserve trailing lambda destinations
  across defaults/varargs, including calls without parentheses. Overrides cannot redefine defaults.
- Preserve vararg passing mode in both structural and resolved declaration signatures.
- Distinguish statement-start `default<T>()` from a switch `default:` recovery boundary.
- Array prefixes include the nullable element type: `[]String?` equals `[](String?)`;
  use `([]String)?` for a nullable array reference.
- Split parser control flow and function expressions into focused source files; preserve the original lossless token stream.
- Clarified the source-file size convention: split above 750 physical lines, with an absolute 1,500-line ceiling; no CI size gate.

### Fixed

- Ignore empty backend environment overrides when resolving the packaged native component for AOT,
  ORC and LLVM inspection, instead of failing with an empty-path error.

## [0.0.1] - 2026-10-19

### Added

- Initial Solc frontend infrastructure in C# 14 on .NET 10 for Windows.
- Immutable UTF-8 source snapshots, byte spans, Unicode scalar and UTF-16 source coordinates.
- Lossless lexical tokens and trivia, with diagnostics for malformed UTF-8, literals, and escapes.
- Initial CST declaration and expression parsing, grouped parameters, array/tuple/lambda syntax, bounded recovery, and module indexing independent of file paths.
- A first-party Solc CLI with CST JSON output, structured diagnostics, response files, and transactional output replacement.
- A native C++20 Windows launcher in `OpenDot::Sol::Launcher`, with a private .NET runtime and process-tree ownership.
- Locked MSBuild solution and automated frontend regression tests.
- Separate Windows 2025 workflows for build/test/coverage, DocFX, CodeQL, package smoke tests, OSV-Scanner, zizmor, and actionlint.
- Codecov OIDC coverage upload and Codacy source-analysis configuration.

Release name: **Sol 0.0.1**. Git tag: **`v0.0.1`**. This is an initial frontend implementation. Full grammar, semantic analysis, native code generation, Yellow Tea, and the standard library are not yet implemented. Unsupported productions are rejected explicitly; CST output is not a claim of semantic validity.
