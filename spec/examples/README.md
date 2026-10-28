<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Source examples

These examples illustrate source shapes, intended behavior, and selected negative semantic cases.
Most examples are syntax/AST fixtures, not programs runnable by the Sol 0.1.0 native subset. A line marked Expected diagnostic is intentionally invalid at the semantic layer and documents the required rejection. Every file carries its own implementation status. Native execution and standard-library support are limited to the subset described in the release notes; full semantic verification remains incomplete even where syntax is accepted.

The development-only [first native program](native/FirstProgram.sol) is the executable exception to
that planned status. It uses the qualified scalar/SolRT bootstrap described in the
[native guide](../../docs/fir-native.md); it does not imply native support for the other examples.

| Example | Explicit source set | Intended behavior |
|---|---|---|
| [Console greeting](console/Greeting.sol) | One file, plus the future standard library | Main prints a greeting and each argument |
| [Argument ordering](calls/ArgumentOrdering.sol) | One file | Ordered named/positional calls, middle vararg, defaults and trailing lambda |
| [Default inheritance](calls/InheritedDefaults.sol) | One file | Base default contract with derived dispatch |
| [Checked configuration](errors/Configuration.sol) | One file, plus the future standard library | Local recovery and declared checked failures |
| [Nullable arrays](data/NullableArrays.sol) | One file | Distinguishes element and container nullability |
| [Value copying](data/Points.sol) | One file | Struct copy changes independently |
| [Collections](collections/VectorUsage.sol) | One file, plus the future standard library | Vector snapshot and mutable borrow boundaries |
| [Payload switch](flow/Commands.sol) | One file | Enum payload dispatch and exhaustive returns |
| [Delegated settings](delegation/Settings.sol) | One file, plus the future standard library | Separate owner state and shared property descriptor |
| [Generic bounds](generics/NamedItems.sol) | One file | Nominal constraints and explicit generic calls |
| [Native clock](native/Clock.sol) | One file | Verified unsafe Windows C call in a later backend |
| [First native program](native/FirstProgram.sol) | One file plus the compiler-owned bootstrap library | Development AOT executable with dynamic or static SolRT |
| [Partial type](modules/Panel.Storage.sol) | Both `Panel.Storage.sol` and [Panel.Behavior.sol](modules/Panel.Behavior.sol) | One type split across files of the same logical module |
| [Conventional layout](layout/src/myproject/example/Greeter.sol) | `Greeter.sol` and [Main.sol](layout/src/myproject/example/Main.sol), rooted at `layout/` | Lowercase module directories and PascalCase source files |
| [Constructed base members](semantics/ConstructedBaseMembers.sol) | One file | Multi-edge generic base substitution, inherited properties and overrides |
| [Inherited overload groups](semantics/InheritedOverloadGroups.sol) | One file | Base overload retention, derived replacement, named calls and defaults |
| [Constrained receiver](semantics/ConstrainedReceiver.sol) | One file | Multiple generic bounds, constrained member lookup and repeated base identity |
| [Expected-result inference](semantics/ExpectedResultInference.sol) | One file | Generic results inferred from a local expected type; missing evidence |
| [Invariant inference](semantics/InvariantInference.sol) | One file | Exact nested arguments versus direct common-type bounds |
| [Ref/out inference](semantics/ExactRefOutInference.sol) | One file | Exact storage evidence, out-variable type and conflicts |
| [Vararg inference](semantics/VarargInference.sol) | One file | Middle varargs, spread, named array, and empty evidence |
| [Nullable inference](semantics/NullableInference.sol) | One file | Nullable payloads, null evidence, and nullable arrays |
| [Lambda context](semantics/LambdaContextInference.sol) | One file | Candidate expected types, named/positional forms, no implicit `it` |
| [Projection diamond](semantics/ProjectionDiamond.sol) | One file | Repeated constructed path versus conflicting generic base paths |
| [Accessor visibility](semantics/MemberAccessors.sol) | One file | Independent getter/setter access, effects, and ref storage |
| [Safe member calls](semantics/SafeMemberCalls.sol) | One file | Single evaluation, skipped arguments, flow narrowing and assertion |
| [Method type shadowing](semantics/MethodTypeShadowing.sol) | One file | Distinct owner and method generic parameter identities |
| [Protected receiver](semantics/ProtectedReceiver.sol) | One file | Derived access through this, derived receiver, and invalid base receiver |
| [Generic bound join](semantics/GenericBoundJoin.sol) | One file | Unique common class, expected interface, and incomparable bounds |
| [Expected base projection](semantics/ExpectedBaseProjection.sol) | One file | Result inference through a generic base/interface chain |
| [Member hiding](semantics/MemberHiding.sol) | One file | Nearest value ownership and no fallback after failed lookup |
| [Type and object receivers](semantics/TypeAndObjectReceivers.sol) | One file | Singleton, companion, instance, and local shadow categories |
| [Function effect inference](semantics/FunctionEffectInference.sol) | One file | Checked callback budgets and unordered effect evidence |
| [Generic constructor inference](semantics/GenericConstructorInference.sol) | One file | Owner arguments from constructor values and nested construction |
| [Constraint after inference](semantics/ConstraintAfterInference.sol) | One file | Candidate type selection followed by owner/method bound checks |
| [Base defaults and overrides](semantics/BaseDefaultsAndOverrides.sol) | One file | Static named/default contract and derived body validation |
| [Multiple-bound members](semantics/MultipleBoundMembers.sol) | One file | Independent members, shared declarations and bound conflicts |
| [Explicit generic arguments](semantics/ExplicitGenericInference.sol) | One file | Explicit owner/method arguments, arity and conversion errors |
| [Nested nullable generics](semantics/NestedNullableGenerics.sol) | One file | Nullable wrappers within invariant constructed types |
| [Member name categories](semantics/MemberNameCategories.sol) | One file | Property/method ambiguity and singleton receiver lookup |
| [Bounded lookup cycles](semantics/BoundedLookupCycles.sol) | One file | Repeated constructed path identity and bounded traversal |
| [Deferred overload candidate](semantics/DeferredOverloadCandidate.sol) | One file | Why incomplete candidates can prevent a unique selection |
| [Literal evidence](semantics/LiteralEvidence.sol) | One file | Exact token value, default type, target range and variables |
| [Multi-bound conflicts](semantics/MultiBoundConflicts.sol) | One file | Same-name declarations across independent constraints |
| [Receiver and method arguments](semantics/ReceiverTypeArgsAndMethodArgs.sol) | One file | Independent owner/method generic parameter sequences |
| [Nullable member evaluation](semantics/NullableMemberEvaluation.sol) | One file | Conditional receiver, skipped arguments and branch proof |
| [Generic lambda evidence transaction](semantics/GenericLambdaEvidenceTransaction.sol) | One file | Lambda evidence replay, candidate-local state and fixed-point dependency |
| [Generic array call evidence](semantics/GenericArrayCallEvidence.sol) | One file | Literal-to-element inference, contextual recheck and array common types |
| [Generic effect callback evidence](semantics/GenericEffectCallbackEvidence.sol) | One file | Bounded unique multi-open checked-effect inference and unresolved effect recovery |
| [Member path hiding routes](semantics/MemberPathHidingRoutes.sol) | One file | Hidden member on one route with a preserved alternate inherited route |
| [Protected callable receiver matrix](semantics/ProtectedCallableReceiverMatrix.sol) | One file | Protected calls through this, super, derived and base-typed receivers |
| [Accessor effect visibility routes](semantics/AccessorEffectVisibilityRoutes.sol) | One file | Independent getter/setter access and operation-specific effects |
| [Generic lambda result join](semantics/GenericLambdaResultJoin.sol) | One file | Lambda body result evidence and unique common result types |
| [Generic candidate recovery matrix](semantics/GenericCandidateRecoveryMatrix.sol) | One file | Rejected, deferred and surviving generic overloads |
| [Inherited generic overload paths](semantics/InheritedGenericOverloadPaths.sol) | One file | Constructed inherited overload paths and override recovery |

Source sets are explicit. A directory path does not determine a module, create a project root or
discover dependencies. Several examples intentionally define their own `Main`; do not combine
independent application examples into a single executable compilation.

The conventional application layout follows module segments under `src/`, using lowercase directory
names and PascalCase `.sol` filenames. The `layout/` example demonstrates this normal convention;
other examples are grouped by subject for documentation. Departing from the conventional module
path remains legal and does not change the declared module identity.
