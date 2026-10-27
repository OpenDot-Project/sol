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
| [GenericInference.Effects.sol](GenericInference.Effects.sol) | Unordered checked-effect substitution, bounded unique top-level matching, and remaining deferred cases |
| [GenericInference.Overloads.sol](GenericInference.Overloads.sol) | Candidate-local evidence, applicability, ranking and deferral |
| [GenericInference.Validation.sol](GenericInference.Validation.sol) | Constraints, ownership, budgets, diagnostics and FIR boundary |
| [GenericInference.LambdaCandidates.sol](GenericInference.LambdaCandidates.sol) | Candidate-local lambda parameter, body-result and checked-effect evidence |
| [GenericInference.ArrayCandidates.sol](GenericInference.ArrayCandidates.sol) | Array literal evidence, common element type, fixed length and nested shape |
| [GenericInference.EffectCandidates.sol](GenericInference.EffectCandidates.sol) | Unordered effect matching, unique multi-open mapping, ambiguity and resource limits |
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
| [ArrayIteration.sol](ArrayIteration.sol) | Dynamic-array snapshots, item copies, loop edges, cursor contract, roots, and native qualification |
| [CleanupConstruction.sol](CleanupConstruction.sol) | Lexical defer activation/unwind and construction-time effects |
| [Delegation.sol](Delegation.sol) | Property/interface delegation and descriptor identity |
| [Operators.sol](Operators.sol) | Companion operators, pairing and location semantics |
| [Annotations.sol](Annotations.sol) | Annotation schemas, constant arguments and builtin names |
| [NativeInterop.sol](NativeInterop.sol) | Windows C boundary, unsafe, explicit native representations |
| [Naming.sol](Naming.sol) | Public names, constants, private backing names and intrinsic exceptions |
| [Entry.sol](Entry.sol) | Main object, exact argument/result contract and process boundary |
| [Memory.sol](Memory.sol) | Reachability, roots/barriers, closures, concurrency and scoped storage |
| [Reflection.sol](Reflection.sol) | Native metadata, SolProperty access and descriptor ownership |
| [StandardLibrary.sol](StandardLibrary.sol) | Intended modules and collection types |

## Generic FIR specialization

The following contracts define the source-selected generic instance graph, current FIR boundary,
verifier requirements and future completion criteria. The examples are specification fixtures; they
do not imply that each row is already parsed, semantically checked, or executed by native code.

