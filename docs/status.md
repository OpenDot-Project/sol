<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
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
These additions are not in the published 0.0.1 component. Parsing them alone does not establish
type/effect/escape/exhaustiveness validation or executable output. See the [frontend guide](frontend.md).

Nominal declarations now include class, struct/ref struct, object/companion, interface, enum payloads,
primary/secondary constructors, accessors/indexers, annotations, delegation, generics, constraints,
and function signature effects. Contextual generic closers preserve the physical lexer tokens.
The new `OpenDot.Sol.Psi` project exposes typed immutable source views and module grouping; see the
[PSI guide](psi.md). The [AST](ast.md) expands grouped parameters and normalizes written type shapes.
[Declaration analysis](analysis.md) now indexes source modules, public import/re-export lookup,
signature groups, and compatible partial declarations. Development Solc accepts `--emit=ast`.
Full expression type checking remains incomplete. A separate closed bootstrap subset now supports native output.
The [source binding SDK](source-binding.md) now resolves written type identities and generic parameters,
checks explicit bases and inheritance cycles, and indexes lexical scopes and capture candidates.
The optional [generic and call analysis](generic-calls.md) now substitutes owned type parameters,
checks specialized nominal constraints, maps direct module-function arguments, and selects supported
overloads. Unsupported receiver, lambda, inference, and conversion paths remain explicitly deferred.
The [argument contract](arguments.md) now covers ordered mixed calls, middle varargs, array-valued
named varargs, default origins/inheritance, and outside-lambda destinations. The commented
[source specification](source-specification.md) also explains planned language/runtime behavior;
its syntax/AST gate does not certify complete semantic implementation or native execution.

The optional [body analysis](body-analysis.md) adds receiver/member lookup, contextual expression and
lambda checking, constructor/override checks, structured definite assignment and smart casts, checked
effects, and conservative storage/capture checks. Its unverified source locations remain queryable;
full inherited overload/conversion/lambda solving, exceptional flow, and borrow-origin proof are still
incomplete. It does not produce verified FIR or native output.

The [Yellow Tea implementation inventory](yellow-tea/current-implementation.md) records experimental
concurrent mark/sweep, generated roots/barriers, handles, and native validation fixtures. The design
also specifies pending span allocation, pacing and broader qualification. No measured latency or
production-readiness guarantee is available. SolRT defaults to dynamic; static linkage is explicit.

The [verified FIR/native path](fir-native.md) admits a closed scalar/stateless subset, independently
checks FIR types/calls/CFG assignment, and lowers through a qualified C++20/CLI LLVM bridge to AMD64
COFF and a Windows executable. Default dynamic and explicit static SolRT modes are execution-tested.
Collectible Strings/scalar arrays and selected String/integral math APIs are available in development.
[Library artifacts](library-artifacts.md) now include scalar C DLL/staticlib output and closed-subset
Sol containers; external Sol module consumption remains pending. ORC runs in a separate native guest
process. General objects, source catches/defer/EH, generics, the complete allocator, and full borrowing
remain gated. The [standard library inventory](standard-library.md) lists implemented and pending modules.

### Remaining stages

1. Continue grammar edge coverage and source tooling against the shared CST/PSI surface.
2. Extend binding with full visibility, generic body validation, receiver-dependent references,
   generated members, and complete overload/expression typing.
3. Expand the verified FIR subset with full nullability, checked exception/cleanup CFG, borrowing,
   closures, generic specialization, and general object layouts.
4. Qualify complete Windows/ORC exception handling, library consumption and extended native ABI.
5. Complete and validate the native allocator, Yellow Tea concurrency invariants, and `std.core`;
   expand the standard library with module-level API contracts and tests.

Testing, malformed-input exploration, security analysis, and documentation evolve with each stage.
A consumer should not claim a compiler capability is available before that capability is implemented
and verified. No delivery dates or production-readiness guarantees are assigned by this roadmap.
