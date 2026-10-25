<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Sol source specification

The `.sol` files in this directory explain the intended language through declarations, examples,
and rule comments. They include semantics that the compiler has not implemented yet. A source
example is a language contract, not a claim that the current compiler can build or execute it.

`examples/` contains larger usage examples grouped by subject. Every file declares its logical
module explicitly; directory and module names do not have to match. Compile source sets explicitly.
The normal project convention is lowercase directories and PascalCase `.sol` filenames. Follow the
declared module segments below the source root: `module myproject.example` conventionally lives in
`src/myproject/example/Greeter.sol`. The compiler accepts other explicit paths; layout is a naming
convention, not a module-resolution requirement. This specification corpus groups subjects for
reading, rather than placing every contract into a conventional application source tree.
Do not link this entire corpus as one application: several examples intentionally have independent
`Main` objects, and the module/partial example has a documented two-file source set.

| Contract | Topics |
|---|---|
| [Lexical.sol](Lexical.sol) | UTF-8, comments, separators, identifiers, literals |
| [Types.sol](Types.sol) | Primitive types, arrays, tuples, bottom and nullable bottom |
| [Bindings.sol](Bindings.sol) | val/var/constants, assignment, initialization |
| [Functions.sol](Functions.sol) | Grouped parameters, named/default/vararg, evaluation order |
| [Lambdas.sol](Lambdas.sol) | Named/positional/headerless lambdas, trailing syntax, ref/out |
| [NullSafety.sol](NullSafety.sol) | Nullable elements versus nullable arrays, casts and safe access |
| [Objects.sol](Objects.sol) | Classes, objects, companions, constructors, accessors, data/sealed |
| [Structs.sol](Structs.sol) | Value copying, defaultability, ref struct, ref/out locations |
| [Generics.sol](Generics.sol) | Invariance, bounds, anti-constraints, specialization limits |
| [GenericInference.sol](GenericInference.sol) | Input, exact and expected-result evidence, invariant matching and projections |
| [GenericInference.Bounds.sol](GenericInference.Bounds.sol) | Lower/upper aggregation, constructed joins and conflict cases |
| [GenericInference.CommonTypes.sol](GenericInference.CommonTypes.sol) | Unique common candidate, invariant nesting, nullability, literals and ref-like limits |
| [GenericInference.Context.sol](GenericInference.Context.sol) | Named/default/vararg, lambda, array and effect evidence |
| [GenericInference.Cycles.sol](GenericInference.Cycles.sol) | Recursive equations, F-bounds, cycle identities, finite substitution and cancellation |
| [GenericInference.Flow.sol](GenericInference.Flow.sol) | Smart-cast evidence, mutation invalidation and flow snapshots per overload |
| [GenericInference.Literals.sol](GenericInference.Literals.sol) | Exact literal data, array/fixed-array and contextual lambda evidence |
| [GenericInference.Lambdas.sol](GenericInference.Lambdas.sol) | Candidate-isolated lambda context, modes, results and effects |
| [GenericInference.Effects.sol](GenericInference.Effects.sol) | Unordered checked-effect substitution and pending set inference |
| [GenericInference.Overloads.sol](GenericInference.Overloads.sol) | Candidate-local evidence, applicability, ranking and deferral |
| [GenericInference.Validation.sol](GenericInference.Validation.sol) | Constraints, ownership, budgets, diagnostics and FIR boundary |
| [GenericInference.LambdaCandidates.sol](GenericInference.LambdaCandidates.sol) | Candidate-local lambda parameter, body-result and checked-effect evidence |
| [GenericInference.ArrayCandidates.sol](GenericInference.ArrayCandidates.sol) | Array literal evidence, common element type, fixed length and nested shape |
| [GenericInference.EffectCandidates.sol](GenericInference.EffectCandidates.sol) | Unordered effect matching and the single-open-pattern limit |
| [GenericInference.CandidateTransactions.sol](GenericInference.CandidateTransactions.sol) | Speculative state ownership, rollback and selected-candidate publication |
| [GenericInference.LambdaResultBounds.sol](GenericInference.LambdaResultBounds.sol) | Lambda result joins, expected bounds and completion paths |
| [GenericInference.LambdaModesCapture.sol](GenericInference.LambdaModesCapture.sol) | Lambda parameter modes, capture and ref-like escape |
| [GenericInference.ArrayCommonType.sol](GenericInference.ArrayCommonType.sol) | Unique common element type search and ambiguity handling |
| [GenericInference.ArrayVarargs.sol](GenericInference.ArrayVarargs.sol) | Named/spread varargs, empty literals and invariant array identity |
| [GenericInference.EvidenceProvenance.sol](GenericInference.EvidenceProvenance.sol) | Origin tracking, deterministic conflict explanations and inspection boundary |
| [GenericInference.ErrorRecovery.sol](GenericInference.ErrorRecovery.sol) | Rejected, deferred, ambiguous and resource-limited candidate outcomes |
| [GenericInference.LambdaFixedPoint.sol](GenericInference.LambdaFixedPoint.sol) | Bounded cross-lambda fixed-point inference and selected replay |
| [GenericInference.ArrayContextualTyping.sol](GenericInference.ArrayContextualTyping.sol) | Recursive array shape, element context, numeric literal and empty-array rules |
| [GenericInference.ArrayVarargMapping.sol](GenericInference.ArrayVarargMapping.sol) | Immutable mapping of vararg elements, named arrays, spread, defaults and empty forms |
| [GenericInference.CandidateEvidenceLedger.sol](GenericInference.CandidateEvidenceLedger.sol) | Candidate-owned evidence records, relations, provenance and conflict outcomes |
| [MemberResolution.sol](MemberResolution.sol) | Receiver categories, constructed bases, overload groups and lookup limits |
| [MemberResolution.Overloads.sol](MemberResolution.Overloads.sol) | Inherited overload merge, override replacement and ambiguity |
| [MemberResolution.Visibility.sol](MemberResolution.Visibility.sol) | Accessor/receiver visibility and nullable member access |
| [MemberResolution.Paths.sol](MemberResolution.Paths.sol) | Constructed paths, substitutions, diamonds and deterministic traversal |
| [MemberResolution.CallableKinds.sol](MemberResolution.CallableKinds.sol) | Property, indexer, constructor, operator, delegation and safe call rules |
| [MemberResolution.Dispatch.sol](MemberResolution.Dispatch.sol) | Static contract, virtual/interface dispatch and lowering boundary |
| [MemberResolution.NullableFlow.sol](MemberResolution.NullableFlow.sol) | Stable narrowing, safe-call evaluation, result lifting and nullable storage |
| [MemberResolution.PartialIdentity.sol](MemberResolution.PartialIdentity.sol) | Cross-file type identity, member merge, initialization and deterministic diagnostics |
| [MemberResolution.Storage.sol](MemberResolution.Storage.sol) | Member locations, ref/out storage, ref-like escape and borrow facts |
| [MemberResolution.Diagnostics.sol](MemberResolution.Diagnostics.sol) | Missing/access/nullable/ambiguity/resource diagnostic ownership |
| [MemberResolution.PathOwnership.sol](MemberResolution.PathOwnership.sol) | Route identity, path preservation and bounded candidate ownership |
| [MemberResolution.PathSensitiveHiding.sol](MemberResolution.PathSensitiveHiding.sol) | Per-route member hiding and alternate-path preservation |
| [MemberResolution.ProtectedCalls.sol](MemberResolution.ProtectedCalls.sol) | Protected lexical owner and static receiver validation |
| [MemberResolution.AccessorCandidates.sol](MemberResolution.AccessorCandidates.sol) | Read/write/compound operation-specific accessor selection |
| [MemberResolution.AccessorEffects.sol](MemberResolution.AccessorEffects.sol) | Getter, setter, compound and delegated accessor effects |
| [MemberResolution.OverrideRecovery.sol](MemberResolution.OverrideRecovery.sol) | Override validation, inherited candidate recovery and suppression |
| [MemberResolution.ResourceLimits.sol](MemberResolution.ResourceLimits.sol) | Lookup depth/path/path-per-candidate limits and cancellation |
| [MemberResolution.PathwiseReduction.sol](MemberResolution.PathwiseReduction.sol) | Route-by-route hiding, override suppression and complete-set outcomes |
| [MemberResolution.ProtectedReceiverMatrix.sol](MemberResolution.ProtectedReceiverMatrix.sol) | Lexical derivation and receiver-type decision matrix |
| [MemberResolution.AccessorOperationEffects.sol](MemberResolution.AccessorOperationEffects.sol) | Read/write/compound sequencing, visibility and effect composition |
| [MemberResolution.CandidateVisibility.sol](MemberResolution.CandidateVisibility.sol) | Name ownership, access filtering, overload recovery and diagnostics |
| [Modules.sol](Modules.sol) | Logical identities, aliases, selective/public imports, partials |
| [Exceptions.sol](Exceptions.sol) | Checked/unchecked hierarchy, throws, catch, cleanup |
| [ControlFlow.sol](ControlFlow.sol) | Four loop forms, switch patterns, try results, tail results |
| [Delegation.sol](Delegation.sol) | Property/interface delegation and descriptor identity |
| [Operators.sol](Operators.sol) | Companion operators, pairing and location semantics |
| [Annotations.sol](Annotations.sol) | Annotation schemas, constant arguments and builtin names |
| [NativeInterop.sol](NativeInterop.sol) | Windows C boundary, unsafe, explicit native representations |
| [Naming.sol](Naming.sol) | Public names, constants, private backing names and intrinsic exceptions |
| [Entry.sol](Entry.sol) | Main object, exact argument/result contract and process boundary |
| [Memory.sol](Memory.sol) | Reachability, roots/barriers, closures, concurrency and scoped storage |
| [Reflection.sol](Reflection.sol) | Native metadata, SolProperty access and descriptor ownership |
| [StandardLibrary.sol](StandardLibrary.sol) | Intended modules and collection types |