| Contract | Topics |
|---|---|
| [GenericFirSpecialization.sol](GenericFirSpecialization.sol) | Source selection, closed instance identity, body substitution, verifier and lowering contract |
| [GenericFirSpecializationBoundaries.sol](GenericFirSpecializationBoundaries.sol) | Unsupported surface, conformance requirements and completion gates |
| [GenericFirSpecializationLimits.sol](GenericFirSpecializationLimits.sol) | Function, body-node, type-depth budgets, cancellation and deterministic failure |
| [GenericFirInstantiationGraph.sol](GenericFirInstantiationGraph.sol) | Exact-key recursion, expanding-key cycles, worklist reachability and cache boundary |
| [GenericFirCacheAndDeterminism.sol](GenericFirCacheAndDeterminism.sol) | Compilation-local identity, deterministic ordering and future cache invalidation |
| [GenericFirSpecializationCases.sol](GenericFirSpecializationCases.sol) | End-to-end generic source selection and native admission matrix |
| [GenericFirJsonContract.sol](GenericFirJsonContract.sol) | FIR inspection schema 5, workspace-local type IDs, source origins and diagnostics |
| [GenericFirArtifacts.sol](GenericFirArtifacts.sol) | AOT/ORC and bin/cdylib/staticlib/solib generic instance boundaries |
| [GenericFirFunctionValues.sol](GenericFirFunctionValues.sol) | Generic declarations, contextual lambdas, function values, captures and indirect calls |
| [GenericFirOwnersAndMembers.sol](GenericFirOwnersAndMembers.sol) | Partial generic owners, constructors, methods, accessors, operators and dispatch |
| [GenericFirStorageAndRoots.sol](GenericFirStorageAndRoots.sol) | Concrete storage, collection and span layouts, ref-like lifetime and Yellow Tea roots |
| [GenericFirConstraints.sol](examples/semantics/GenericFirConstraints.sol) | Nominal constraints, bounds, anti-constraints, inheritance and direct cases |
| [GenericFirConstraintsAdvanced.sol](examples/semantics/GenericFirConstraintsAdvanced.sol) | Overloads, nested forwarding, verifier agreement and deferred constraint outcomes |
| [GenericFirNullability.sol](examples/semantics/GenericFirNullability.sol) | Nullable type identity, nullable array element/container syntax and safe access |
| [GenericFirNullabilityFlow.sol](examples/semantics/GenericFirNullabilityFlow.sol) | Nested substitution, flow, GC representation and native diagnostic boundaries |
| [GenericFirPrimitiveAbi.sol](examples/semantics/GenericFirPrimitiveAbi.sol) | Primitive, String and array ABI admission boundaries |
| [GenericFirEffects.sol](examples/semantics/GenericFirEffects.sol) | Generic checked effects, exception substitution and call budgets |
| [GenericFirNestedSubstitution.sol](examples/semantics/GenericFirNestedSubstitution.sol) | Nested type constructors, owner identity, simultaneous substitution and limits |
| [GenericFirArgumentMapping.sol](examples/semantics/GenericFirArgumentMapping.sol) | Named/default/vararg/ref/out mapping before generic inference |
| [GenericFirOverloadSelection.sol](examples/semantics/GenericFirOverloadSelection.sol) | Conversion ranking, generic tie rules, result evidence and exact ref/out |
| [GenericFirOverloadSelectionBoundaries.sol](examples/semantics/GenericFirOverloadSelectionBoundaries.sol) | Candidate rollback, native rejection, verifier identity and phase diagnostics |
| [GenericFirFlow.sol](examples/semantics/GenericFirFlow.sol) | Flow-sensitive values and limits of reusing open-body facts |
| [GenericFirRecursion.sol](examples/semantics/GenericFirRecursion.sol) | Recursive calls, distinct type keys, cancellation and budget outcomes |
| [GenericFirJsonContract.sol](examples/semantics/GenericFirJsonContract.sol) | Schema field and source binding scenarios for generic FIR calls |
| [GenericFirArtifacts.sol](examples/semantics/GenericFirArtifacts.sol) | Artifact/backend combinations and private closed helper scenarios |
| [GenericFirFunctionValues.sol](examples/semantics/GenericFirFunctionValues.sol) | Direct call versus function value, lambda, capture and GC scenarios |
| [GenericFirOwnersAndMembers.sol](examples/semantics/GenericFirOwnersAndMembers.sol) | Generic class/member identity and native admission cases |
| [GenericFirStorageAndRoots.sol](examples/semantics/GenericFirStorageAndRoots.sol) | Nullable array elements, spans, descriptors, roots and process-isolation cases |
| [GenericFirCacheAndDeterminism.sol](examples/semantics/GenericFirCacheAndDeterminism.sol) | Per-build keys, FIFO reuse, invalidation and deterministic-output cases |
| [GenericFirFunctionValues source contract](GenericFirFunctionValues.sol) | Generic declarations versus function values, lambdas, captures and indirect-call requirements |
| [GenericFirFunctionValues semantic examples](examples/semantics/GenericFirFunctionValues.sol) | Direct calls, expected lambdas, effects, ref/out, captures, GC and artifact boundaries |

All example pairs that share a declared module form one logical module contract when read together.
Compile only the individual fixture groups a scenario requires; unrelated contracts deliberately
use independent example modules.

## Flow-transfer and checked-effect fixtures

These independent files make path-sensitive transfer and effect contracts concrete. They are
syntax examples and normative expectations, not claims that every rule is implemented by the
current analyzer. Each file is an independent source module.

