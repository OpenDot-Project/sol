<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Verified FIR generic specialization

This document describes the maintainer contract for specializing module-level generic functions
into closed FIR bodies and records the source-only boundary for generic owner member body views. The
subject is the boundary between source binding and the native subset.
It does not define the full Sol generic language, its source syntax, or the eventual public ABI.

The central requirement is simple: LLVM receives only a verified FIR program whose function
signatures, expression types, calls, effects, and flow facts have already been tied back to the
selected source declarations. A type argument written in JSON or stored on a call is not by itself
a proof that the generated function body is safe.

## Status of this document

The bounded source-to-FIR slice described here is implemented in the current feature branch. It is
narrower than the language specification, and this document does not claim test or CI completion.
Use the table below to avoid confusing source-analysis generic-call support with verified native
body support.

| Capability | Source analysis | Verified FIR/native slice |
| --- | --- | --- |
| Generic function declarations and call binding | Implemented for the currently supported analysis surface | Emitted only when a closed native specialization is admitted |
| Ordered generic type arguments on a resolved call | Preserved by body-call binding | Recorded on `FirCall` and compared by the verifier |
| Generic module-level free-function bodies | Open bodies are checked using declaration-owned type parameters | Closed instances are rebound and rechecked through `BodyAnalysis`; unsupported body forms remain outside verified FIR |
| Generic methods and generic owners | Member calls are analyzed in source contexts. An internal on-demand query can rebind one direct closed top-level generic member body for exact owner and method vectors; ordinary call analysis does not request that view. | Rejected by the verified native subset, including a successfully rebound member body |
| Native type support | Broader than the native backend | Limited by `FirVerifier.Supported` and the selected ABI slice |
| Generic function values | Expected function type closes each type parameter; overloads are ranked after closure | Exact-signature free-function and stateless-object references are admitted for supported native layouts, including vararg arrays and ref/out modes |
| Closure capture | Analysis may retain facts or defer | Not admitted; no environment object or capture-root ABI exists |
| User-defined operator target | `BodyAnalysis` retains the selected overload and closed signature | Direct `FirCall` form is limited to nongeneric, non-lifted operators with exact operands and supported native layouts; nominal value types still stop with `SOL2400` |
| Constraints | Checked during candidate selection | Rechecked when an instance is built and when FIR is verified |
| Resource limits | Analysis and type interning have their own budgets | 4,096 instances, 16,000,000 specialized body nodes, and 128 type levels |

The implementation status of a row must be confirmed from the compiler, focused validation, and CI
before calling it complete in release notes. A design statement in this document is not evidence that
a particular source program compiles or that LLVM emitted correct machine code.

## Compiler trust path

The relevant pipeline is:

1. CST and PSI preserve the source syntax and exact source spans.
2. AST nodes retain source origins, declarations, and generic parameter lists.
3. `SourceAnalysis` resolves declarations and written types.
4. `BodyAnalysis` binds each call to one selected target and retains its specialized signature.
5. `FirBuilder` reserves and builds ordinary functions and closed generic function instances.
6. `FirVerifier` independently checks the resulting source-to-FIR contract and definite assignment.
7. `VerifiedFirProgram` is minted only if every verifier obligation succeeds.
8. `LlvmEmitter` accepts the capability and lowers the already verified program through the LLVM C++
   API bridge.

The verifier does not trust a `HasErrors == false` result from an earlier phase. Body analysis can
contain deferred facts; FIR must reject an unverified obligation rather than treat it as a default
value. Generic specialization is another point where facts are projected into a different type
context, so the same rule applies: every substituted fact is checked against source-owned data.

No additional executable IR is introduced for this feature. The specialization is represented as
ordinary `FirFunction` and `FirCall` nodes with closed `ResolvedType` identities. The CFG remains a
side-table derived from FIR for flow verification; it is not a second body representation.

## Source identity and generic parameters

Generic parameters are declaration-owned identities. `TypeAnalysis.GetTypeParameters` returns them
in source declaration order. Each `ResolvedType.Parameter` carries a `GenericParameterIdentity`
with the owner declaration and zero-based ordinal. Names are presentation only.

For example, the `T` in a free function and the `T` in a different function are not interchangeable
because their spellings match. The `T` declared by a generic owner and a method-level `T` are also
different identities. A specialization map must use the exact `ResolvedType` parameter keys returned
by the source analysis for the target declaration.

