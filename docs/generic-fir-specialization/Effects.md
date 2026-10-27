<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Generic effects through FIR specialization

## Status and scope

This guide describes how generic checked-effect types move from source call analysis into a closed
function instance and verified FIR. It covers the first native specialization slice: direct calls
to module-level generic free functions whose complete type-argument tuple is known and whose
concrete body types fit the current FIR/native subset.

The implementation retains the selected source call's generic type arguments, creates a
monomorphic FIR body for each distinct closed argument tuple, substitutes the body's resolved
types, checks declared effect metadata, and verifies caller budgets before the backend receives a
`VerifiedFirProgram`.

This is a bounded projection of existing generic source analysis. The compiler currently analyzes
the generic declaration body once in its open source context. It substitutes the resulting bound
type facts while constructing a concrete FIR instance; it does not yet re-run every semantic rule
against a fresh body analysis per substitution. Any rule whose validity can change after
substitution remains a proof gap or a native admission limitation. The implementation is not the
complete generic specialization contract in the language specification.

The following features remain outside this guide's implemented boundary:

- generic type owners, generic members, constructors, properties, and operators;
- virtual, indirect, or reified generic dispatch;
- arbitrary nominal object layouts, boxing, and ref-like generic arguments;
- generic C ABI exports and cross-module generic templates;
- full per-instance semantic rebinding when concrete types change applicability or flow;
- native checked exception unwinding, source `catch`, `finally`, and cleanup lowering;
- complete unordered matching of multiple open effect patterns.

A closed effect list in FIR is source metadata and a verification fact. It does not by itself
provide an exception object representation, a native unwind edge, or a runtime handler.

## Effect sets in source types

A function type carries ordered value parameters and modes, a result type, and an effect set. Value
parameter positions are ordered. Effect membership is not ordered. For example, these declarations
have the same effect membership even though their text order differs:

```sol
module docs.generic_fir_specialization
func readA(): String throws DiskFailure, NetworkFailure
func readB(): String throws NetworkFailure, DiskFailure
```

The generic type-argument tuple remains declaration-ordered. If `E` and `F` are parameters of one
generic function, `[DiskFailure, NetworkFailure]` means `E = DiskFailure` and
`F = NetworkFailure`. The effect set produced after substitution is independently normalized and
compared using the source exception identity rules.

An effect identity is a resolved constructed type in one analysis workspace. A printed name or
path is not the identity. `ReadFailure<int>` and `ReadFailure<String>` are different constructed
types; duplicate routes to the same constructed type do not create new set members. Type variables
are keyed by their declaration owner and ordinal, so same-spelling `E` parameters from two
declarations are not interchangeable.

The source exception hierarchy controls checked coverage. `Throwable` is the root, `Exception` and
its subclasses are checked unless they are under `RuntimeException`, and `RuntimeException`
descendants are unchecked. Classification comes from resolved ancestry; names such as `Failure`,
`Fatal`, or `Unchecked` have no special meaning.

An unchecked failure can still happen at runtime. “Unchecked” means that a caller does not need to
catch or declare it under the language's checked-effect rule. It does not mean that the failure is
removed from the function's source semantics or that a native runtime must suppress it.

## Source binding record

`BodyBindingBuilder.Calls` resolves a call candidate before FIR construction. It gathers explicit
type arguments, ordinary input relations, expected-result evidence, supported lambda facts, and
the bounded effect evidence currently available to body analysis. It checks the selected candidate's
constraints and produces substituted signature facts.

For each selected call, `BodyCallBinding` retains:

| Field | Meaning |
|---|---|
| `Call` | The exact source AST call and its origin. |
| `Target` | The selected source callable declaration. |
| `Receiver` | The resolved receiver identity where applicable. |
| `TypeArguments` | Function-owned type arguments in declaration order. |
| `ParameterTypes` | Concrete parameter types after candidate substitution. |
| `ReturnType` | Concrete result type after substitution. |
| `Arguments` | Source-to-parameter mapping and evaluation provenance. |
| `DefaultParameters` | Omitted parameters supplied by source defaults. |

Nongeneric calls have an initialized empty `TypeArguments` array. This is distinct from an
uninitialized/default array. A verifier can therefore tell “the selected callable has no generic
parameters” from missing specialization evidence.