| Fixture | Contract focus |
|---|---|
| [FlowTransfer.BranchState.sol](examples/semantics/FlowTransfer.BranchState.sol) | Branch joins, reachability, and assignment facts |
| [FlowTransfer.CallMutationBoundary.sol](examples/semantics/FlowTransfer.CallMutationBoundary.sol) | Call invalidation and stable local refinements |
| [FlowTransfer.CallSummaries.sol](examples/semantics/FlowTransfer.CallSummaries.sol) | Normal and exceptional call summaries |
| [FlowTransfer.CapturedAliases.sol](examples/semantics/FlowTransfer.CapturedAliases.sol) | Captured mutable aliases and invalidated facts |
| [FlowTransfer.CorrelatedBranches.sol](examples/semantics/FlowTransfer.CorrelatedBranches.sol) | Correlated predicates and path joins |
| [FlowTransfer.DeferRegistration.sol](examples/semantics/FlowTransfer.DeferRegistration.sol) | Lexical defer registration and exit order |
| [FlowTransfer.EnumeratorEdges.sol](examples/semantics/FlowTransfer.EnumeratorEdges.sol) | Iterable acquisition, iteration, and disposal edges |
| [FlowTransfer.ExceptionalSnapshots.sol](examples/semantics/FlowTransfer.ExceptionalSnapshots.sol) | Per-operation exceptional definite-assignment snapshots |
| [FlowTransfer.FinallyExceptionalWrites.sol](examples/semantics/FlowTransfer.FinallyExceptionalWrites.sol) | Finally writes on thrown and escaping paths |
| [FlowTransfer.FinallyWrites.sol](examples/semantics/FlowTransfer.FinallyWrites.sol) | Finally writes on normal and return exits |
| [FlowTransfer.FunctionValues.sol](examples/semantics/FlowTransfer.FunctionValues.sol) | Function-value calls, effects, and out assignment |
| [FlowTransfer.LoopBackedgeWrites.sol](examples/semantics/FlowTransfer.LoopBackedgeWrites.sol) | Loop backedge assignments and killed refinements |
| [FlowTransfer.LoopEdges.sol](examples/semantics/FlowTransfer.LoopEdges.sol) | Zero-iteration, continue, break, and return edges |
| [FlowTransfer.LoopFixedPoint.sol](examples/semantics/FlowTransfer.LoopFixedPoint.sol) | Fixed-point invariants and bounded convergence |
| [FlowTransfer.MixedTransfers.sol](examples/semantics/FlowTransfer.MixedTransfers.sol) | Combined normal, return, break, and throw transfers |
| [FlowTransfer.NullableFacts.sol](examples/semantics/FlowTransfer.NullableFacts.sol) | Null and type-test facts across branch forms |
| [FlowTransfer.PatternScopes.sol](examples/semantics/FlowTransfer.PatternScopes.sol) | Case-local bindings and scope isolation |
| [FlowTransfer.RefOutEdges.sol](examples/semantics/FlowTransfer.RefOutEdges.sol) | Ref/out mutation and normal-return assignment |
| [FlowTransfer.RequiredFields.sol](examples/semantics/FlowTransfer.RequiredFields.sol) | Constructor field assignment and publication |
| [FlowTransfer.ShortCircuitFacts.sol](examples/semantics/FlowTransfer.ShortCircuitFacts.sol) | Short-circuit edge facts and single evaluation |
| [FlowTransfer.SwitchAlternativeStates.sol](examples/semantics/FlowTransfer.SwitchAlternativeStates.sol) | Alternative switch states and exhaustive joins |
| [FlowTransfer.SwitchCoverage.sol](examples/semantics/FlowTransfer.SwitchCoverage.sol) | Closed/open-domain coverage and unmatched edges |
| [FlowTransfer.TryCleanup.sol](examples/semantics/FlowTransfer.TryCleanup.sol) | Catch, cleanup, and escaping-exception transfer |
| [FlowTransfer.TryValueResults.sol](examples/semantics/FlowTransfer.TryValueResults.sol) | Try/catch value joins and bottom paths |
| [CheckedEffects.Accessors.sol](examples/semantics/CheckedEffects.Accessors.sol) | Getter/setter effect composition |
| [CheckedEffects.ArgumentEvaluationOrder.sol](examples/semantics/CheckedEffects.ArgumentEvaluationOrder.sol) | Source-order argument effects and failure points |
| [CheckedEffects.BudgetCompatibility.sol](examples/semantics/CheckedEffects.BudgetCompatibility.sol) | Checked subtype budgets and incompatibility |
| [CheckedEffects.Callbacks.sol](examples/semantics/CheckedEffects.Callbacks.sol) | Function-value effect contracts and callbacks |
| [CheckedEffects.CallEdges.sol](examples/semantics/CheckedEffects.CallEdges.sol) | Direct call edges, hierarchy coverage, and propagation |
| [CheckedEffects.CandidateEffects.sol](examples/semantics/CheckedEffects.CandidateEffects.sol) | Candidate-local effect inference and rollback |
| [CheckedEffects.CleanupComposition.sol](examples/semantics/CheckedEffects.CleanupComposition.sol) | Cleanup effects across nested exits |
| [CheckedEffects.CleanupOrdering.sol](examples/semantics/CheckedEffects.CleanupOrdering.sol) | Cleanup execution order and effect union |
| [CheckedEffects.ConstructorDefaults.sol](examples/semantics/CheckedEffects.ConstructorDefaults.sol) | Constructor defaults and initialization effects |
| [CheckedEffects.Constructors.sol](examples/semantics/CheckedEffects.Constructors.sol) | Constructor call and initializer effects |
| [CheckedEffects.DefaultOriginOverrides.sol](examples/semantics/CheckedEffects.DefaultOriginOverrides.sol) | Inherited defaults and source-origin effects |
| [CheckedEffects.DefaultsVarargs.sol](examples/semantics/CheckedEffects.DefaultsVarargs.sol) | Defaulted and variadic call effect sets |
| [CheckedEffects.DeferredCandidates.sol](examples/semantics/CheckedEffects.DeferredCandidates.sol) | Deferred candidate proof and non-publication |
| [CheckedEffects.DispatchContract.sol](examples/semantics/CheckedEffects.DispatchContract.sol) | Virtual dispatch and declared effect contracts |
| [CheckedEffects.EffectProvenance.sol](examples/semantics/CheckedEffects.EffectProvenance.sol) | Effect identity, origin, and deterministic reporting |
| [CheckedEffects.ExceptionalRefOut.sol](examples/semantics/CheckedEffects.ExceptionalRefOut.sol) | Ref/out facts at exceptional call edges |
| [CheckedEffects.ForeignBoundary.sol](examples/semantics/CheckedEffects.ForeignBoundary.sol) | Native-boundary effect obligations |
| [CheckedEffects.FunctionValueReturns.sol](examples/semantics/CheckedEffects.FunctionValueReturns.sol) | Returned callable effect signatures |
| [CheckedEffects.GenericPatterns.sol](examples/semantics/CheckedEffects.GenericPatterns.sol) | Generic effect patterns and substitution |
| [CheckedEffects.HandlerEffectSets.sol](examples/semantics/CheckedEffects.HandlerEffectSets.sol) | Handler-local effect sets and propagation |
| [CheckedEffects.HandlerPathSubtraction.sol](examples/semantics/CheckedEffects.HandlerPathSubtraction.sol) | Catch subtraction by hierarchy and path |
| [CheckedEffects.HierarchyCatch.sol](examples/semantics/CheckedEffects.HierarchyCatch.sol) | Catch subtype coverage and broad static types |
| [CheckedEffects.Indexers.sol](examples/semantics/CheckedEffects.Indexers.sol) | Indexer getter/setter effect order |
| [CheckedEffects.InterfaceDiamonds.sol](examples/semantics/CheckedEffects.InterfaceDiamonds.sol) | Interface inheritance and effect contracts |
| [CheckedEffects.InvocationBranches.sol](examples/semantics/CheckedEffects.InvocationBranches.sol) | Branch-sensitive callable invocation effects |
| [CheckedEffects.NestedFunctionTypes.sol](examples/semantics/CheckedEffects.NestedFunctionTypes.sol) | Nested function-type effect validity |
| [CheckedEffects.OperatorCheckedVariant.sol](examples/semantics/CheckedEffects.OperatorCheckedVariant.sol) | Checked effects from operator selection |
| [CheckedEffects.OperatorDelegation.sol](examples/semantics/CheckedEffects.OperatorDelegation.sol) | Operator and delegation protocol effects |
| [CheckedEffects.OperatorSelection.sol](examples/semantics/CheckedEffects.OperatorSelection.sol) | Selected operator effects and conversion edges |
| [CheckedEffects.Overrides.sol](examples/semantics/CheckedEffects.Overrides.sol) | Override effect narrowing and generic substitution |
| [CheckedEffects.SelectedDefaultsAndVarargs.sol](examples/semantics/CheckedEffects.SelectedDefaultsAndVarargs.sol) | Selected overload mapping with defaults/varargs |
| [CheckedEffects.StandardIoChain.sol](examples/semantics/CheckedEffects.StandardIoChain.sol) | Chained standard I/O checked effects |
| [CheckedEffects.UnhandledBoundary.sol](examples/semantics/CheckedEffects.UnhandledBoundary.sol) | Unhandled effects at the process boundary |