The ordered argument vector has one position for each parameter identity:

```text
declaration: func <T, U> choose(first: T, second: U): T
arguments:   [int, String]
substitution: T -> int, U -> String
```

Do not infer this vector from a function signature after substitution. An unused parameter may not
occur in any parameter or return type, but it remains part of the declared arity and can be supplied
explicitly. The call binder is the source of the selected ordered arguments.

Substitution is simultaneous. If a caller instance maps `T` to `int`, then a nested call binding
whose argument is `T` is specialized once to `int`. The replacement is not recursively rewritten
using another mapping for the replacement value. This keeps declaration identity and avoids
order-dependent chains.

## On-demand source views for generic owner members

`BodyAnalysis.GetGenericMemberBody` is an internal source-analysis query for a narrowly bounded
member shape. Given a source member, a direct constructed owner, and the complete ordered method
argument vector, it rechecks constraints and binds the member's original AST body under both maps.
The resulting `SpecializedBodyAnalysis` records the constructed owner and the owner and method
argument vectors separately. It recomputes supported call selection, expression types, return facts,
checked effects, nullable types and flow; it does not reuse the open body's selected call table.

The query accepts a direct canonical top-level generic class, struct, ref struct or interface that
declares the member. Partial parts must already have merged to one validated declaration identity.
Owner and method vectors must have exact arity, belong to the current `TypeSpecialization` workspace,
and contain no open type parameter. Owner constraints are checked against every validated part;
method constraints are checked after the owner map is applied to their bounds. A failed proof produces
an invalid result with `SOL2221`; an indeterminate proof stays Deferred. A wrong owner, incomplete
vector, foreign type, nested declaration or inherited receiver projection is a rejected API request,
not a source-language diagnostic emitted for an ordinary call.

Successful results are keyed by the source `AstFunction` identity and separate ordered owner/method
type ID vectors. A cached result is returned only after cancellation is checked. Invalid, Deferred,
canceled, diagnostic-bearing or incomplete results are not cached as Bound. The cache is local to one
`BodyAnalysis` workspace; numeric type IDs must not escape into serialized output or persistent
identity.

This query does not run automatically from ordinary member call binding, is not part of the public
frontend SDK, and does not create FIR. Generic owner calls still fail native admission with
`SOL2400`. It proves no object layout, receiver ABI, dispatch slot, allocation, ref-like escape,
exception unwind, GC descriptor, root map, write barrier, AOT body or ORC body. The eventual FIR
builder and verifier must independently anchor a selected call to source and prove every runtime
obligation before LLVM can consume it.

## Body call binding contract

`BodyCallBinding` is the immutable bridge between body analysis and FIR. For each selected source
call it retains:

- the original `AstCall` and selected `AstCallable` target;
- the receiver type where the source binding has one;
- declaration-ordered generic `TypeArguments` for the selected callable;
- substituted parameter types and result type;
- source argument-to-parameter mappings, including passing mode information;
- defaulted parameter indexes.

Nongeneric calls carry an initialized empty type-argument array. This is distinct from an uninitialized
default `ImmutableArray`, which is malformed compiler metadata. Generic calls carry all selected
arguments, including explicit arguments and arguments inferred from input expressions, lambda
evidence, arrays, or expected result types.

The binder must finish inference and constraint checking before constructing `BodyCallBinding`.
FIR is not a second overload resolver. `FirBuilder` consumes the one selected target and its
substituted facts; the verifier later confirms those facts still agree with the original binding.

Operator selection follows the same rule through `BodyOperatorBinding`. It is anchored to the
original `AstOperation` or compound `AstAssignment` and stores the selected function, generic
arguments, substituted parameter/result types, effects, and whether nullable lifting was used.
Speculative candidate analysis snapshots this table with calls and expressions so a rejected
overload cannot leave a stale target behind. When the native builder admits an operator, it emits an
ordinary `FirCall`; source correspondence checks the selected declaration and each operand, and the
call verifier checks its closed signature and effects. There is no operator dispatch at runtime.