The generic arguments are retained directly from the solver's substitution map. They are not
reconstructed by inspecting parameter types after substitution: a type parameter that appears only
in a result or an effect can still be inferred from expected-result or callback-effect evidence.
Inferring the tuple from the parameter list would lose that declaration-owned identity.

The call binder selects a candidate once and records that decision. FIR must not infer a different
effect type after selection, prefer a candidate because it has fewer effects, or rank a call using
the printed order of `throws` entries.

## Checked effects and generic inference

An effect variable such as `E` in `throws E` is a type parameter with an effect position. It is not
an extra return value and cannot be discovered by pretending that a thrown exception is the normal
result of the function.

When `E` also appears in an ordinary parameter or result position, those positions may close it.
The declared effect is then substituted using the same `E` identity:

```sol
module docs.generic_fir_specialization
func <T, E: Throwable> transform(
    value: T,
    callback: (T) -> String throws E
): String throws E = callback(value)

func readDisk(path: String): String throws DiskFailure = path
func caller(): String throws DiskFailure = transform("cache", { p: String -> readDisk(p) })
```

The selected tuple is `[String, DiskFailure]`; the call's declared checked effect is
`DiskFailure`. The parameter/result shape and the effect set are checked as separate parts of the
function type.

The current body analysis also has a bounded one-open-effect inference case. When exactly one open
effect pattern remains after fixed members match, and there is exactly one residual actual effect,
the solver can use that constructed type as evidence. It does not pair two open patterns by source
order. The current resolver does not implement complete set matching for multiple simultaneously
open patterns or all cross-lambda effect interactions.

The effect solver must distinguish these outcomes:

| Evidence | Meaning |
|---|---|
| One exact residual type and one open pattern | Candidate may infer that type, subject to bounds. |
| No residual type for a required open parameter | Missing evidence; do not invent an empty effect type. |
| Conflicting exact identities for one parameter | Candidate is inapplicable or remains incomplete. |
| Multiple valid assignments to open effect variables | Ambiguous/deferred; declaration order is not a tie-break. |
| An unresolved callback body | Incomplete candidate, not an effect-free callback. |

Effect inference does not widen `DiskFailure` to `Exception` just because the common base exists.
Any permitted function-effect conversion is applied after solving according to the language's
function type rules. It must not mutate the generic instance key or suppress checked coverage.

## Callback analysis and candidate isolation

A callback body contributes only effects from operations whose targets are actually selected:
calls, property accessors, setters, operators, constructors, and nested callbacks when those
operations are available to source analysis. A missing or ambiguous operation makes the candidate
incomplete. It is never represented as an empty set.

When overload resolution speculates on a callback under several candidates, each candidate owns
its own flow snapshot and effect facts. If a candidate loses, its inferred generic tuple, diagnostics,
assigned-local facts, and effect set are discarded. The selected candidate publishes its final
facts once.

The generic call resolver does not use effect count as a preference. Two candidates that differ
only by `throws` are not distinct overloads. Parameter conversion ranking remains governed by the
ordinary overload rules; effect compatibility filters a candidate and participates in caller safety.

Lambda effect inference, source checked-effect coverage, generic body projection, and native
exception handling are separate stages. Success in one does not establish completion of the others.

## Building a closed instance

`FirBuilder` first reserves deterministic IDs for ordinary source functions. While building those
functions, a selected direct call to a generic free function supplies an ordered closed type tuple.
The builder validates its arity, rejects open/ref-like/unsupported native types, rechecks the
declaration's type constraints, and interns a key consisting of the source function identity plus
the ordered resolved type IDs.

The key uses analysis-workspace identity, not a display string, file path, unordered set, or hash
iteration order. The same selected declaration and ordered concrete tuple share one FIR instance
within the compilation. Different tuples remain distinct even when their lowered LLVM types happen
to be identical, such as two nominal types that both lower to an opaque pointer. This preserves
source semantics and diagnostics.

The builder enqueues each new instance and then drains the queue. Calls found in an instance body
can enqueue another instance. Since a key is reserved before the body is built, an exact recursive
call can point back to the already reserved ID instead of expanding forever. Calls with different
closed tuples receive different IDs. The worklist order follows source declaration and expression
order so inspection remains deterministic.