## Cleanup-transfer fixtures

These files isolate registration and cleanup transfer contracts from the broader flow suite. They
cover activation after the declaration is reached, branch-correlated registrations, scope nesting,
loop exits, return/throw composition, checked effects, native boundaries, and exceptional state
snapshots. A comment that says `Deferred` describes an intentionally unsupported proof case; it is
not a runtime result.

| Fixture | Contract focus |
|---|---|
| [CleanupTransfer.CheckedEffects.sol](examples/semantics/CleanupTransfer.CheckedEffects.sol) | Cleanup effects on normal and exceptional exits |
| [CleanupTransfer.ExitForms.sol](examples/semantics/CleanupTransfer.ExitForms.sol) | Fallthrough, return, throw, break, continue, and bottom |
| [CleanupTransfer.Failures.sol](examples/semantics/CleanupTransfer.Failures.sol) | Primary and suppressed cleanup failures |
| [CleanupTransfer.Loops.sol](examples/semantics/CleanupTransfer.Loops.sol) | Per-iteration scopes and loop transfer ownership |
| [CleanupTransfer.NativeBoundary.sol](examples/semantics/CleanupTransfer.NativeBoundary.sol) | Process/runtime boundaries and unsupported native lowering |
| [CleanupTransfer.Registration.sol](examples/semantics/CleanupTransfer.Registration.sol) | Reachability-sensitive registration timing |
| [CleanupTransfer.ScopeNesting.sol](examples/semantics/CleanupTransfer.ScopeNesting.sol) | Nested scope ownership and LIFO order |
| [CleanupTransfer.StateEdges.sol](examples/semantics/CleanupTransfer.StateEdges.sol) | Definite assignment, refinements, and exceptional snapshots |