Current FIR admission is deliberately narrower than source binding: only nongeneric, non-nullable,
value-parameter calls with exact operand types and already supported native layouts can become a
`FirCall`. User-defined operators require an owner type, and nominal value layouts are not yet in the
verified native type set, so those programs still stop with `SOL2400`. Nullable lifting, implicit
conversions, checked/generic operator targets, and logical `true`/`false` plus `&`/`|` lowering do not
cross this boundary. A selected source overload must never be replaced with a built-in FIR unary or
binary operation merely because that operation has a convenient backend implementation.

Defaults, expanded varargs, safe calls, and other forms that do not have a qualified native lowering
remain independently rejectable by the native subset. Preserving generic arguments in a call binding
does not imply that every argument-mapping form can be represented in FIR.

## Admission boundary

The first native specialization slice is for module-level generic free functions. A target is
eligible only when all of the following are true:

- The selected target is a source `AstFunction` with at least one function type parameter.
- The function is not nested in a type declaration. Generic members and methods are excluded.
- The call binding supplies one ordered argument for every function type parameter.
- Caller-context substitution closes every type argument.
- Each argument is admitted by the native type predicate and is not ref-like.
- The declaration's constraints are proven satisfied by `TypeSpecialization.CheckConstraints`.
- The function has a body and no unimplemented native feature disqualifies the body.
- The specialization graph stays within configured instance and type-depth limits.

An ordinary function remains on the existing path. A generic source function is not emitted as an
open template body. It is emitted only as one or more closed instances requested by resolved calls.
The source declaration itself remains the canonical origin for each generated FIR instance.

This boundary is intentionally conservative. It admits no `T` in the final parameter, local, return,
effect, or expression types. When any source fact stays open, the compiler reports a diagnostic and
does not produce a verified program.

## Instance identity and scheduling

One function can have multiple native bodies. The identity of an instance is the source function
identity plus the ordered IDs of its closed type arguments within that `TypeSpecialization` workspace.
Do not key instances by display name, rendered type name, or parameter spelling.

The source declaration identity prevents unrelated overloads with the same name from colliding.
The ordered IDs distinguish `pair<int, String>` from `pair<String, int>`. IDs are meaningful only
inside the current analysis workspace; they must not be persisted as global type identities.

`FirBuilder` reserves the ID for a new function instance before it queues the body for construction.
That ordering is required for recursion. A recursive call with the same function and same closed
arguments finds the already reserved ID and refers to it. The queue then builds each instance body
once. Mutual recursion similarly terminates when calls return to an existing key.

If recursive calls keep creating new type argument combinations, the graph is not finite. The
compiler must stop when the maximum instance count is reached. It must not recurse in the host stack,
silently truncate the graph, or emit a partially verified module.

The build order is deterministic for the same source distribution and options:

1. Ordinary declarations are assigned IDs in source declaration order.
2. Their bodies are built in that order.
3. Newly discovered generic instances are reserved in deterministic call traversal order.
4. The pending instance queue is drained in reservation order.
5. The resulting array is emitted by contiguous function ID.

Changing collection iteration or sorting keys must not change which source call selected which
instance. If a new deterministic ordering is adopted, update the serialization tests as part of the
same change.

## Specialization metadata in FIR

`FirFunction.SpecializationArguments` records the closed, declaration-ordered type arguments for a
monomorphic body. It is initialized to an empty array for ordinary functions. `FirCall.TypeArguments`
records the closed arguments for a generic target and is empty for an ordinary target.

The pair allows the verifier to check three identities at each call site:

1. The call's source binding selected the same source declaration as the target FIR function.
2. The source binding's arguments, after substituting the caller instance, equal the FIR call's
   `TypeArguments`.
3. The FIR call's arguments equal the target function's `SpecializationArguments`.

The call result type and parameter types are checked against the source binding after the same caller
substitution. The target signature is then checked independently against the target declaration's
signature under the target's own specialization map.

This duplication of evidence is deliberate. Call metadata describes the selected edge in the call
graph. Function metadata describes the body at the other end of that edge. If either side is edited
or serialized incorrectly, the equality checks fail.

The FIR JSON inspection schema is version 4 for this shape. It serializes function specialization
arguments and call type arguments as workspace-local resolved-type IDs. The JSON is an inspection
format, not a mechanism for importing a verified capability or reconstructing arbitrary FIR.

## Building a specialized body

For each instance, the builder creates a map from the source function's generic parameter identities
to the instance's closed arguments. It uses that map consistently for the full body projection.