For an instance such as `relay<DiskFailure>`, the builder creates a simultaneous map from that
function's canonical generic parameter identities to the closed types. It applies this map to:

- the function parameter slots and return type;
- local slot types and expression result types;
- generic argument tuples on nested calls;
- declared checked effect types;
- array element types when their representation is in the admitted subset.

The replacement values are not recursively rewritten a second time. This matches the shared type
substitution rule and avoids capture between same-named parameters owned by different declarations.

The current bridge projects the open source body's existing bindings. It does not rebuild the
body's symbol table or replay every generic semantic rule for each tuple. If a source construct
requires such replay to prove a concrete operation safe, FIR/native support must reject it until
that analysis exists.

## FIR records and verifier checks

Every specialized `FirFunction` records `SpecializationArguments`. A generic `FirCall` records the
same ordered target `TypeArguments`. The function and call both retain their original AST origin;
the clone does not become a new Sol declaration or gain a new user-visible name.

Before minting `VerifiedFirProgram`, `FirVerifier` checks the instance in its current function
context. It verifies:

1. the source function exists in the compilation and has the same name/origin;
2. generic arity equals the source declaration's owned parameter count;
3. each argument belongs to the current type workspace and fits the supported native type subset;
4. no argument contains an unresolved generic parameter;
5. the source declaration's constraints are satisfied by the concrete tuple;
6. the source body's result, parameters, and declared effects equal their substituted forms;
7. the function instance key is not duplicated in the FIR program;
8. a call's source binding, call type arguments, and target function tuple agree after applying the
   caller instance's substitution;
9. call result and declared target effects match source analysis;
10. each argument is the same source expression and parameter mapping, has the exact concrete type,
    and preserves its value/ref/out mode;
11. checked target effects fit the caller's verified effect budget;
12. ordinary FIR slot, operation, literal, and definite-assignment invariants still hold.

The verifier is independent of builder intent. A valid `BodyCallBinding` alone does not make a
function executable, and a serialized FIR document cannot recreate the private verified capability.
The LLVM emitter accepts only the result of verification.

These checks prove the concrete type and source identity facts represented in FIR. They do not
prove that source `catch`, native unwinding, cleanup, or exception-object construction has been
implemented. In this native subset, an effect declaration remains compile-time metadata and a
caller-budget obligation.

## Checked and unchecked effects in FIR

The generic tuple may contain `E = DiskFailure`, but only checked descendants require caller
coverage. If `E = FatalRuntimeFailure` and that type derives from `RuntimeException`, the instance's
source type argument still records `FatalRuntimeFailure`; the checked-budget checker does not treat
it as a required checked effect.

An unchecked effect is not converted to `!`, removed from the source declaration, or confused with
an empty set. The source's exception hierarchy determines classification. FIR carries the declared
effect metadata needed to compare source binding and target identity; the backend does not infer a
new exception type from an LLVM function signature.

If the same effect is repeated in a source contract, the semantic set rules decide normalization.
FIR verification compares the exact selected metadata produced by source analysis and rejects a
tampered list. Presentation order is not an exception conversion rule.

## Native exception boundary

The current verified FIR checks checked-effect budgets at calls and records effect identities in
flow side tables. The native backend does not yet lower exceptional CFG successors or source
`catch`, `finally`, and `defer` behavior. It does not emit a C++ exception specification for Sol
effects. A function that declares `throws DiskFailure` is not thereby able to throw that source
object through native frames.

Native runtime failures such as a failed checked arithmetic guard use their existing runtime error
path. That behavior is distinct from source checked exceptions. Do not infer source exception
semantics from a process exit code or a generated LLVM call.

To implement native EH later, the compiler will need a separate contract for exception object
layout, ownership and GC roots, normal and exceptional CFG edges, catch matching, cleanup ordering,
rethrow identity, cross-function unwinding, ORC guest isolation, and AOT/runtime ABI compatibility.
The effect metadata added here is a prerequisite for that work, not its substitute.

## Native admission and unsupported effects

