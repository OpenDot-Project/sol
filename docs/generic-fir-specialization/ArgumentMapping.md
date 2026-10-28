<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Generic call argument mapping

This guide explains how a source call becomes a selected generic call and then a possible FIR call.
It focuses on the relationship between actual arguments, formal parameters, generic inference, and
the closed native specialization boundary. General source rules for defaults and varargs live in
[`arguments.md`](../arguments.md); the broader generic inference rules live in
[`generic-inference.md`](../generic-inference.md). The normative specialization contract and
call-site examples are in [`GenericFirSpecialization.sol`](../../spec/GenericFirSpecialization.sol)
and [`GenericFirArgumentMapping.sol`](../../spec/examples/semantics/GenericFirArgumentMapping.sol).

## Implementation status

The development compiler has a call binder that maps named and positional arguments, retains source
evaluation order, records defaulted and expanded parameter information, and attaches ordered generic
type arguments to the selected body-call binding. The first generic FIR slice consumes a selected
module-level free-function call, closes its type arguments, and emits a monomorphic `FirCall` when the
mapped values can be represented by the verified subset.

The current native subset supports named argument reordering and exact scalar `ref`/`out` calls when
the storage location maps to a verified FIR slot. It also lowers pure defaults on module-level free
functions when each omitted expression is a scalar/String literal, an earlier value-parameter read,
or a supported primitive unary/binary expression. Explicit expressions execute once in source order;
omitted defaults execute afterward in formal order. Calls to defaults are rejected if they target a
vararg function, if a default follows a ref/out parameter, or if an omitted expression needs a call,
member access, array operation, effect, or unsupported conversion. User varargs, expanded varargs,
array spread, and closure lowering remain outside this slice with `SOL2400`.

`out var` is represented in the source binder, which may assign its type after candidate selection.
The current native lowering path does not qualify every `out var` declaration shape. Do not treat
source binding as proof that the destination has a verified slot or that its lifetime and definite
assignment are safe. The examples mark those cases `SourceBound` or `not-qualified` when appropriate.

Closed generic free-function instances rerun body analysis under their function arguments. For a
generic owner member, the compiler now also has an internal on-demand source body view for one direct,
closed, top-level generic owner and complete method argument vector. That query rebinds the original
body, but ordinary member call analysis does not request it automatically and it does not admit the
result to FIR. Inherited/nested owner routes and full call-path integration remain future work. The
generic owner/member native surface continues to stop at `SOL2400`.

## Status and diagnostic labels

The adjacent fixture distinguishes the source contract from the executable subset:

| Fixture status | Meaning |
| --- | --- |
| `Specialized` | The call is expected to map and close, and its simple body is admitted by the current verified FIR subset. |
| `SourceBound` | Source analysis selects and types the call; native FIR qualification is explicitly absent or incomplete. |
| `SourceBoundNativeRejected` | Source mapping is complete, but the current native builder rejects this unsupported construct with `SOL2400`. |
| `Rejected` | Source argument structure, inference, conversion, or candidate selection fails before FIR construction. |
| `RejectedOrDeferred` | The available evidence is insufficient or analysis is incomplete; no verified target exists. |

`Planned` describes normative behavior whose implementation remains future work. It is not a source
diagnostic and does not mean the compiler currently accepts a call. Defaults, vararg array packing,
spread lowering, closure environments, full `out var` flow proof, and call-path-integrated generic
member body analysis are listed as remaining work. The bounded on-demand owner body view is a source
analysis result only. A source-invalid call uses its argument or inference diagnostic; a source-valid
call outside native admission uses `SOL2400`; an inconsistent FIR uses `SOL2402`.

## Terms and identities

An **actual argument** is one source argument written at a call site. A **formal parameter** is one
declaration parameter. Mapping connects the two without changing the source argument list. A
**parameter index** is its zero-based declaration position; a **source index** is its zero-based
position in the written call. A named argument changes the mapping, not either index space.