## Construction-effect fixtures

Construction fixtures separate successful publication, field coverage, source-order effects,
delegated-property protocols, generic effect substitution, partial initialization order, and
unsupported escape/callback paths. They specify which behavior a complete implementation must
support while distinguishing the current body's bounded subset.

| Fixture | Contract focus |
|---|---|
| [ConstructionEffects.Accessors.sol](examples/semantics/ConstructionEffects.Accessors.sol) | Accessor effects and construction call sites |
| [ConstructionEffects.ApiCompatibility.sol](examples/semantics/ConstructionEffects.ApiCompatibility.sol) | Constructor API compatibility and checked budgets |
| [ConstructionEffects.Budgets.sol](examples/semantics/ConstructionEffects.Budgets.sol) | Subtype effect budgets and unhandled initializer effects |
| [ConstructionEffects.Chaining.sol](examples/semantics/ConstructionEffects.Chaining.sol) | Explicit delegation and constructor-chain boundary |
| [ConstructionEffects.Delegates.sol](examples/semantics/ConstructionEffects.Delegates.sol) | provideDelegate/getValue/setValue effect ownership |
| [ConstructionEffects.ExceptionalEdges.sol](examples/semantics/ConstructionEffects.ExceptionalEdges.sol) | Allocation and initializer failure snapshots |
| [ConstructionEffects.Fields.sol](examples/semantics/ConstructionEffects.Fields.sol) | Required stored-field initialization and publication |
| [ConstructionEffects.GenericEffects.sol](examples/semantics/ConstructionEffects.GenericEffects.sol) | Generic checked-effect substitution |
| [ConstructionEffects.InitializationState.sol](examples/semantics/ConstructionEffects.InitializationState.sol) | Constructor states and normal versus bottom completion |
| [ConstructionEffects.NoEscape.sol](examples/semantics/ConstructionEffects.NoEscape.sol) | Callback and constructing-instance escape deferral |
| [ConstructionEffects.Order.sol](examples/semantics/ConstructionEffects.Order.sol) | Declaration-order initializer evaluation |
| [ConstructionEffects.Partial.sol](examples/semantics/ConstructionEffects.Partial.sol) | Single initialization part and unsupported multi-part effects |
| [ConstructionEffects.ValueTypes.sol](examples/semantics/ConstructionEffects.ValueTypes.sol) | Class allocation edges versus struct/ref-struct construction |