Generic type arguments must be closed and supported by the current FIR value representation. This
includes supported primitive scalars, `std.core.String`, and one-level dynamic arrays of admitted
scalar/String elements. `half`, `quad`, ref-like arguments, nullable values, nested arrays,
function values, unsupported nominal objects, and generic member owners remain outside the first
native slice.

An unsupported candidate or body construct produces a source diagnostic, normally `SOL2400` for
the native subset gate. A specialization resource limit uses `SOL2403`; `SOL2402` is reserved for
independent FIR verification failures. Neither case may silently
create a default body, erase an effect, replace an open parameter by `Object`, or publish an
unverified LLVM module.

The native type gate is separate from source generic validity. A type may be legal in source
analysis and legal as a generic argument in the language, yet not have an admitted layout in this
native subset.

Generic type arguments are not automatically exported. This work does not define a stable generic
C ABI name, a generic `.solib` template format, or external instantiation linking. Only concrete
instances selected inside the compilation can enter the generated FIR graph.

## Serialization and artifact identity

`FirJson` inspection schema 4 adds resolved type IDs for function specialization arguments and
generic call target arguments. IDs are local to the analysis workspace and are not stable across
compilations. JSON is diagnostic output, not the verified-program transport format and not a cache
key.

The canonical in-memory key is declaration identity plus the ordered closed type tuple. Any future
persistent cache or `.solib` metadata must add an explicit compiler/runtime identity, target ABI,
constraint witness, effect signature, and schema version. Hashes can accelerate lookup but cannot
replace equality on the canonical identities.

AOT and ORC consume the same verified FIR. Link mode (`SolRT.dll` versus static SolRT) does not
change source type arguments or effect identity. ORC execution remains in the separate guest
process; generic specialization does not move guest objects into the host compiler process.

## Resource and cancellation rules

The first builder limits a compilation to 4096 distinct specialized functions and 128 levels of
nested type depth for each closed argument. Type interning has its own workspace budget. These
limits bound separate resources; they are not additive allowances and must not be disabled by an
optimization mode.

An exact recursive key resolves to the reserved instance. Expanding recursion that creates new
keys consumes the instance budget. When the budget is reached, the request receives `SOL2403` at
its source call. The builder must not return an incomplete function list that appears verified.

Cancellation is checked before reserving/building instances and while traversing function bodies.
A canceled compilation publishes no `VerifiedFirProgram`. Any future persisted instance cache must
also avoid publishing partial records after cancellation.

The 128-level type-depth check is not the same as generic call-graph depth. The distinct-instance
budget bounds graph growth; a future contract may define another explicit graph-depth limit if
semantic rebinding or recursive constraint solving adds a new unbounded dimension.

## Diagnostics and source locations

The source call origin identifies where overload selection requested an instance. A generic FIR
function retains the declaration origin for its body and signature. A nested call keeps its own
expression origin even when the containing body is specialized more than once.

Unsupported native shapes report the source node that blocks admission. Resource diagnostics must
not point only to an internal synthetic function number. The verifier reports the source origin
associated with the offending FIR node and does not downgrade a structural mismatch to a warning.

The user-facing message should describe the actual boundary: unsupported generic method, open
argument, unsupported native type, unsatisfied source constraint, duplicate identity, or exhausted
budget. An error must not suggest that the source generic itself is invalid when it is valid but
not executable by the current backend subset.

## Implementation matrix

| Capability | Current state |
|---|---|
| Keep generic function type arguments in body call binding | Implemented |
| Preserve ordered type arguments on FIR calls/functions | Implemented in schema 4 inspection |
| Build bounded direct free-function instances | Implemented for admitted native types |
| Substitute slots, expression types, results, nested call arguments, and effects | Implemented as typed FIR projection |
| Recheck every semantic rule in a fresh concrete body context | Not implemented |
| Generic class/object/method specialization | Not implemented |
| Full multiple-open effect-set matching | Not implemented |
| Verify generic constraints and concrete source signatures | Implemented in FIR verifier |
| Check caller checked-effect budgets | Implemented as source/FIR proof |
| Native checked exception unwinding | Not implemented |
| Source catch/finally/defer lowering | Not implemented |
| Generic C ABI or cross-module template linking | Not implemented |