A **selected call binding** records the winning source callable, its receiver when present, ordered
generic type arguments, substituted formal parameter types and result type, source-to-formal
argument records, and omitted default parameter indexes. This is a source-analysis fact, not FIR and
not an executable artifact.

A **generic instance** is one generic free-function declaration plus one ordered tuple of closed
type identities. The displayed name `f<int,String>` is only a readable rendering; canonical identity
uses declaration identity and workspace-owned types, not strings or numeric type names.

A **verified FIR call** points to one concrete function index. It retains the type arguments that
were selected at the source call so the verifier can compare the source binding, target instance,
substituted signature, and argument origins.

The words **Bound**, **Rejected**, **Deferred**, and **Verified** describe different stages. Bound
means a source candidate was selected. Rejected means complete evidence rules out a source candidate.
Deferred means an unavailable source-analysis obligation could affect selection. Verified means FIR
passed its independent checks. None of these words should be substituted for another in diagnostics
or release notes.

## Mapping order

The call mapper resolves argument structure before generic inference. For each candidate it:

1. Reads the candidate's parameter list, passing modes, default declarations, and vararg position.
2. Scans actual arguments in source order and records each argument's source index and source mode.
3. Assigns a positional argument to the next eligible formal parameter.
4. Assigns a named argument to the formal with that exact name.
5. Rejects an unknown name, repeated formal, incompatible ref/out marker, or illegal positional gap.
6. Marks omitted parameters that have defaults.
7. Checks whether all remaining required parameters have been supplied.
8. Records the candidate-local mapping before expression typing and inference continue.

This order prevents inference from repairing an invalid argument list. For example, a generic type
that could fit the next free parameter does not make `f(second = x, y)` legal: once the source names
the second parameter, `y` cannot be silently moved backward into the first.

Each overload candidate owns its own mapping. One candidate's defaults, type arguments, conversion
ranks, array spread state, or rejected lambda result cannot change another candidate's state. Only a
unique best applicable candidate becomes the selected call binding.

## Positional and named mapping

Suppose a declaration is `func f(first: T, second: U, third: int)`. The mapping table below uses
source indexes on the left and formal names on the right.

| Call shape | Source-to-formal mapping | Result |
| --- | --- | --- |
| `f(a, b, c)` | `0→first`, `1→second`, `2→third` | Positional order |
| `f(first=a, b, third=c)` | `0→first`, `1→second`, `2→third` | Ordered mixing |
| `f(third=c, first=a, second=b)` | `0→third`, `1→first`, `2→second` | Full named reorder |
| `f(second=b, a)` | No mapping for `a` | Positional after reorder is rejected |
| `f(missing=a)` | No mapping for `a` | Unknown name is rejected |
| `f(first=a, first=b)` | First mapping exists only | Duplicate fill is rejected |
| `f(a, second=b, b)` | Third actual repeats `second` | Already-filled formal is rejected |

The exact ordered-mixing rule is specified in `docs/arguments.md`. In short, the mapper may keep
consuming the next positional slot while source order still corresponds to declaration order. After
a named argument skips a slot or reorders the selected formal, ordinary positional arguments cannot
guess the caller's intent. A trailing lambda follows its separate final-function-parameter rule.

Named argument names are source-level parameter names. Generic specialization does not rename
parameters, synthesize aliases, or use type argument spellings as argument names. A parameter name
appearing in multiple overloads is resolved separately against every candidate.

Overload resolution runs on the candidate mappings. A mapping error that invalidates all candidates
is reported as a call error; it is not a reason to select the first candidate in declaration order.
Unknown or duplicate named arguments do not produce partial FIR.

## Mapping is not evaluation order

Sol evaluates supplied argument expressions once in their written source order. Mapping later
associates each evaluated value with a formal parameter. Reordering named arguments changes formal
indexes, but it never sorts or reevaluates the source expressions.

For example:

```sol
module docs.generic_fir_specialization
func pair(first: int, second: int): int = first * 10 + second
func mark(value: int): int { println(value); value }

func example(): int {
    pair(second = mark(2), first = mark(1))
}
```

The `mark(2)` expression is source argument 0 and runs first. It maps to formal `second`. The
`mark(1)` expression is source argument 1 and runs second. It maps to formal `first`. The resulting
formal values are then passed to `pair`; the compiler must not execute `mark(1)` first just because
`first` is formal parameter 0.

The FIR builder stores arguments with parameter indexes but preserves their source origins. The
verifier checks that every FIR argument value unwraps to the corresponding source expression and
that the source mapping and mode match. This check matters even when two arguments have the same
type and a swapped pair would still pass a superficial signature check.

Expression evaluation remains distinct from default evaluation. All explicit argument expressions
run once in source order first. Only after a unique candidate is selected do omitted defaults run,
once each, in declaration parameter order. The selected candidate determines which default
expression applies.

## Generic inference after mapping

The binder builds generic evidence from mapped arguments. Each evidence record needs the candidate,
source index, formal parameter index, evidence relation, and source origin. It must not infer from
the argument's textual position alone.

| Mapped argument kind | Generic evidence | Final check |
| --- | --- | --- |
| Value argument | Input/lower bound for its mapped formal | Normal source conversion |
| `ref` storage | Exact type evidence | Writable storage with exactly the specialized type |
| `out` storage with a written type | Exact type evidence | Writable storage with exactly the specialized type |
| `out var` | No prior type evidence | Type is assigned only after one candidate wins |
| Explicit generic type argument | Fixed substitution for that ordinal | Constraints and all call conversions still apply |
| Expected result | Output/upper bound when the enclosing binder supplies it | Specialized result must meet that context |
| Omitted default | No inference evidence | Default value is checked after selection |
| Empty vararg | No element evidence | Type must come from elsewhere or be explicit |
| Lambda | Candidate-local contextual evidence if supported | Final callable shape and body must agree |
| Spread array | Element evidence only under the declared spread rule | Array element conversions are checked separately |

An explicit argument fixes one declared type parameter in declaration order. The evidence solver
does not replace it with a different inferred type. The completed mapping still checks the actual
value against the fixed formal type. An expected result may infer an unbound parameter, but it cannot
silently overwrite an explicit argument.

The same generic parameter can appear in multiple formal types. Each mapped value contributes
evidence to that parameter; the solver then applies the generic inference relation rules. A ref/out
exact identity and an ordinary value lower bound can conflict. The binder must retain both records so
the rejection points at the right arguments instead of widening the by-reference location.

Type arguments are ordered by the canonical type-parameter identities of the selected declaration.
Spelling is not an identity: owner `T` and function `T` are distinct. Parameter order is declaration
order even if named arguments arrive in a different order. Unused parameters remain in the vector and
cannot be filled from another declaration's substitution.

Defaults do not become actual arguments. A default expression may refer to earlier parameters under
the source contract, but it is not evidence for a generic parameter before candidate selection. An
empty vararg likewise cannot manufacture `Object` or the first declared constraint as a fallback.

## Exact `ref` and `out`

Ref/out mode appears at both declaration and call site. The marker belongs to the actual expression;
when a named actual is used, the value expression and mode remain attached to that source index.
`ref` must map to a `ref` formal and `out` to an `out` formal. A mismatch rejects the candidate.

By-reference evidence is exact. If `T` is inferred from a `ref` location of type `int`, the
specialized `ref T` formal is `ref int`. The compiler cannot widen it to `long`, wrap it as nullable,
box it into `Object`, or apply a user conversion. A second ordinary value argument must convert to
the already selected `T`; it cannot rewrite the exact location type.

The value must denote eligible storage. An immutable `val`, temporary expression, ordinary property
getter result, or value-returning call cannot be silently copied into a hidden temporary and passed
by reference. The native verifier requires a supported ref/out argument to be a `FirRead` of a
writable slot, with the exact target parameter type and matching passing mode.