The substitution applies to:

- the inferred body return type;
- written parameter types;
- local declarations with an explicit written type;
- expression binding types, including call results;
- declared checked-effect types;
- array iteration element types;
- nested generic call arguments before instance lookup.

The local slots still refer to source `LocalSymbol` identities. Substitution changes a slot's type,
not the symbol that identifies the declaration. This preserves lexical scope, assignment, and origin
checks across the generic body projection.

Expression and statement origins remain the original AST origins. A specialized instance does not
copy or synthesize source spans. Diagnostics therefore point to the generic declaration or selected
call site that caused the failure. Debug output can distinguish instances by source origin plus the
ordered specialization vector.

Do not clone a generic `FirFunction` body to create a second instance. The open body is not a valid
verified FIR template. Build each instance from the source AST and the completed `BodyAnalysis` under
its substitution context, so every type-bearing node is projected from source-owned facts.

## Verifier entry and function identity

`FirVerifier.Verify` checks function IDs against their array positions. A specialized body keeps the
source origin of its generic declaration, so lookup from FIR origin to AST is one-to-many rather than
one-to-one. Use the specialization vector to distinguish those bodies.

Before checking a body, the verifier:

1. Locates exactly the source function anchored by the FIR function origin.
2. Retrieves the declaration-owned type parameter identities in order.
3. Checks that the function metadata has the same arity.
4. Validates ownership and native support for each argument.
5. Rejects any argument that still contains a generic parameter.
6. Builds the source parameter-to-argument substitution map.
7. Rechecks constraints on the source declaration.
8. Rejects a duplicate source identity plus ordered argument vector.

An ordinary function has no specialization arguments. A generic FIR function with no type arguments
is not treated as a generic template; it fails the arity check. A nongeneric function with nonempty
specialization metadata also fails.

Generic functions under a type owner are rejected even if the owner appears stateless. This avoids
mixing owner type parameters, method type parameters, receiver substitution, dispatch, and generic
instance caching into the first native slice.

## Signature verification

The verifier compares the specialized FIR signature with the source declaration under the function
instance's own substitution map. It checks the result type, parameter count, parameter names, exact
parameter types, passing modes, and declared effects.

The source body summary must be present and bound. Its return type is substituted and compared with
`FirFunction.ReturnType`. If the summary has no result type, a generic instance cannot invent one.
The body must also satisfy the existing native subset rules for statement shapes, slot identity,
storage, and function completion.

Every FIR slot and expression type is checked for analysis-workspace ownership and membership in the
native supported type set. A type with the same name from another analysis is not interchangeable.
The interner's IDs are not a substitute for `ValidateOwnership`.

Local slot types are checked independently from source declaration metadata. This catches a mutation
that changes the source signature but leaves locals open, or changes a local's type without changing
its initializer and reads.

The verifier must not trust only the root function's signature. Every node can carry a type: constants,
reads, calls, arrays, index operations, arithmetic, returns, and conditional expressions are all
checked through their own invariants.

### Terms used in this guide

- **Declaration identity** is the AST node and canonical symbol selected by analysis.
- **Type argument** is a closed `ResolvedType` substituted for one declaration-owned parameter.
- **Instance** is one source function paired with one ordered argument vector.
- **Specialized body** is FIR rebuilt from source facts under one instance substitution.
- **Verified capability** is the `VerifiedFirProgram` value accepted by native lowering.

These terms are deliberately separate. A type argument does not identify a function by itself, an
instance does not create a second source declaration, and an open generic body is not verified FIR.

## Constraint verification

The call binder checks constraints to choose an applicable candidate. Instance creation repeats the
constraint check after caller substitution. FIR verification checks constraints again using the
function metadata and source declaration.

The verifier accepts only a `Satisfied` result. `Unsatisfied` is a failed proof. `Indeterminate` is
also a failed proof: the verifier cannot turn unresolved bounds into a success just because earlier
analysis did not report a source error.

The checked argument vector must match the declaration's own parameter order. Do not use the enclosing
generic caller's parameter order or a receiver's owner arguments to validate the method's parameters.
The current native slice rejects generic owners and members before such a composition would be
needed.

Ref-like values remain outside this slice. A future expansion that supports `allows ref struct` must
prove layout, lifetime, capture, storage, and escape restrictions in the new lowering. Passing the
constraint check alone is not enough to establish those obligations.

