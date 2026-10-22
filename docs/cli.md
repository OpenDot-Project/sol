<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Solc command-line reference

This page describes Sol 0.0.1. The compiler accepts source paths and options directly:

```text
solc <SOURCE.sol>... [OPTIONS]
```

Development builds additionally accept `--emit=ast`, producing `<source-basename>.ast.json`.
This Unreleased option writes the [structural AST](ast.md), without running declaration/type analysis.
It uses the same full-input preflight and transactional output protections as CST emission.
They also support `--emit=fir`, `--emit=llvm-ir`, and native AOT `bin` output for the
[verified bootstrap subset](fir-native.md). The native backend component is built separately;
`--static-solrt-lib` selects static SolRT instead of default dynamic linkage. These capabilities
are not in the published 0.0.1 component.

Unreleased builds now support [library artifacts](library-artifacts.md) and process-isolated ORC bin
execution. `-C llvm-compiler=orc` defaults to `--emit=jit`; `--` then introduces program arguments,
and `-o`/`--out-dir` are rejected for execution. `--emit=llvm-ir` still writes inspection text only.
Native/library outputs require the separately built backend, and their verified source subset is
documented in the native guide. The option table below retains the published 0.0.1 behavior.

There are no `run`, `check`, or `explain` subcommands, and no `--entry` option.
Diagnostic explanation uses the `--explain CODE` option. The CLI parser belongs to Solc;
other tools have their own command-line contracts.

## Information options

| Option | Behavior |
|---|---|
| `-h`, `--help` | Print help and exit successfully. |
| `-V`, `--version` | Print `solc 0.0.1`. |
| `--verbose` | With `--version`, show edition, target, frontend scope, and planned LLVM version. |
| `--print edition-list` | Print `2026`. |
| `--print target-list` | Print `x86_64-pc-windows-msvc`. This identifies the configured target, not available native code generation. |
| `--explain CODE` | Print the explanation of a known diagnostic, for example `--explain SOL1100`. |

Help, version, print, and explanation requests cannot be combined with source files or compilation
options, or with a different information request. Unknown query keys and diagnostic codes are errors.
`--verbose` alone does not select an information request.

## Parsing and output options

| Option | Accepted values and defaults |
|---|---|
| `--emit` | `cst` writes CST JSON. `link` is the default and returns `SOL0001` because native compilation is unimplemented. |
| `-o PATH` | One output path. CST/AST require one source; development FIR/LLVM/native emission accepts one combined output for multiple sources. |
| `--out-dir DIR` | Output directory, defaulting to the current directory. With explicit `-o`, that file path is used instead. |
| `--edition` | Only `2026`; default `2026`. |
| `--target` | Only `x86_64-pc-windows-msvc`; the default. |
| `--artifact-type` | `bin`, `cdylib`, `staticlib`, or `solib`; default `bin`. Accepted and validated, but does not generate these artifacts in this release. |
| `--static-solrt-lib` | Unreleased: select static SolRT linkage; the default is dynamic. A flag without a value, implemented by the development native AOT bootstrap. |
| `-C llvm-compiler=VALUE` | `aot` or `orc`; default `aot`. Accepted and validated; neither backend is implemented yet. |
| `--error-format` | `human` or `json`; default `human`. |
| `--error-limit N` | Positive integer, default 100 diagnostics per source. |
| `--color` | `auto`, `always`, or `never`; default `auto`. |

Long value options accept `--option=value` or `--option value`. `-Cllvm-compiler=orc` is also
accepted. Identical repeated values are accepted; conflicting repeats are rejected. There is no
general "last option wins" behavior. `-o` is a separate option followed by its value, not a `-oPATH` shorthand.

```powershell
solc first.sol second.sol --emit=cst --out-dir out/cst --color=never
solc example.sol --emit=cst --artifact-type=solib -C llvm-compiler=orc -o out/example.cst.json
```

Both examples request CST only. The second does not build a Sol library or execute ORC.
Sources must have a `.sol` extension. `--` stops option interpretation for subsequent source paths.
Duplicate source paths are rejected without regard to case on Windows.

Without `-o`, each result is named `<source-basename>.cst.json` in the output directory. Distinct
sources with the same basename collide and are rejected; module names do not determine output paths.
All sources are parsed before output is committed. Any parse error prevents output replacement
for the entire source set. Outputs cannot overwrite sources. Inputs are rechecked before writing;
an observed source change aborts the operation. Output staging and rollback protect against ordinary
write failures; this is not a crash-durable transaction spanning an entire filesystem.

## Response files

`@FILE` expands a strictly decoded UTF-8 response file. Each nonempty physical line is one complete
argument. Lines are not shell commands: do not add shell quoting around paths containing spaces.
There is no comment syntax, environment substitution, or nested response-file expansion. Relative
paths resolve from the invoking process's current directory, not the response file's directory.

```text
example.sol
--emit=cst
-o
out/example.cst.json
```

```powershell
solc '@args.rsp'
```

`@@name.sol` spells a literal argument beginning with `@`. After an outer `--`, response-file
expansion is disabled. A line beginning with `@` inside a response file is rejected as nesting.

## Diagnostics and exit status

Human source diagnostics use `path(line,column): error CODE: message`, with one-based line and
Unicode scalar column. In `auto` mode, error labels are colored only when stderr is interactive and
`NO_COLOR` is absent. JSON source diagnostics are emitted one object per stderr line and contain:

| Field | Meaning |
|---|---|
| `schema_version` | Currently 1. |
| `origin`, `code`, `severity`, `message` | Compiler origin and diagnostic identity/content. |
| `path` | Source snapshot path. |
| `byte_start`, `byte_length` | Zero-based UTF-8 byte range. |
| `line`, `column` | One-based line and Unicode scalar column. |
| `utf16_column` | Zero-based UTF-16 code-unit column for editor integration. |

The unsupported-stage diagnostic also respects JSON mode but has no source-position fields.
Command-line validation, I/O errors, cancellation, and internal failures currently use separate
text/exit paths; `--error-format=json` does not promise JSON for every possible failure.

| Exit code | Meaning |
|---|---|
| 0 | Requested information or CST operation completed. |
| 1 | Source diagnostic, I/O failure, or unimplemented native compilation stage. |
| 2 | Invalid command-line usage or conflicting output paths. |
| 70 | Internal compiler/launcher failure, including an incomplete component. |
| 130 | Cooperative cancellation. |

Key diagnostic families are `SOL0001` (unimplemented stage), `SOL0002` (I/O), `SOL1001`–`SOL1010`
(lexical input/literals), and `SOL1100`–`SOL1104` (module requirement, syntax, unsupported production,
declaration brace placement, and depth budget). Use `solc --explain CODE` for a known individual code.