The current native slice can lower scalar ref/out locations when source binding and FIR preserve the
same mutable local slot. It also supports a function ref/out parameter when its address and type are
otherwise within the admitted signature. This statement does not qualify every property, array
element, `out var`, ref-like value, or escaping lifetime shape.

## `out var`

An `out var name` actual has no preexisting local type, so it cannot contribute exact inference
evidence. Other mapped arguments, explicit generic arguments, or an expected result must determine a
unique candidate and type. After selection, the source binder can assign the selected out-parameter
type to the newly declared local.

The order is important:

1. Map the `out var` expression to one candidate's out formal.
2. Infer candidate type parameters from all independent permitted evidence.
3. Reject/defer if candidates remain ambiguous or the generic type remains unresolved.
4. Check constraints and final parameter modes.
5. Assign the selected type to the new local and record its source scope.
6. Prove definite assignment on every normal path before later reads.

The new local does not exist as a typed value during overload ranking. It cannot make one candidate
win because that candidate happens to provide a more convenient output type. If two candidates
remain equally good, `out var` does not break the tie.

The current native gate has not established all slot-creation and flow invariants for this syntax.
The source mapping is useful evidence, but `out var` should be treated as unqualified for verified
FIR until the call target, new local slot, source origin, out write, and post-call definite assignment
are all checked together.

## Defaults

A default is attached to a declaration parameter, not to a source argument index. The mapping records
which parameters are omitted and which selected declaration owns their expressions. It must not
pretend a default was written by the caller.

The full source contract orders evaluation as follows:

1. Evaluate each explicit source expression once, in source order.
2. Select one overload candidate using its mapping and generic evidence.
3. Apply the final closed substitution to each selected default's declared context.
4. Evaluate missing defaults in declaration parameter order.
5. Make values of earlier parameters available to later defaults.
6. Check default result type, effects, accessibility, and allowed parameter reads.

An explicit supplied value suppresses that parameter's default. A call can omit more than one default;
each omitted expression is evaluated at most once for that call. No default executes when a function
is declared. If an override inherits a default from a base declaration, its source owner remains
attached to the default record.

Generic specialization cannot treat a default as an inference shortcut. For example, if `T` occurs
only in an omitted default and not in an explicit input or expected result, the call has no evidence
for `T`. If the explicit argument fixes `T`, then the selected default must be checked under that
substitution. A default typed for a different `T` is an error; it does not create a separate
specialization or change the overload winner.

The native builder admits only the pure default expression subset described above. It rejects an
omitted default outside that subset with `SOL2400`; it does not omit the parameter or synthesize a
zero value. Earlier formal reads in a default become call-value references to values captured by the
call's ordered evaluation plan, so an explicit argument expression is never run twice.

## Varargs and spreads

The source language allows one `vararg` parameter. Positional actuals mapped to that parameter are
element expressions; a named vararg actual denotes the complete `[]T` array. `*array` expresses
element-wise spread under the source contract. The binder preserves which form was written because
array identity and element conversion have different rules.

| Source form | Source mapping | Generic evidence | Current FIR |
| --- | --- | --- | --- |
| `f(1, 2)` | Two actuals expand into one vararg formal | Each element contributes to `T` | Rejected: user vararg lowering |
| `f(values = items)` | One whole array fills the vararg slot | Invariant `[]T` shape can constrain `T` | Rejected: user vararg lowering |
| `f(values = *items)` | Named whole-array spread syntax | Array shape/elements follow source rule | Rejected: vararg lowering |
| `f(*items)` | Spread actuals expand in source order | Element evidence follows spread contract | Rejected: expanded vararg lowering |
| `f()` with empty vararg | Empty array slot | No element evidence | Rejected/deferred if `T` remains open |
| `f<T>()` with empty vararg | Empty array slot | Explicit `T` closes element type | Rejected: user vararg lowering |