The “implemented” entries describe the compiler state of this branch, not release availability.
The current released Sol 0.0.1 remains the initial CST frontend. A feature is not release-ready
until the repository's CI, artifact, documentation, and release gates establish its exact scope.

## Review checklist

When changing generic effect handling, check the complete chain rather than one record in
isolation:

- Does the source binding retain declaration-ordered arguments even when evidence comes from an
  expected result or effect position?
- Are type parameter identities owner/ordinal based, not name based?
- Does substitution cover the source declared effect list and nested callback function types?
- Can any unresolved parameter survive into a target function, call, local, or effect entry?
- Is the selected overload unchanged after candidate-local lambda replay?
- Does the target's closed argument tuple equal the call binding after caller substitution?
- Are checked types classified from the resolved exception hierarchy?
- Does the caller's checked budget cover every selected checked effect?
- Are unchecked effects left semantically distinct from an empty effect set?
- Does the verifier reject a tampered target, result type, effect list, or argument mapping?
- Are unsupported generic owners and ABI types stopped before LLVM construction?
- Does schema 4 expose enough inspection data without leaking source paths or pretending IDs are
  stable across sessions?
- Do AOT and ORC consume the same verified FIR while preserving host/guest process isolation?
- Does the public status describe source metadata accurately without claiming native unwinding?

Passing a source generic inference check is only the first proof. The generated body, selected call
graph, concrete signatures, effect budgets, and backend boundary must all agree before code is
eligible for native output.

## End-to-end trace

Consider a callback-taking declaration whose effect parameter is also in its declared effect set:

```sol
module docs.generic_fir_specialization
func <E: Throwable> invoke(callback: () -> String throws E): String throws E = callback()
func readDisk(): String throws DiskFailure = "disk"
func app(): String throws DiskFailure = invoke({ readDisk() })
```

The analysis and FIR stages handle this as a chain of distinct facts:

| Stage | Fact established | Fact not established |
|---|---|---|
| Parse | Generic declaration, lambda, call, and effect syntax have source nodes. | Type correctness. |
| Declaration binding | `E` belongs to `invoke`; `DiskFailure` is a Throwable subtype. | A concrete call instance. |
| Body call candidate | The lambda is checked against the candidate's callback shape. | Native exception unwinding. |
| Effect evidence | `readDisk()` contributes `DiskFailure` to the callback body. | A second unrelated effect argument. |
| Candidate solve | `E` is fixed to the residual constructed effect if the bounded one-open rules apply. | General unordered matching of any number of open patterns. |
| Source binding | `BodyCallBinding` stores the selected target and `[DiskFailure]`. | Successful FIR verification. |
| Builder queue | The key `(invoke declaration, DiskFailure)` is reserved once. | A stable cross-process cache key. |
| FIR projection | `invoke` slots, result, call types, and declared effects are closed. | Concrete semantic rebinding of every source operation. |
| FIR verifier | Source identity, type tuple, signature, call mapping, and checked budget agree. | A native catch edge. |
| LLVM emitter | Only a verified closed function graph enters LLVM construction. | A Sol exception ABI. |

The pipeline must preserve the difference between “this call is source-bound” and “this target can
be emitted.” If source analysis returns an incomplete callback candidate, the builder must not
manufacture the missing type argument. If the target body contains an unsupported feature, FIR
admission must report that feature rather than emitting a body with an omitted operation.

## Effect-set matching examples

Effect sets compare by member identity after the source rules normalize them. The following table
separates exact evidence from a future operation that remains outside the bounded solver:

| Formal set | Actual set | Required handling |
|---|---|---|
| `{DiskFailure}` | `{DiskFailure}` | Exact closed match. |
| `{E}` | `{DiskFailure}` | One-open residual may infer `E`. |
| `{DiskFailure, E}` | `{DiskFailure, NetworkFailure}` | Remove the fixed member, infer the unique residual. |
| `{E, F}` | `{DiskFailure, NetworkFailure}` | Do not zip members; remain ambiguous/deferred. |
| `{E}` | `{}` | Missing evidence; do not infer a no-throw type. |
| `{DiskFailure}` | `{NetworkFailure}` | Exact mismatch unless source conversion rules explicitly allow coverage later. |
| `{E}` | `{DiskFailure, NetworkFailure}` | Do not infer a common base automatically. |
| `{E}` | `{FatalRuntimeFailure}` | Unchecked failures do not count as checked-effect evidence. |