## Call-site verification

For each `FirCall`, the verifier validates the target index before looking up the target function.
It then matches the call origin back to one source `BodyCallBinding`. The source call binding is the
authority for overload selection and argument mapping.

The verifier checks:

- selected source target origin equals target FIR function origin;
- call type arguments equal source type arguments under the caller instance substitution;
- call type arguments equal target function specialization arguments;
- call result type equals the substituted source result type;
- target result type equals the call result type;
- selected checked effects equal target function effects after substitution;
- every source argument maps to exactly one compatible FIR argument;
- every FIR argument has the selected parameter index and passing mode;
- each argument value has the exact specialized target parameter type;
- `ref` and `out` values retain the existing exact writable-storage proof.

The verifier does not rerun overload ranking. If a target differs from the source binding, the program
is invalid even if the new target happens to accept the same argument types.

Argument evaluation order is represented by the source binding mappings and the order of the FIR
arguments. Named arguments may map to different parameter positions; the verifier checks the
source-to-FIR map rather than sorting the emitted arguments by type or name.

Safe calls, defaulted parameters, and expanded varargs are still rejected where they lack a qualified
native representation. Generic metadata does not weaken those restrictions.

## Checked effects

The generic function's declared thrown types are substituted with the same function map as its
signature. Each specialized effect must still be a valid descendant of `std.core.Throwable` and must
appear in the source declaration's ordered effect metadata.

At a call site, the selected target's source effects are substituted through the caller's instance
map and compared with the target instance effects. The existing checked-effect budget then verifies
that checked effects are contained in the caller's declared budget.

Do not erase an effect because the specialized body currently contains no throwing instruction.
Function effect metadata is part of the source contract and the call graph. It controls legality at
the call site even if a particular body has no observed throw.

Source analysis now has bounded inference for multiple top-level open effect patterns when it proves
one complete assignment. Verified FIR still needs dedicated ordered substitution for the generic
argument tuple and canonical set representation for effects. Nested function-type sets, ambiguous
assignments and effect verification must not rely on display names or unordered iteration order.

## Supported native type boundary

The current subset is defined in `FirVerifier.Supported`. It admits supported primitive scalar
types, the empty tuple, `std.core.String`, and one-dimensional managed arrays whose element is an
admitted scalar or `String`. The backend excludes `half` and `quad` until their native ABI and
operations are implemented.

The specialization argument check rejects:

- open generic parameters at any nested type position;
- ref-like argument types;
- types outside `FirVerifier.Supported`;
- nominal applications that lack a proven native layout;
- function, tuple, nullable, or multidimensional forms without an implemented native layout. The
  current nullable exception is a closed exact-ABI function pointer and an array element of that
  same type; other nullable wrappers remain unsupported.

This is not the full runtime object model. The verifier must not infer representation from a source
type name. String uses its known runtime representation, arrays use the qualified runtime array ABI,
and primitive layouts follow the explicitly supported native type mapping.

If the supported type set expands, update the independent native type verifier, LLVM lowering,
runtime root handling, and ABI tests. An argument admission rule without matching body and ABI checks
can create a false verification claim.

## Resource and recursion limits

The current FIR specialization builder caps generated generic instances at 4,096 per compilation,
accounts at most 16,000,000 specialized source-body AST nodes, and caps specialization type depth at
128. These limits are guardrails, not source-language semantic features. Resource exhaustion must
produce a bounded diagnostic and no `VerifiedFirProgram`.

The verifier also keeps its existing recursion limits for nested FIR expressions and validates CFG
convergence. Those limits remain separate from generic instance count. A program can hit one budget
without hitting the others.

Tests should cover at least:

- a recursive function that reuses the same instance key and terminates specialization;
- two distinct closed argument vectors that create distinct instances;
- nested generic calls where caller substitution closes the callee arguments;
- an expanding chain that reaches the instance budget and fails closed;
- a deeply nested type argument that is rejected before unbounded traversal.

Do not catch `OperationCanceledException` and translate it into an ordinary unsupported-source
diagnostic. Cancellation remains cancellation. Do not replace a resource diagnostic with a partial
program or a warning.

## Origin and diagnostic policy

Generic instances preserve their source declaration origin. Each `FirCall` preserves its call-site
origin. The verifier uses those identities to tie target functions and call bindings to the same
source declaration graph.