A named array argument fills the vararg parameter once. It cannot be combined with additional
expanded values for the same parameter. An array passed as an ordinary positional element is not
automatically flattened; it has type `[]T`, not `T`. A `*` marker cannot be applied to a non-array.

Spread and expanded elements retain source order. Conversion is checked per element and cannot be
reordered to group equal types. A mapped array's element type does not become covariant merely
because the caller used a spread marker. Defaults of vararg parameters are a separate declaration
rule: an explicit array suppresses the default; omitted varargs use their specified default or empty
packing, without implicitly appending a default after supplied elements.

The generic call binder may inspect enough information to record a vararg mapping or infer an
element type. That does not mean the verified native subset can construct the required array, root
it, pass it through the runtime ABI, or preserve its allocation effects. Until those obligations are
implemented, these calls remain source-only or are rejected by the FIR gate.

## Trailing lambdas

The source mapper associates an outside trailing lambda with the final function-typed parameter
under the language's trailing-lambda rule. The lambda keeps its `IsTrailingLambda` origin fact. A
parenthesized lambda remains at its written position unless explicitly named; it cannot jump over a
required parameter just because another mapping would be convenient.

The generic binder may use the contextual function type to infer lambda parameter/result types and
may perform candidate-local body analysis. Mapping, lambda analysis, and closure conversion are
separate operations. After selection, a closed generic call can enter native FIR when its callback
is a noncapturing lambda with an exact supported ABI. Capturing closure environments and ABI
adapters remain outside this path. The native tests exercise trailing, named, parenthesized, and
zero-parameter callbacks, with both inferred and explicit generic type arguments.

The compiler must not let a losing candidate's expected lambda type leak into a winning candidate.
Each candidate's lambda body facts and inferred type arguments are isolated. If lambda analysis is
deferred and could change overload selection, the call remains deferred rather than falling through
to the first complete candidate.

There is no implicit `it` parameter. Lambda parameter names or `$0` references are governed by the
lambda source contract; they do not change the enclosing call's argument indexes. `ref`/`out` lambda
parameters describe the callback signature, not the passing mode of the lambda expression itself.

## Nested calls and forwarding

Nested calls have separate source argument arrays, mappings, and candidate selections. The inner
call first produces a typed expression under its selected source binding. The outer call maps that
expression as one actual. The outer formal cannot mutate the inner mapping or replace the inner
candidate's type arguments.

When a generic caller invokes another generic function, the caller's closed substitution is applied
to the nested call's selected type arguments before reserving the callee instance. For example,
`forward<T>(value: T) = identity(value)` means `forward<int>` calls `identity<int>`. The source
parameter owners remain distinct even if both are spelled `T`.

Repeated calls with the same target declaration and ordered closed types reuse one compilation-local
FIR function instance while retaining separate call nodes and source origins. Calls with distinct
argument tuples must never alias. A new declaration/argument tuple is reserved before its body is
built so an exact recursive call can target the reserved function ID. The function-count budget
prevents an unbounded chain of distinct instances.

The current implementation does not re-run the full source binder for each concrete generic body.
Consequently, generic bodies that require concrete-type operator/member overload selection are not
made safe merely by replacing `T` with `int` or `String`. Only the body shapes admitted by both
source analysis and the independent FIR checks can enter this slice.

## Body-call record and FIR fields

`BodyCallBinding` retains these call facts:

| Field | Purpose | FIR relationship |
| --- | --- | --- |
| `Call` | Source AST call identity | `FirCall.Origin` must point to this call |
| `Target` | Selected source callable | Target FIR function must retain its declaration origin |
| `Receiver` | Selected substituted receiver, when applicable | Generic owner/member calls remain outside this free-function slice |
| `TypeArguments` | Ordered callable-owned substitutions | Compared with the call and target instance |
| `ParameterTypes` | Final source-bound parameter types | Compared with the target FIR signature |
| `ReturnType` | Selected source result type | Compared with `FirCall.Type` |
| `Arguments` | Source-index/formal-index records | Used to check argument identity, order, and mode |
| `DefaultParameters` | Omitted defaults after selection | Lowered for the bounded pure free-function subset |

