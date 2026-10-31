<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Status and roadmap

## Sol 0.1.0

Sol 0.1.0 ships two Windows x86-64 components: a frontend package for CST/AST/verified-FIR
inspection, and a native package that adds LLVM 23.1.3, SolRT, AOT linking, and process-isolated
ORC for a restricted verified subset. Neither package implements the complete language specification.

| Capability | Sol 0.1.0 state |
|---|---|
| UTF-8 source, byte spans, scalar/UTF-16 positions, lexer and bounded parser | Implemented; grammar remains a subset and unsupported productions are diagnosed. |
| PSI and structural AST | Implemented and included in the compiler component; not a stable NuGet SDK. |
| Module/declaration and body analysis | Implemented in partial source-analysis slices; unsupported facts remain deferred or diagnostic. |
| Verified FIR | Implemented for a closed bootstrap subset with an independent verifier and CFG assignment checks. |
| LLVM AOT and artifact output | Implemented in the native component for admitted FIR: bin, cdylib, staticlib, and solib have separate limits. |
| ORC | Implemented for bin execution in a separate native guest process. |
| Yellow Tea and SolRT | Experimental precise concurrent mark/sweep with span allocation, generated roots and barriers; no measured latency guarantee. |
| Standard library | Restricted std.core, std.stdio, and integral std.math bootstrap APIs; most specified modules remain unimplemented. |
| Package runtime | Each component carries its private .NET 10 runtime; neither NativeAOT nor SDK self-contained publishing is used. |

The original Sol 0.0.1 release remains an immutable CST-only frontend package. This status page
describes 0.1.0 and the current implementation; it does not retroactively change that release's
capabilities.

### Implementation at 0.1.0

The CST parser now includes braced `if/else`, the four `for` forms, `switch` with flat payload/type
patterns, `try/catch/finally` in statement/value contexts, `defer`, and `unsafe`. Function expressions
add typed/ref/out lambda headers, local return labels, anonymous functions, trailing lambdas, and
structured named/ref/out/spread call arguments. Casts and type tests are parsed with their precedence.
These additions extend beyond the initial 0.0.1 parser; the 0.1.0 native backend admits only a closed verified subset. Parsing them alone does not establish
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
overloads. Its shared generic solver now collects input/exact/expected bounds, invariant nested
evidence, and constructed nominal base projections. Unsupported candidate paths remain explicitly
deferred.
The [argument contract](arguments.md) now covers ordered mixed calls, middle varargs, array-valued
named varargs, default origins/inheritance, and outside-lambda destinations. The commented
[source specification](source-specification.md) also explains planned language/runtime behavior;
its syntax/AST gate does not certify complete semantic implementation or native execution.

The optional [body analysis](body-analysis.md) adds receiver/member lookup, contextual expression and
lambda checking, constructor/override checks, structured definite assignment and smart casts, checked
effects, and conservative storage/capture checks. Receiver lookup substitutes constructed bases,
merges inherited callable groups, removes same-signature base recovery candidates after override,
and supports constrained type-parameter lookup. Generic member calls use candidate-local argument,
ref/out, expected-result, and base-projection evidence before checking owner/method constraints.
Unverified source locations remain queryable. The current development body-analysis slice records
exceptional snapshots for explicit throws and supported resolved calls, accessors, operators,
delegation protocols, and constructor field initialization. Catch branches join only snapshots that
can overlap their caught type; finally is checked on reachable normal, return, and escaping-exception
paths. Function-value effects and ref/out normal-return assignment are included in this slice. It is
still conservative for built-in potentially throwing operations. The lexical cleanup slice now
tracks reached `defer` registrations and applies bounded LIFO cleanup on normal and abrupt source
edges; an overflowing path set stays Deferred. Implicit, primary, and secondary constructor source
proofs resolve `this`/base chains and carry initialization, helper, callback, and delegation effects
with constructed storage identities. Missing bodies, uncertain receiver aliases, and exhausted
proof budgets remain Deferred. Native construction and lazy object/module initialization are
separate boundaries. Suppressed failure data is
analysis metadata, not a runtime Throwable object or delivered Windows exception. Complete CFG
correlations, full protected receiver checks, complete construction proofs, contextual lambda/array
evidence, and borrow-origin proof remain incomplete. These are source-analysis facts only; this API
does not produce verified FIR or native output. See the [cleanup/construction guide](cleanup-construction.md)
for the exact supported boundary.

Interface method/property/indexer default selection and `by` forwarding now have source operation
records, declaration-time conflict/missing-implementation diagnostics, compatible independent
getter/setter merging, and direct `super<I>` binding. Native interface dispatch and forwarding
lowering remain outside the supported FIR boundary; see [member resolution](member-resolution.md).

The [Yellow Tea implementation inventory](yellow-tea/current-implementation.md) records the experimental
concurrent mark/sweep collector, generated roots/barriers, handles, and span allocator. Broader allocator
concurrency/workload qualification and pacing work remain open. No measured latency or production-readiness
guarantee is available. SolRT defaults to dynamic; static linkage is explicit.

The [verified FIR/native path](fir-native.md) admits a closed scalar/stateless subset, independently
checks FIR types/calls/CFG assignment, and lowers through a qualified C++20/CLI LLVM bridge to AMD64
COFF and a Windows executable. Default dynamic and explicit static SolRT modes are execution-tested.
Collectible Strings/scalar arrays and selected String/integral math APIs are included in the 0.1.0 native component.
[Library artifacts](library-artifacts.md) now include scalar C DLL/staticlib output and closed-subset
Sol containers; external Sol module consumption remains pending. ORC runs in a separate native guest
process. Closed module-level generic functions have a verified FIR/native slice for admitted
primitive, String, and one-level array ABI types, with per-instance body rebinding and monomorphic
generic function references. Generic owners/members, broad generic ABI, source catches/defer/EH, the
complete allocator, and full borrowing remain gated. The [standard library inventory](standard-library.md)
lists implemented and pending modules.

### Unreleased source-analysis work

Development source analysis automatically requests closed body evidence for selected generic owner
members, including uniquely projected inherited members. It keeps owner and method arguments
separate, rebinds the original AST, and checks a bounded dependency graph across members, generic free
functions, stateless object methods, and direct local functions. Local bodies retain enclosing generic
arguments and call-site local type/assignment facts; inferred local results are recomputed before
enclosing overload selection. Invalid/deferred dependencies propagate into caller facts. See
[automatic member body analysis](generic-fir-specialization/AutomaticMemberBodyAnalysis.md).
This work is unreleased, is not a public specialization SDK, and does not admit generic owners or
local generic closures to FIR. `SOL2400` remains the native boundary until the independent verifier,
owner layout, receiver/closure ABI, GC metadata and runtime gates are implemented and qualified.

### Remaining stages

1. Continue grammar edge coverage and source tooling against the shared CST/PSI surface.
2. Extend source analysis beyond the bounded top-level owner and lexical body slices; address nested
   owners, remaining receiver visibility/default conflict rules, generated members, and complete
   overload/expression typing on top of the constructed-member resolver.
3. Expand the verified FIR subset with full nullability, checked exception/cleanup CFG, borrowing,
   closures, broader generic specialization, and general object layouts.
4. Qualify complete Windows/ORC exception handling, library consumption and extended native ABI.
5. Broaden allocator/concurrency workload qualification, validate Yellow Tea invariants, and implement `std.core`;
   expand the standard library with module-level API contracts and tests.

Testing, malformed-input exploration, security analysis, and documentation evolve with each stage.
A consumer should not claim a compiler capability is available before that capability is implemented
and verified. No delivery dates or production-readiness guarantees are assigned by this roadmap.
