# Changelog

All notable changes to Sol are recorded here. Release names use `Sol X.Y.Z` and Git tags use `vX.Y.Z`.

## [Unreleased]

### Added

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