Builder admission failures use the native-subset diagnostic path. Resource exhaustion uses the
resource diagnostic path. Source semantic failures remain owned by analysis and keep their original
diagnostic codes. Do not invent a fake source span or synthetic module path for a compiler-generated
instance.

If the builder sees a type it cannot specialize, it must identify the responsible call or declaration
in the diagnostic. If the verifier sees malformed metadata, it must reject the FIR at the corresponding
function or call origin. Diagnostics should remain deterministic and bounded by the existing compiler
diagnostic limit.

## Serialization and observability

`FirJson.Serialize` is deterministic for the same verified program. Schema 7 contains:

- a function's `specializationArguments` as an ordered array of type IDs;
- a call's `typeArguments` as an ordered array of type IDs;
- ordered explicit/default call values and `FirCallValueReference` nodes for earlier captured formals;
- `FirFunctionReference` target IDs and `FirIndirectCall` function/argument trees;
- each function's source name, ID, signature, effects, slots, and body;
- the source `@escaping` contract on every formal parameter slot;
- function type parameter modes and checked effects in the resolved type table;
- the derived flow graph, where indirect-call sites have a null direct target.

An empty argument array means an ordinary function call or body. It does not mean that generic
arguments are unknown. Unknown or default arrays should be rejected before serialization.

The inspection JSON is not an executable interchange format. It omits parser state, source workspace
ownership capabilities, and verifier construction authority. Never deserialize it directly into a
`VerifiedFirProgram`.

Keep host paths out of serialized output. Source positions are offsets and lengths relative to the
source object; they should not serialize local checkout paths or temporary build directories.

## Failure and recovery behavior

`FirCompilation.Create` returns no verified program when the builder emits an unsupported diagnostic,
when analysis has errors, or when verification fails. `Program != null` is the only success result for
this API. `HasErrors == false` on an intermediate analysis object is not sufficient.

The expected bounded behaviors are:

| Condition | Expected behavior |
| --- | --- |
| Generic call has no unique selected target | Source analysis diagnostic; no FIR program |
| Generic call leaves a parameter open | Source inference defers or reports; FIR is not certified |
| Type argument unsupported by native ABI | Native-subset diagnostic; no verified program |
| Generic owner or generic method target | Rejected by native subset |
| Constraint proof is unsatisfied or indeterminate | Instance rejected |
| Instance or type-depth budget is exceeded | Resource diagnostic; no partial output |
| Call target index is invalid in mutated FIR | Verifier rejects target identity |
| Call substitution does not match its target instance | Verifier rejects the call |
| Function signature differs from substituted source | Verifier rejects the function |
| Checked effect metadata is removed or changed | Verifier rejects source/call effect contract |

Some malformed `FirProgram` shapes may cause API argument validation to throw, such as an invalid
entry index. Tests should distinguish those invalid construction conditions from verifier diagnostics.
Both outcomes must prevent minting `VerifiedFirProgram`.

## Tampering boundaries

The FIR records are immutable records for ordinary use, but callers can create modified copies with
record `with` expressions. The verifier is therefore required to recheck rather than trust that FIR
came from `FirBuilder`.

Adversarial verifier tests should independently mutate:

1. `FirCall.TypeArguments` while preserving the call's source origin.
2. `FirFunction.SpecializationArguments` while preserving the function body.
3. A target ID so a call points at another source declaration.
4. A specialized parameter type while keeping the argument expression unchanged.
5. A return type while keeping the body return unchanged.
6. An effect list while keeping the source function declaration unchanged.
7. The function ID or array position.
8. Two instance records to duplicate one source plus argument vector.
9. A nested expression type or slot type that reintroduces an open parameter.
10. A source argument mapping or passing mode that no longer matches the call.

Each mutation should start from a valid verified fixture, call `FirVerifier.Verify` on the altered
`FirProgram`, and assert that no new verified capability is returned. A test that merely checks for
an exception in the parser does not exercise the FIR tamper boundary.

Tamper tests should use workspace-owned replacement types. A foreign type should be tested separately
as an ownership failure, because it may fail before the generic signature comparison.

## Test organization

The smallest useful regression is an inferred scalar call that produces one specialized body and a
call targeting its ID. The suite should then build outward to explicit type arguments, `String`,
arrays, nested calls, multiple instances, and generic recursion.