An implementation must not treat a function's type-argument list as ordered effect membership.
For `func <E, F>`, the argument tuple is ordered; for `throws E, F`, the set is not. Reversing
source `throws` order must not alter candidate applicability or the chosen mapping.

When a function value has an already-known effect type, that static contract remains relevant even
if a particular body path does not throw every declared effect. In contrast, an untyped lambda's
actual body effects are collected from selected operations. These two data sources cannot be
collapsed into one “effects observed at runtime” list.

## Effect identity under generic substitution

Suppose a generic exception type is used as an effect:

```sol
module docs.generic_fir_specialization
class ReadFailure<T>(val item: T): Exception("read")
func <T> read(value: T): T throws ReadFailure<T> = value
```

After `read<String>("x")`, the effect type is `ReadFailure<String>`. After `read<int>(1)`, it is
`ReadFailure<int>`. Even if both values use the same machine pointer representation or the body
does not construct the exception, the resolved identities differ.

Generic parameter ownership matters in nested declarations. If a caller has its own `E` and calls
another generic function with the caller's `E`, the inner call's binding initially contains the
caller-owned parameter identity. When the caller instance is closed, the builder applies the
caller's substitution to that inner argument before interning the target key.

The verifier repeats this comparison. It does not trust the builder's key. For the current caller
instance it substitutes the source binding's open target arguments and compares those concrete
identities to all of the following:

- the `FirCall.TypeArguments` tuple;
- the target `FirFunction.SpecializationArguments` tuple;
- the substituted source binding's parameter and result types;
- the target function's declared effect list.

A mismatch is an invalid FIR program. It is not repaired by substituting the target's type arguments
into the call or by choosing a different overload during verification.

## Checked coverage with a generic caller

Checked-effect coverage is evaluated in the caller's current function context. For a nongeneric
caller, the context substitution is empty. For a specialized generic caller, the verifier first
closes its type variables, then checks the closed effects of each call.

For example, if an outer function is specialized with `E = DiskFailure`, a nested `throws E` call
must be checked as `throws DiskFailure`. Comparing the raw source binding's open `E` to the target's
closed `DiskFailure` would either reject a valid call or accidentally accept an unrelated effect.
The caller substitution is part of the verifier contract for every nested call.

A caller's effect budget remains a declared/caught source property. The backend cannot add a wider
budget just because it can lower both functions to `ptr ()` or another common LLVM signature. LLVM
types are coarser than Sol function types and do not carry generic exception identities.

An unchecked effect is filtered only for the mandatory checked budget. The exception declaration
and specialized function metadata remain source-owned facts. This distinction prevents the
verifier from using “unchecked” as an alias for “no exception exists.”

The current catch pipeline is incomplete for native code. Source analysis can reason about catches
in supported syntax, but the verified native subset rejects catch-shaped bodies. A successful
source catch analysis cannot be substituted for an exceptional CFG or a machine-level handler.

## Repeated instances and effects

The canonical instance key excludes the call-site effect spelling. It consists of the selected
declaration identity and its closed generic argument tuple. If two source sites select the same
declaration with `E = DiskFailure`, they share one function instance even when the calls occur in
different functions or their `throws` lists were written in different orders.

The call sites remain distinct. Each `FirCall` retains its own origin, arguments, expected result,
and caller budget context. Sharing the callee body does not merge caller flow graphs or make one
call's catch cover another call.

Two calls to `readFailure<String>` and `readFailure<int>` produce distinct canonical instances
because their arguments differ. Hash collisions, display-name collisions, and equal LLVM pointer
types must not merge them. Equality is checked on the resolved tuple after hashing.

Exact recursive calls reuse a reserved instance ID. A recursive call that grows a type tuple
creates a new key and consumes the instance budget. The current native type admission rejects many
constructed forms before unbounded growth is possible, but the resource cap remains necessary for
future admitted types and nested generic graphs.

## Artifact and runtime separation

