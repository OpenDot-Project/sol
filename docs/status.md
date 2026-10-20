# Status and roadmap

## Sol 0.0.1

Sol 0.0.1 is the first frontend release for Windows x86-64. It is useful for exploring the supported
syntax subset and developing source analysis infrastructure. It cannot compile or run a Sol program.

| Capability | Current state |
|---|---|
| Immutable UTF-8 source, byte spans, scalar/UTF-16 positions | Implemented. |
| Lossless lexer, comments/trivia, malformed-input diagnostics | Implemented initial lexical surface. |
| CST declarations and expressions | Initial subset; unsupported productions diagnosed explicitly. |
| Explicit module names independent of file paths | Parsed and indexed; no import binding. |
| CLI, response files, CST JSON, output transactions | Implemented for the CST stage. |
| Private .NET 10 component and native launcher | Released for Windows x86-64. |
| PSI/AST/FIR, symbol/type/effect/escape analysis | Planned. |
| LLVM 23.1.3 AOT and ORC, native artifacts and entry validation | Planned. |
| C++/CLI LLVM bridge | Added only when actual backend interop needs it. |
| Native runtime and Yellow Tea | Planned; no published latency measurements. |
| Standard library | Planned; imports in source examples do not imply available implementations. |

## Implementation order

### Current development branch (Unreleased)

The CST parser now includes braced `if/else`, the four `for` forms, `switch` with flat payload/type
patterns, `try/catch/finally` in statement/value contexts, `defer`, and `unsafe`. Function expressions
add typed/ref/out lambda headers, local return labels, anonymous functions, trailing lambdas, and
structured named/ref/out/spread call arguments. Casts and type tests are parsed with their precedence.
These additions are not in the published 0.0.1 component. They remain syntax support, without
type/effect/escape/exhaustiveness validation or executable output. See the [frontend guide](frontend.md).

Nominal declarations now include class, struct/ref struct, object/companion, interface, enum payloads,
primary/secondary constructors, accessors/indexers, annotations, delegation, generics, constraints,
and function signature effects. Contextual generic closers preserve the physical lexer tokens.
The new `OpenDot.Sol.Psi` project exposes typed immutable source views and module grouping; see the
[PSI guide](psi.md). The [AST](ast.md) expands grouped parameters and normalizes written type shapes.
[Declaration analysis](analysis.md) now indexes source modules, public import/re-export lookup,
signature groups, and compatible partial declarations. Development Solc accepts `--emit=ast`.
Expression type checking and executable output remain unimplemented.

### Remaining stages

1. Continue grammar edge coverage and source tooling against the shared CST/PSI surface.
2. Extend declaration analysis with resolved type identity, full visibility, base compatibility, and local scopes.
3. Resolve symbols, types, overloads, nullability, checked exceptions, ref/out storage, and ref-struct
   escape rules into verified FIR.
4. Introduce the LLVM bridge and native artifact/entry pipeline with ABI and exception-handling tests.
5. Implement and validate the native runtime, Yellow Tea concurrency invariants, and `std.core` bootstrap;
   expand the standard library with module-level API contracts and tests.

Testing, malformed-input exploration, security analysis, and documentation evolve with each stage.
A consumer should not claim a compiler capability is available before that capability is implemented
and verified. No delivery dates or production-readiness guarantees are assigned by this roadmap.