## Array-iteration contract and fixtures

[ArrayIteration.sol](ArrayIteration.sol) is the normative source contract. The [reader guide](../docs/array-iteration.md)
explains the same rules in execution order and marks the current verified-FIR/native boundary. The
45 independent fixtures below separate source semantics, future cursor behavior, GC obligations,
and native qualification. They are not one buildable application, and a normative fixture may use
syntax whose semantic or backend support is still pending.

| Fixture | Contract focus |
|---|---|
| [ArrayIteration.AccessorEffects.sol](examples/semantics/ArrayIteration.AccessorEffects.sol) | Getter and setter effects around source and indexed access |
| [ArrayIteration.AssignmentJoins.sol](examples/semantics/ArrayIteration.AssignmentJoins.sol) | Zero-trip and break-state definite-assignment joins |
| [ArrayIteration.BindingsAndCopies.sol](examples/semantics/ArrayIteration.BindingsAndCopies.sol) | Item mutability, copy semantics, and reference values |
| [ArrayIteration.BorrowedViews.sol](examples/semantics/ArrayIteration.BorrowedViews.sol) | Borrowed span lifetime and escape restrictions |
| [ArrayIteration.BoundsAndOrder.sol](examples/semantics/ArrayIteration.BoundsAndOrder.sol) | Saved length, ascending indexes, and observable order |
| [ArrayIteration.CaptureAndRefinement.sol](examples/semantics/ArrayIteration.CaptureAndRefinement.sol) | Captures, alias invalidation, and nullable facts |
| [ArrayIteration.CheckedEffects.sol](examples/semantics/ArrayIteration.CheckedEffects.sol) | Source and body checked-effect propagation |
| [ArrayIteration.CollectionMutation.sol](examples/semantics/ArrayIteration.CollectionMutation.sol) | Element mutation versus structural collection mutation |
| [ArrayIteration.CollectionOrder.sol](examples/semantics/ArrayIteration.CollectionOrder.sol) | Stable array order versus collection-defined order |
| [ArrayIteration.CursorCleanup.sol](examples/semantics/ArrayIteration.CursorCleanup.sol) | Close obligations on exhaustion and loop transfers |
| [ArrayIteration.CursorEffects.sol](examples/semantics/ArrayIteration.CursorEffects.sol) | Independent acquisition, advance, current, and close effects |
| [ArrayIteration.CursorMutation.sol](examples/semantics/ArrayIteration.CursorMutation.sol) | Cursor state mutation across repeated advance calls |
| [ArrayIteration.CursorProtocol.sol](examples/semantics/ArrayIteration.CursorProtocol.sol) | Future iterator and cursor member contract |
| [ArrayIteration.CursorState.sol](examples/semantics/ArrayIteration.CursorState.sol) | Cursor state transitions and current-value availability |
| [ArrayIteration.CursorTransferState.sol](examples/semantics/ArrayIteration.CursorTransferState.sol) | Cursor state at continue, break, return, and throw edges |
| [ArrayIteration.CursorUnwind.sol](examples/semantics/ArrayIteration.CursorUnwind.sol) | Cleanup failures while another failure is unwinding |
| [ArrayIteration.DeferEdges.sol](examples/semantics/ArrayIteration.DeferEdges.sol) | Per-iteration defer activation and execution order |
| [ArrayIteration.DefiniteAssignment.sol](examples/semantics/ArrayIteration.DefiniteAssignment.sol) | Body-only assignment and reachable normal exits |
| [ArrayIteration.DescriptorLayout.sol](examples/semantics/ArrayIteration.DescriptorLayout.sol) | Array descriptor and element-storage assumptions |
| [ArrayIteration.Diagnostics.sol](examples/semantics/ArrayIteration.Diagnostics.sol) | Source binding and semantic error locations |
| [ArrayIteration.DiagnosticsAndRecovery.sol](examples/semantics/ArrayIteration.DiagnosticsAndRecovery.sol) | Error recovery without fabricating a successful loop |
| [ArrayIteration.EmptyAndInference.sol](examples/semantics/ArrayIteration.EmptyAndInference.sol) | Contextual element types and statically empty literals |
| [ArrayIteration.EvaluationOrder.sol](examples/semantics/ArrayIteration.EvaluationOrder.sol) | One-time source evaluation and side-effect order |
| [ArrayIteration.FactoryEvaluation.sol](examples/semantics/ArrayIteration.FactoryEvaluation.sol) | Factory calls, empty results, and source evaluation count |
| [ArrayIteration.FirJsonSchema.sol](examples/semantics/ArrayIteration.FirJsonSchema.sol) | Serialized FIR schema and array-node identity |
| [ArrayIteration.FirVerification.sol](examples/semantics/ArrayIteration.FirVerification.sol) | Verifier obligations for slots, source types, and loop body |
| [ArrayIteration.FixedArrays.sol](examples/semantics/ArrayIteration.FixedArrays.sol) | Normative fixed-array traversal outside current native support |
| [ArrayIteration.GcProgress.sol](examples/semantics/ArrayIteration.GcProgress.sol) | Bounded safepoints and progress through long traversal |
| [ArrayIteration.GcRoots.sol](examples/semantics/ArrayIteration.GcRoots.sol) | Array, current item, and reference-element roots |
| [ArrayIteration.IndexedComparison.sol](examples/semantics/ArrayIteration.IndexedComparison.sol) | Explicit indexed-loop equivalence and differences |
| [ArrayIteration.MethodOverloads.sol](examples/semantics/ArrayIteration.MethodOverloads.sol) | Overload resolution before iteration protocol lowering |
| [ArrayIteration.NativeNegative.sol](examples/semantics/ArrayIteration.NativeNegative.sol) | Unsupported element, shape, and cursor rejection |
| [ArrayIteration.NativePositive.sol](examples/semantics/ArrayIteration.NativePositive.sol) | Qualified dynamic-array bootstrap shapes |
| [ArrayIteration.NestedScopes.sol](examples/semantics/ArrayIteration.NestedScopes.sol) | Nested-loop targets, bindings, and cleanup scopes |
| [ArrayIteration.NullableFlow.sol](examples/semantics/ArrayIteration.NullableFlow.sol) | Nullable array references and nullable elements |
| [ArrayIteration.ProtocolResolution.sol](examples/semantics/ArrayIteration.ProtocolResolution.sol) | Exact protocol selection and ambiguity handling |
| [ArrayIteration.RefLikeLifetime.sol](examples/semantics/ArrayIteration.RefLikeLifetime.sol) | Scoped cursor and ref-like value lifetime constraints |
| [ArrayIteration.RuntimeValidation.sol](examples/semantics/ArrayIteration.RuntimeValidation.sol) | Runtime length, element access, and failure behavior |
| [ArrayIteration.Shapes.sol](examples/semantics/ArrayIteration.Shapes.sol) | Dynamic arrays, fixed arrays, and collection categories |
| [ArrayIteration.StdCollections.sol](examples/semantics/ArrayIteration.StdCollections.sol) | Standard collection iteration promises and boundaries |
| [ArrayIteration.StructuralPattern.sol](examples/semantics/ArrayIteration.StructuralPattern.sol) | Structural cursor protocol shape and member identity |
| [ArrayIteration.Transfers.sol](examples/semantics/ArrayIteration.Transfers.sol) | Break, continue, return, throw, and nesting transfers |
| [ArrayIteration.TryCatchEdges.sol](examples/semantics/ArrayIteration.TryCatchEdges.sol) | Cursor and body failures across catch/finally edges |
| [ArrayIteration.ValueViews.sol](examples/semantics/ArrayIteration.ValueViews.sol) | Value copy versus object identity through reference elements |
| [ArrayIteration.WritesAndIdentity.sol](examples/semantics/ArrayIteration.WritesAndIdentity.sol) | Rebinding, explicit indexed stores, and active array identity |

