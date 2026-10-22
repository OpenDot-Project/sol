<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Architecture and ownership

## Existing implementation

Sol's implementation is primarily C# 14 on .NET 10, built with MSBuild. The current managed projects are:

| Project | Responsibility |
|---|---|
| `OpenDot.Sol.Text` | Immutable UTF-8 byte snapshots, spans, and source coordinates. |
| `OpenDot.Sol.Diagnostics` | Source-bound diagnostics with validated byte ranges. |
| `OpenDot.Sol.Cst` | Lossless lexer, initial declaration/expression parser, and syntax models. |
| `OpenDot.Sol.Psi` | Typed immutable source views, snapshot identity/navigation, and explicit module grouping (Unreleased). |
| `OpenDot.Sol.Ast` | Immutable structural normalization, source origins, parameter expansion, and type shapes (Unreleased). |
| `OpenDot.Sol.Analysis` | Declaration/import lookup, module graphs, and partial composition in an explicit source distribution (Unreleased). |
| `OpenDot.Sol.Fir` | Closed native-subset FIR, compiler-owned bootstrap declarations, verification, and CFG definite assignment (Unreleased). |
| `OpenDot.Sol.Lowering` | C# LLVM emission, native bridge contract/loading, COFF/link execution (Unreleased). |
| `OpenDot.Sol.Driver` | Explicit-source parsing, CST/AST output, and declaration-analysis entry points. |
| `OpenDot.Sol.Cli` | Solc's independent argument parser, process entry, diagnostics, and output transactions. |

`tests/OpenDot.Sol.Tests` exercises these components, including malformed byte inputs, recovery,
depth limits, output rollback, and CLI behavior. The C++20 project under `src/native/launcher`
implements the Windows component launcher. It does not implement a parser, LLVM bridge, or GC.

The managed solution is `Sol.slnx`; `Build.proj` provides the higher-level MSBuild entry point.
PowerShell scripts in `eng` handle repository checks, process smoke tests, native building, and
component assembly. Generated files stay under ignored build/output directories.

## Compiler stages

The compiler design follows this order:

```text
UTF-8 source -> Lexer -> CST -> PSI -> AST -> FIR -> LLVM IR
```

Only source handling, lexing, and an initial CST subset are implemented in 0.0.1.
Source development additionally covers nominal/member declarations, typed PSI views, structural AST,
and declaration/body analysis. It also implements a closed [verified FIR/native subset](fir-native.md).
See [AST](ast.md) and [analysis](analysis.md) for the wider frontend boundaries.
The stages have the following responsibilities; complete language coverage remains pending:

- **CST** preserves spelling, tokens, trivia, and syntax structure.
- **PSI** provides typed source-structure views over syntax snapshots.
- **AST** normalizes syntax while retaining source origins.
- **FIR** resolves symbols, types, flow, nullability, checked effects, and storage/lifetime rules.
- **LLVM lowering** consumes verified FIR and produces LLVM IR for native artifacts or ORC execution.

FIR is the resolved frontend representation before LLVM. Flow graphs, root maps, and cleanup
tables belong to its analyses rather than a separately named intermediate language.
Frontend APIs must remain usable without loading LLVM. Consumer tools should share this frontend
instead of copying its grammar or starting a second semantic implementation.

## Native boundary and memory ownership

LLVM 23.1.3 is the selected backend version. Compiler semantics and lowering decisions remain in C#;
the bridge owns native API handles, marshaling, and lifetime/error translation. No C++/CLI
project was needed for the initial managed-only frontend. The development native bootstrap has
a C++20/CLI adapter and an unmanaged LLVM C-interface translation unit. It performs parse/verify/pass/
verify/object operations; instruction and semantic policy remains in C#. See [native AOT](fir-native.md).

Yellow Tea is the planned native Sol garbage collector. Its design uses precise, non-moving,
concurrent mark/sweep with short coordination phases; production collection must not fall back
to a full-heap stop-the-world scan. It is not implemented or benchmarked yet. Sol program objects
will live in the native runtime's heap, separately from the compiler's CLR objects.

The [Yellow Tea documentation](yellow-tea/index.md) specifies heap allocation, roots, handshakes,
barrier publication, weak references, sweeping, pacing, and validation. SolRT will link dynamically
by default; `--static-solrt-lib` selects static linkage. The native bootstrap now implements both
link modes. Its argument-lifetime arena and static literals do not implement Yellow Tea collection.

## Repository scope

This repository owns Solc, the shared frontend APIs, the planned native Sol runtime/Yellow Tea,
and the planned standard library. Its documentation covers those components and their interfaces.
Other tools document their own implementations in their own repositories.

The standard library belongs to the native Sol program environment, not the compiler's .NET runtime.
The development bootstrap embeds compiler-owned `std.core` declarations and specialized scalar/String
`std.stdio` output adapters. The wider standard library still needs implementation and runtime support.
In the published 0.0.1 release imports are syntax only.