The verified FIR object is an in-memory capability. `FirJson` serializes a versioned inspection
view; it is not loaded back as a trusted executable representation. Schema 4 exposes
`specializationArguments` on functions and `typeArguments` on calls as workspace-local type IDs.

The generic type ID and effect type ID are not stable ABI numbers. A future persistent artifact must
encode canonical declarations/types and the compiler, runtime, target triple, and artifact schema
version. A serialized bare integer such as `17` cannot identify `DiskFailure` outside its original
workspace.

AOT and ORC both consume the same verified FIR and the same LLVM 23.1.3 C++ API bridge. Selecting
dynamic versus static SolRT linkage does not change the effect tuple. ORC remains a separate native
guest process; the compiler's host GC and the guest Yellow Tea heap are isolated by process
boundaries.

The current native bootstrap does not promise that a source checked exception can cross that
process boundary or a generated function frame. Generic effect metadata is not a serialization
format for exceptions, a remote error protocol, or a catch table.

## Authoring future conformance cases

When adding an effect case to the normative `.sol` matrices, record the source context and the
expected result explicitly. A useful scenario states:

1. the generic declaration and owner of each open type parameter;
2. fixed effect members before inference;
3. actual callback operations that contribute effects;
4. whether each effect is checked or unchecked by the resolved hierarchy;
5. the expected closed type-argument tuple, if it is unique;
6. the substituted declared call effect set;
7. the enclosing caller's catch/declaration budget;
8. whether source analysis, FIR verification, and native admission each accept it;
9. the source call span and the target declaration origin that must remain linked;
10. any resource or incomplete-analysis boundary involved.

Do not label a source example as native-supported solely because its generic arguments are closed.
It must also satisfy the target ABI subset, body binding, FIR source contract, checked-effect proof,
and supported operation rules.

For rejected cases, distinguish the reason. A type-argument inference conflict is not a native ABI
error. An unsupported generic method is not a syntax error. An exhausted specialization budget is
not a checked-effect failure. A caller that lacks a checked budget is not an invalid generic
declaration.

For ambiguous cases, state the competing assignments. Avoid an oracle that merely says “fails,”
because that does not reveal whether the solver recognized ambiguity, missing evidence, or an
unsupported body.

For unchecked cases, assert both that checked coverage is not required and that the source type
identity is preserved. This helps prevent a future optimization from removing all unchecked
metadata and accidentally treating runtime failure as impossible.

For catch cases, state separate source-analysis and native-lowering expectations. A catch may be a
valid source construct while remaining outside the current native subset. The native gate must fail
closed until exceptional CFG and cleanup semantics are represented.

## Maintainer review questions

Before changing the implementation or contract, verify each layer:

- **Source declarations:** are effect types Throwable-derived and checked classification based on
  resolved ancestry?
- **Inference:** is the generic type-argument tuple complete, ordered, and candidate-local?
- **Effect set:** are open and fixed patterns matched without positional pairing?
- **Substitution:** are all declared and nested function effects rewritten simultaneously?
- **Body binding:** are selected callback effects complete, and are rejected candidate facts rolled
  back?
- **Instance key:** does it use canonical declaration and ordered closed type identity?
- **Nested call:** is the caller's substitution applied before the inner key is formed?
- **Function signature:** do concrete parameters, result, and effects match the source declaration?
- **Call signature:** do type arguments, target, result, argument mappings, and modes agree?
- **Checked budget:** are all checked target effects handled or declared by the caller?
- **Unchecked failures:** are they omitted only from mandatory checked coverage, not mislabeled as
  an empty source effect set?
- **Verifier:** can a manually altered FIR target or effect list still mint `VerifiedFirProgram`?
- **Lowering:** are unsupported exception paths rejected before LLVM construction?
- **Inspection:** does JSON schema 4 show argument identities without exposing host paths?
- **Artifacts:** are generic instances kept out of stable public C ABI names and unversioned caches?
- **Status:** does documentation separate implemented FIR projection from planned full rebinding and
  native exception support?

The critical safety property is agreement: source overload selection, the body binding, the
specialized function, the `FirCall`, the verifier's caller budget, and the backend all describe one
closed target and one effect contract. No single layer can compensate for a mismatch in another.
