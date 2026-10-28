<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Solc command-line reference

This page describes Sol 0.1.0. Solc has one compilation command; it has no `run`, `check`, or
`explain` subcommands and no `--entry` option. `--explain CODE` is an information flag. The CLI
parser belongs to Solc; Solfmt, Sol-Tidy, Shake, and Solup have independent command-line contracts.

```text
solc <SOURCE.sol>... [OPTIONS]
solc <SOURCE.sol>... -C llvm-compiler=orc --emit=jit [OPTIONS] -- [PROGRAM-ARG...]
```

The default backend is AOT and the default emission is a linked artifact. Selecting the ORC backend
defaults to JIT execution of a bin in a separate native guest process. The frontend component
supports source inspection and verified FIR emission; it cannot perform LLVM lowering, linking, or
ORC execution. Native outputs require the native component.

## Information options

| Option | Behavior |
|---|---|
| `-h`, `--help` | Print help and exit successfully. |
| `-V`, `--version` | Print the product version. |
| `--verbose` | With `--version`, print edition, target, frontend stages, backend modes, and LLVM version. |
| `--print KEY` | Print `edition-list` (`2026`) or `target-list` (`x86_64-pc-windows-msvc`). |
| `--explain CODE` | Print a known diagnostic description, for example `--explain SOL1100`. |

Information requests cannot be combined with source files or compilation options. Unknown print keys
and diagnostic codes are errors. `--verbose` modifies `--version`; it does not select an information
request by itself.

## Compilation options

| Option | Accepted values and behavior |
|---|---|
| `--emit` | `cst`, `ast`, `fir`, `llvm-ir`, `link`, or `jit`. Default is `link` with AOT and `jit` with ORC. `cst`/`ast` write JSON; `fir` writes independently verified FIR JSON; `llvm-ir` writes printer output from a constructed native module; `link` creates the selected native artifact; `jit` runs a bin through ORC. |
| `-o PATH` | Select one output path. CST/AST require exactly one source. FIR, LLVM IR, and linked native output can combine an explicit source set into one output. |
| `--out-dir DIR` | Set the directory for the default output name; default is the current directory. |
| `--edition=2026` | Select the only supported source edition. |
| `--target TRIPLE` | Select the only supported target, `x86_64-pc-windows-msvc`. |
| `--artifact-type TYPE` | Select `bin`, `cdylib`, `staticlib`, or `solib`; default `bin`. Applies to AOT-linked output, not ORC JIT execution. |
| `--static-solrt-lib` | Select static SolRT linkage. Dynamic SolRT linkage is the default. |
| `-C llvm-compiler=VALUE` | Select `aot` or `orc`; default `aot`. ORC defaults to `--emit=jit`. |
| `--error-format` | Select `human` or `json` source diagnostics; default `human`. |
| `--error-limit N` | Set a positive diagnostic limit; default 100 per source. |
| `--color` | Select `auto`, `always`, or `never`; default `auto`. |

Long value options accept `--option=value` or `--option value`. `-Cllvm-compiler=orc` is also
accepted. Identical repeated scalar options are accepted; conflicting repeats are rejected. `-o` is
a separate option followed by its value, not a `-oPATH` shorthand.

```powershell
solc first.sol second.sol --emit=cst --out-dir out/cst --color=never
solc spec/examples/native/FirstProgram.sol -C llvm-compiler=aot --artifact-type=bin -o out/Hello.exe
solc spec/examples/native/FirstProgram.sol -C llvm-compiler=orc -- Alice 20
```

The second example emits an executable; the third executes the program with `Alice` and `20` as its
arguments. Program arguments after `--` are meaningful only for ORC JIT. For compilation, `--` stops
option parsing so subsequent `.sol` paths are treated as inputs. ORC JIT rejects `-o` and `--out-dir`.

Sources must use the `.sol` extension. Duplicate source paths are rejected case-insensitively on
Windows. CST/AST outputs default to `<source-basename>.cst.json` or `<source-basename>.ast.json`.
Native outputs use the selected artifact extension; the exact sidecar outputs for DLL/LIB linkage
are described in [native and Sol library artifacts](library-artifacts.md). Distinct input sources
with colliding default output names are rejected.

All sources are read and checked before output is committed. Any parse or analysis failure leaves
requested outputs untouched. Solc rejects outputs that overwrite a source or collide with another
output. Source bytes are rechecked before write. Staging and rollback protect against ordinary write
failures; this is not a crash-durable transaction spanning an entire filesystem.

## Response files

`@FILE` expands a strictly decoded UTF-8 response file. Each nonempty physical line is one complete
argument. Lines are not shell commands; do not add shell quoting around paths containing spaces.
There is no comment syntax, environment substitution, or nested response-file expansion. Relative
paths resolve from the invoking process's current directory, not the response file's directory.

```text
spec/examples/native/FirstProgram.sol
-C
llvm-compiler=aot
--artifact-type=bin
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
Command-line validation, I/O errors, cancellation, launcher failures, and internal failures have
separate output paths; `--error-format=json` does not promise JSON for every possible failure.

| Exit code | Meaning |
|---|---|
| `0` | Requested information, inspection, linked output, or ORC execution completed successfully. |
| `1` | Source diagnostic, I/O failure, or compiler-stage failure. |
| `2` | Invalid command-line usage or conflicting output paths. |
| `70` | Internal compiler/launcher failure, including an incomplete component. |
| `130` | Cooperative cancellation. |

Key diagnostics include `SOL0001` (compiler stage could not produce the requested artifact),
`SOL0002` (source/response/output access), `SOL1001`–`SOL1010` (lexical input/literals), and
`SOL1100`–`SOL1104` (module, syntax, unsupported production, brace placement, and depth budget).
Use `solc --explain CODE` for a known individual code.