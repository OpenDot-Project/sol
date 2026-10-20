# Architecture and ownership

## Existing implementation

Sol's implementation is primarily C# 14 on .NET 10, built with MSBuild. The current managed projects are:

| Project | Responsibility |
|---|---|
| `OpenDot.Sol.Text` | Immutable UTF-8 byte snapshots, spans, and source coordinates. |
| `OpenDot.Sol.Diagnostics` | Source-bound diagnostics with validated byte ranges. |
| `OpenDot.Sol.Cst` | Lossless lexer, initial declaration/expression parser, and syntax models. |
| `OpenDot.Sol.Psi` | Typed immutable source views, snapshot identity/navigation, and explicit module grouping (Unreleased). |
| `OpenDot.Sol.Driver` | Explicit-source parsing, logical module grouping, and CST JSON output. |
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
Source development additionally covers nominal/member declarations and the first typed PSI views.
The later stages have the following intended boundaries:

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

LLVM 23.1.3 is the selected backend version. A C++20/CLI DLL will be introduced when actual LLVM
C++ API operations are implemented. Compiler semantics and lowering decisions remain in C#;
the bridge will own native API handles, marshaling, and lifetime/error translation. No C++/CLI
project is currently needed for the managed-only frontend.

Yellow Tea is the planned native Sol garbage collector. Its design uses precise, non-moving,
concurrent mark/sweep with short coordination phases; production collection must not fall back
to a full-heap stop-the-world scan. It is not implemented or benchmarked yet. Sol program objects
will live in the native runtime's heap, separately from the compiler's CLR objects.

## Repository scope

This repository owns Solc, the shared frontend APIs, the planned native Sol runtime/Yellow Tea,
and the planned standard library. Its documentation covers those components and their interfaces.
Other tools document their own implementations in their own repositories.

The standard library belongs to the native Sol program environment, not the compiler's .NET runtime.
Its implementation will be added with the compiler/runtime contracts it requires. Imports are
currently syntax only; this release does not provide standard-library implementations.