`FirCall.Target` is a function-array index, not a name. `FirCall.TypeArguments` repeats the closed
ordered type vector for verification and JSON inspection. The target `FirFunction.SpecializationArguments`
must carry the same vector. A nongeneric target has an initialized empty vector. A default immutable
array is not interchangeable with an explicitly initialized empty array where metadata is serialized.

`FirJson` schema 7 records ordered function specialization arguments, call type arguments, and
call-value references as compilation-local data. A default expression that reads an earlier formal
uses a `FirCallValueReference` to an earlier position in the call evaluation plan. Type IDs are not
persistent ABI identities or cross-build cache keys. Source byte spans remain available, while
absolute checkout paths are omitted. A consumer that assumed the previous schema must reject schema
5 or upgrade explicitly.

## Independent verifier checks

The FIR verifier does not trust a builder's type argument array in isolation. It checks the function
and call as one source-linked chain:

1. The target function's origin identifies exactly one source function declaration.
2. The function's specialization arity matches the source declaration's owned type parameters.
3. Each specialization type belongs to the current type workspace and is closed.
4. Source generic constraints are satisfied by the concrete ordered arguments.
5. Parameter, return, local, and effect types equal the source types after substitution.
6. The selected call's substituted type arguments equal both `FirCall` and target-instance metadata.
7. The source-bound parameter and result types agree with the target signature and call expression.
8. The argument mapping fills the expected parameters without duplication or omission.
9. Each FIR argument comes from the mapped source expression and preserves its passing mode.
10. Ref/out arguments refer to exact writable slots and satisfy the out-flow proof.
11. The function graph has a unique declaration-plus-type-tuple identity.
12. No unresolved parameter, unsupported native type, or unverified body node survives.

These checks defend against same-name target substitution. A second function with the same name and
signature cannot replace the selected declaration. Likewise, swapping two same-typed source values
does not pass merely because both types match. Origin identity and mapping records remain part of
the proof.

The checked-effect set is also tied to the selected target after type substitution. Removing an
effect, changing its order when the language treats it as a set, or using a different generic target
must fail verification. This native subset does not yet provide general catch/finally unwinding, so
source effect support must not be mistaken for runtime exception support.

## Diagnostics and failure phases

Structural source-call problems belong to argument mapping and overload selection. Examples include
unknown names, duplicate fills, missing required parameters, mode mismatches, no applicable
candidate, and ambiguity. Generic inference reports missing, conflicting, or ambiguous evidence in
its own phase. These source failures occur before FIR function instance creation.

A source-valid call that needs unsupported native lowering is reported by the FIR builder with
`SOL2400`. A corrupted or internally inconsistent FIR program is rejected by verifier diagnostics
(`SOL2402`). The bounded specialization graph/type-resource failures use `SOL2403`. Source inference
resource limits retain their own analysis diagnostics, including `SOL2290` where applicable.

The failure phase must be preserved. A native default-lowering gap is not a missing generic type
argument. A ref type mismatch is not an LLVM error. A verifier failure is not converted into a
successful empty function. A backend failure must not change which source candidate won.

For a rejected or deferred call, there is no verified generic target ID. It is invalid to drop one
argument, choose a different overload, erase a required effect, or substitute a fallback `Object`
type to keep output generation moving.

## Native support matrix

The table is deliberately specific to argument mapping and generic FIR. Other operations and types
may narrow the effective program further.