## Implementation boundaries

- The development frontend parses the illustrated syntax into lossless CST, PSI and structural AST.
- Declaration, written-type and lexical analyses cover explicit source sets, imports, partials and
  portions of generic/type/default metadata. Direct module-function calls and a bounded body-analysis
  subset bind arguments, receiver members, inherited overload groups, generic input/exact/expected
  bounds, constructed base projections, and nominal constraints.
- Complete cross-distribution access validation, interface-default conflict resolution,
  candidate-local inference for every lambda/array form, nested function-type effect matching,
  monomorphized body checking, checked effect coverage, smart casts, definite assignment, ref escape,
  constant evaluation, and generated data/delegation members remain incomplete.
- Default-expression origins and evaluation order are represented by the analysis API. Actual
  evaluation, virtual calls, native exceptions, standard library behavior and garbage collection
  require later verified FIR, LLVM and runtime implementation for each claimed feature. Only the
  separately qualified closed bootstrap subset produces native executables; semantic examples do
  not establish broader implementation coverage. The released Sol 0.0.1 component contains only the
  initial CST frontend.

The current body-analysis delta adds bounded candidate-local lambda replay, generic array-literal
evidence, unique top-level matching for multiple open effects, generic operator result evidence,
per-path hiding/override recovery, protected method receiver checks, and accessor visibility/effect
checks. These are source-analysis facts only; the contracts
above intentionally include broader requirements that remain pending.

The corpus is syntax-checked separately from semantic tests. Syntax validation does not supply
`std.core`, resolve library APIs, prove the rule comments, or execute the examples. Planned rules
remain documented even when their semantic tests or implementation do not exist.

See [argument contracts](../docs/arguments.md), [generic call analysis](../docs/generic-calls.md),
[generic inference](../docs/generic-inference.md), [member resolution](../docs/member-resolution.md),
the [path-sensitive flow and checked-effect guide](../docs/flow-and-effects.md), and the
[implementation status](../docs/status.md) for the supported API boundaries.
