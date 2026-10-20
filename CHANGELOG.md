# Changelog

All notable changes to Sol are recorded here. Release names use `Sol X.Y.Z` and Git tags use `vX.Y.Z`.

## [Unreleased]

### Added

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