| Feature | Source mapping/analysis | Current native specialization status |
| --- | --- | --- |
| Direct module-level generic free-function call | Ordered explicit/inferred arguments | Supported for closed admitted types and body shapes |
| Repeated identical generic call | Distinct call sites, shared key | Same closed target ID within one compilation |
| Named argument reorder | Formal indexes differ from source indexes | Supported when no default/vararg blocker applies |
| Positional then named in declaration order | Stable source order | Supported when signature/body shapes are admitted |
| `ref`/`out` scalar local | Exact storage and matching mode | Supported where source slot and FIR proof align |
| `out var` local creation | Type after candidate selection | Not broadly qualified for native FIR |
| User default argument | Omitted parameter and default owner | Rejected by current native gate |
| User vararg | Element or whole-array mapping | Rejected by current native gate |
| Array spread | Source order and per-element mapping | Rejected by current native gate |
| Trailing lambda | Final function-parameter mapping | Supported for a closed selected target and exact-ABI noncapturing lambda; captures/adapters remain rejected |
| Generic member/owner call | Receiver substitution and member lookup; internal on-demand body rebind exists for direct closed owners only | Rejected by current native slice |
| Open generic argument | Deferred or rejected by source inference | Never a closed FIR instance |
| Unsupported type argument | Source mapping may still be valid | Rejected before verified FIR |

Passing a row in one source analysis phase does not imply the final native column is supported. A
program with multiple calls must have every call and every transitively required generic instance
admitted before LLVM lowering can start.

## Conformance record format

The adjacent `.sol` fixture annotates the source call with a stable case label and expected facts.
The record should state enough to distinguish mapping from inference and lowering:

- source call and source argument indexes;
- selected source declaration and overload result;
- parameter indexes and formal names;
- source evaluation order;
- passing modes and storage identities for ref/out;
- explicit/inferred ordered type arguments;
- substituted parameter and result types;
- omitted default/vararg/lambda metadata where present;
- instance identity and whether FIR is expected or absent;
- diagnostic category at the owning phase.

The fixture's labels are semantic oracle names, not an alternative CLI diagnostic catalog. If the
public compiler diagnostics change, update the mapping from category to stable diagnostic ID while
preserving source origins and phase ownership. Unsupported examples remain normative examples; they
do not claim a successful implementation.

## Maintainer checklist

Before changing call mapping or generic FIR, verify these boundaries together:

- Mapping occurs before generic evidence collection.
- Candidate-local maps and substitutions never leak between overloads.
- Named reorder changes formal indexes only.
- Source expressions execute once in source order.
- Defaults run only after selection and are not generic evidence.
- Empty varargs do not invent an element type.
- Ref/out modes are exact and refer to eligible storage.
- `out var` type assignment happens only after unique selection.
- Lambda and spread facts retain their own origin and conversion rules.
- Nested generic calls compose the caller's closed substitution by owner identity.
- Repeated keys reuse one target without merging call origins.
- `FirCall` and `FirFunction` metadata agree with the selected source binding.
- The verifier checks the substituted source signature, call mapping, and effects.
- Unsupported calls cannot leave a partially verified function or artifact.
- `FirJson` schema changes are explicit and every supported consumer is version-aware.

Do not use display names or argument text as generic identities. Do not infer evaluation order from
parameter indexes. Do not make backend availability influence overload ranking. Do not accept a call
because one particular example happens to lower; each mapping and body obligation must be proved by
the relevant compiler phase.

## Remaining work

The normative source rules cover more than the current backend. Remaining work includes wiring the
bounded closed owner body view into member call analysis, inherited/nested owner composition,
broader default evaluation and effect analysis, vararg array construction, spread lowering, closure
environments, full `out var` flow/lifetime proof, checked exception handling, and generic
artifact/export rules.

Each extension adds separate obligations. A default requires value/effect checks under the selected
substitution. A spread requires array representation, conversion, evaluation order, and roots. A
closure requires environment layout, capture ownership, and GC visibility. A generic member requires
receiver substitution, access checks, virtual dispatch rules, and source identity. These should not
be collapsed into one broad “generic call supported” flag.

Until those areas are implemented and verified, the compiler must continue to report the exact
unsupported feature rather than silently accepting a source mapping as executable code.