The body-binding tests should assert that argument order and substitutions are retained before FIR.
FIR tests should assert a verified capability, closed signatures, and call-target identity. Lowering
tests should inspect the LLVM API trace or run the native integration suite when the bridge is
available. Safety tests should mutate one invariant at a time.

Negative cases must identify the phase that rejects them. A parser rejection does not prove that FIR
is safe. A semantic rejection does not prove the verifier rejects mutated metadata. A verifier test
does not prove LLVM lowering uses the target ID. Keep these claims separate in test names and reports.

When adding fixtures, use one source scenario per named case or make the matrix columns explicit.
Include the selected type argument vector, expected result type, diagnostic expectation, and expected
instance count. Keep each source file under the repository LOC limit and keep generated test data
deterministic.

## Native lowering requirements

The LLVM bridge receives an ordinary monomorphic `FirFunction` signature. It declares one LLVM
function for each FIR function ID, then emits each body and resolves call targets through the integer
ID. No generic type parameter reaches LLVM.

The generic feature must not introduce textual LLVM construction. Type-to-ABI mapping stays in the
existing typed lowering path. If a specialization contains `String` or an array, the existing runtime
representation and GC root rules still apply. The specialization mechanism does not change object
ownership, safepoints, or Yellow Tea semantics.

The same verified FIR representation is used for AOT and ORC. Backend choice cannot change overload
resolution, inference, constraint satisfaction, function identity, or checked-effect legality.
Backend-specific failure must occur after FIR verification and must not alter source binding facts.

## Extending the slice

Each next capability should be added in dependency order:

1. Prove source binding retains all evidence needed for the new generic construct.
2. Define how owner and function parameters compose, preserving declaration identities.
3. Define a finite, canonical instance key and bounded scheduling behavior.
4. Substitute every type-bearing body fact, including local and control-flow facts.
5. Extend source anchoring and independent verifier checks.
6. Extend the ABI predicate and native lowering for every newly admitted type.
7. Add positive, negative, recursion, resource-limit, and tamper regression cases.
8. Update the JSON schema only when its observable contract changes.
9. Update the language specification and generic-call docs so planned and implemented claims stay
   accurate.

Do not begin with class layout or generic interface dispatch. Those features require owner
substitution, constructed inheritance, receiver lookup, object layout, vtable dispatch, and runtime
semantics that are outside this first free-function slice.

Exact-ABI function arrays and nullable function pointers have bounded verifier and runtime contracts.
Closure conversion, receiver capture, broader default and vararg forms, ref-like arguments, and
generic export ABI still need their own explicit design and verifier contracts before admission.

## Maintainer review checklist

Before merging a specialization change, confirm every item that applies:

- [ ] The selected source target is preserved as an AST identity, not a name lookup.
- [ ] Type arguments are ordered by declaration identity and include unused parameters.
- [ ] Nongeneric metadata uses initialized empty immutable arrays.
- [ ] Substitution is simultaneous, workspace-owned, and cancellation-aware.
- [ ] Caller substitution is applied to nested generic call arguments.
- [ ] Instance reservation happens before body construction for recursive references.
- [ ] Duplicate source-plus-argument identities cannot appear in verified FIR.
- [ ] Instance-count and type-depth exhaustion fail closed.
- [ ] Function result, parameters, locals, expression types, and effects are all substituted.
- [ ] Constraints are proven during selection and independently rechecked by the verifier.
- [ ] `FirCall` arguments exactly match the specialized target signature and passing modes.
- [ ] Checked effect metadata remains consistent at both the function and call boundaries.
- [ ] Unsupported generic methods, owners, ref-like types, and ABI forms remain rejected.
- [ ] The verifier rejects record-copy tampering and never reuses a stale capability.
- [ ] AOT and ORC consume the same verified typed FIR behavior.
- [ ] Serialized JSON records specialization vectors deterministically and contains no host paths.
- [ ] Focused tests identify parser, analysis, FIR, verifier, and lowering claims separately.
- [ ] Public docs state what is actually implemented, not merely specified.

If an item cannot be proved by code and a regression, leave that shape outside the verified subset.

## Diagnostic triage notes

When a generic source compiles but `FirCompilation.Create` returns no program, inspect diagnostics in
phase order rather than changing the verifier first:

1. Parser diagnostics indicate that the fixture does not use accepted syntax.
2. Declaration or type diagnostics indicate the generic declaration or written type is not valid.
3. Body analysis diagnostics indicate unresolved or conflicting call evidence, an invalid body, or an
   effect/flow issue.
4. Native subset diagnostics indicate that a valid source feature is not implemented by FIR/native.
5. Verifier diagnostics indicate the builder emitted a shape that failed its independent proof.
6. LLVM bridge errors occur only after a verified program exists and indicate native lowering issues.

Do not suppress a verifier diagnostic by broadening the admission predicate. First identify whether
the builder's substitution map, source bindings, and specialized slot types all agree. If the source
facts are ambiguous, fix the source analysis or keep the source form deferred.

For `SOL2400`, read the subject in the diagnostic and identify the unsupported source node. The code
means a source construct is outside the verified native subset; it does not mean the source is
semantically invalid in the full language.

For `SOL2403`, identify which bounded resource was hit. Do not retry with a larger unbounded limit as
the first repair. Check whether an instance key is missing a type argument, whether recursion is
expanding type arguments, or whether substitution repeatedly constructs equivalent types.

## Review examples

### One closed scalar instance

```sol
module app
import std.stdio

func <T> identity(value: T): T = value

object Main {
    func main(args: []String) {
        println(identity(42))
    }
}
```

The selected body call carries `T -> int`. FIR contains the ordinary entry plus one monomorphic
`identity` function with `int` parameter and result. The call target references that function ID.
This example is a contract illustration; its behavior must still be covered by the actual compiler
test suite before being treated as a passing smoke fixture.

### Two separate instances

```sol
module app
import std.stdio

func <T> identity(value: T): T = value

object Main {
    func main(args: []String) {
        println(identity(42))
        println(identity("sol"))
    }
}
```

The source declaration origin is shared by both bodies. Their specialization vectors differ, so the
instances receive different IDs. The two call nodes preserve their respective argument vectors.
The verifier must not merge them merely because they have the same source name and body.

### Nested substitution

```sol
module app

func <T> identity(value: T): T = value
func <U> relay(value: U): U = identity(value)

object Main {
    func main(args: []String) {
        val answer = relay(42)
    }
}
```

The `relay` instance is specialized with `U -> int`. Its nested call binding originally refers to
`U`; caller substitution turns that value into `int` before identity instance lookup. The resulting
graph contains `relay<int>` and `identity<int>`, and both bodies have closed signatures.

If a nested binding remains open, the compiler must reject the native program. It must not emit
`identity<U>` or rely on LLVM to resolve a source-level generic parameter.

## Source-of-truth files

The implementation is distributed across these source files:

| File | Responsibility |
| --- | --- |
| `src/managed/OpenDot.Sol.Analysis/BodyAnalysis.cs` | Immutable call binding metadata |
| `src/managed/OpenDot.Sol.Analysis/BodyBindingBuilder.Calls.cs` | Candidate selection and ordered type arguments |
| `src/managed/OpenDot.Sol.Fir/FirBuilder.cs` | Ordinary function reservation and body projection |
| `src/managed/OpenDot.Sol.Fir/FirBuilder.Specializations.cs` | Instance keys, type closure, and bounded queue |
| `src/managed/OpenDot.Sol.Fir/FirBuilder.Expressions.cs` | Specialized expression types and call targets |
| `src/managed/OpenDot.Sol.Fir/FirBuilder.ForEach.cs` | Specialized iterable element types |
| `src/managed/OpenDot.Sol.Fir/FirNodes.cs` | FIR call/function specialization metadata |
| `src/managed/OpenDot.Sol.Fir/FirVerifier.cs` | Independent type, source, constraint, and call checks |
| `src/managed/OpenDot.Sol.Fir/FirJson.cs` | Deterministic schema-7 inspection output |
| `src/managed/OpenDot.Sol.Lowering/LlvmEmitter.cs` | LLVM function declarations and ID-based calls |

`docs/generic-calls.md` describes the broader source analysis surface. `spec/GenericFirSpecialization.sol`
and `spec/GenericFirSpecializationCases.sol` define the language-facing contract and case matrix.
Those documents must remain explicit that generic source analysis is broader than the native verified
subset.