## Implementation boundaries

- The development frontend parses the illustrated syntax into lossless CST, PSI and structural AST.
- Declaration, written-type and lexical analyses cover explicit source sets, imports, partials and
  portions of generic/type/default metadata. Direct module-function calls and a bounded body-analysis
  subset bind arguments, receiver members, inherited overload groups, generic input/exact/expected
  bounds, constructed base projections, and nominal constraints.
- Complete cross-distribution access validation, interface-default conflict resolution,
  candidate-local inference for every lambda/array form, multi-open generic effect inference,
  monomorphized body checking, checked effect coverage, smart casts, definite assignment, ref escape,
  constant evaluation, and generated data/delegation members remain incomplete.
- Default-expression origins and evaluation order are represented by the analysis API. Actual
  evaluation, virtual calls, native exceptions, standard library behavior and garbage collection
  require later verified FIR, LLVM and runtime implementation. No example currently produces a native
  executable. The released Sol 0.0.1 component contains only the initial CST frontend.

The current body-analysis delta adds bounded candidate-local lambda replay, generic array-literal
evidence, one-open-effect matching, per-path hiding/override recovery, protected method receiver
checks, and accessor visibility/effect checks. These are source-analysis facts only; the contracts
above intentionally include broader requirements that remain pending.

The corpus is syntax-checked separately from semantic tests. Syntax validation does not supply
`std.core`, resolve library APIs, prove the rule comments, or execute the examples. Planned rules
remain documented even when their semantic tests or implementation do not exist.

See [argument contracts](../docs/arguments.md), [generic call analysis](../docs/generic-calls.md),
[generic inference](../docs/generic-inference.md), [member resolution](../docs/member-resolution.md),
and the [implementation status](../docs/status.md) for the supported API boundaries.
