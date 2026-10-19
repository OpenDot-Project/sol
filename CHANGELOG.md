# Changelog

All notable changes to Sol are recorded here. Release names use `Sol X.Y.Z` and Git tags use `vX.Y.Z`.

## [Unreleased]

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
