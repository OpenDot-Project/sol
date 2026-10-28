<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Generic overload selection before FIR specialization

## Purpose

This page explains how a selected generic call becomes an input to the bounded FIR specialization
path. It focuses on one rule: source overload selection finishes before native admission begins.
Code-generation availability must never change which source declaration wins.

The normative case matrix is in
[`GenericFirOverloadSelection.sol`](../../spec/examples/semantics/GenericFirOverloadSelection.sol).
The broader generic function instance contract is in
[`GenericFirSpecialization.sol`](../../spec/GenericFirSpecialization.sol). The implementation is a
restricted development slice; the fixture annotations do not claim that all rows are executable or
that the case matrix has passed a conformance harness.

The managed source binder already resolves generic direct calls, constructs candidate-local
substitutions, checks supported constraints, and ranks applicable candidates. The FIR bridge now
retains ordered generic type arguments on each `BodyCallBinding`; `FirBuilder` can create closed
module-level free-function instances from those bindings. The current implementation substitutes
already-bound body facts. It does not rerun the entire source binder against every concrete instance.

## Phase boundary

The call pipeline has four distinct decisions:

1. **Lookup** forms the source-visible overload group after imports, aliases, lexical shadowing,
   and access rules.
2. **Applicability** maps actual arguments, collects permitted generic evidence, solves a candidate's
   type arguments, validates constraints, and checks conversions and passing modes.
3. **Ranking** compares all complete applicable candidates using source-language rules.
4. **Native admission** checks the unique winner's body, closed signature, effects, and ABI types.

Only the first three steps decide the source target. The fourth step can reject compilation, but it
cannot return to step three and choose a worse overload. This separation keeps source semantics
independent of the selected target machine, AOT versus ORC mode, LLVM availability, or cache state.

### Source-selected target

A source call is bound to a declaration identity, not to a display name. The identity matters when
two overloads share a name, when generic parameters share spellings, and when a backend emits
multiple specializations of one source function.

`BodyCallBinding.Target` retains the selected source callable. Its `TypeArguments` array is ordered
by the declaration's type-parameter ordinal. Its parameter and result types are already substituted
for that selected call. Argument mappings still point back to the original source argument indexes.

For a nongeneric call, `TypeArguments` is an initialized empty immutable array. It is not `default`.
Consumers can distinguish a nongeneric binding from a missing body binding without relying on a
null array or a sentinel type ID.

### Native instance

When a generic free-function call is selected and admitted, `FirBuilder` reserves an instance before
processing its body. The instance identity combines the source declaration and its complete ordered
closed type-argument tuple. Two separate calls with the same key reuse one FIR function; calls with
different arguments produce distinct functions even when their display names match.

The call stores `FirCall.TypeArguments`. The target function stores
`FirFunction.SpecializationArguments`. The verifier checks that both vectors agree with the source
binding after applying the caller's current substitution.

## Candidate applicability

Candidate applicability is computed independently. Evidence found for one candidate must not mutate
another candidate's substitution, lambda state, array element join, or diagnostics. A candidate is
then one of four source outcomes:

| Candidate state | Meaning | Can win? |
| --- | --- | --- |
| Applicable | Signature, arguments, constraints, and supported evidence form a complete match | Yes |
| Rejected | A proof shows a mismatch, conflict, arity error, or unsatisfied constraint | No |
| Deferred | A required analysis is unavailable or a budget is reached | Not by pretending it failed |
| Ambiguous evidence | More than one closed solution remains for that candidate | No unique candidate result |

These are not native backend outcomes. A native unsupported body does not retroactively make its
candidate source-inapplicable. In particular, a generic body cannot lose a source tie merely because
its instance is more expensive to construct or its lowered operations are unsupported.

### Argument mapping comes first

Named and positional actuals are mapped to parameters before inference evidence is collected. The
mapping preserves source evaluation order; it does not reorder effects to match parameter order.
Duplicate names, missing required parameters, incompatible spread forms, and invalid argument modes
reject a candidate before its type arguments are committed.

An actual contributes evidence only through a mapped formal. An omitted default does not provide
type evidence. An empty vararg does not choose an arbitrary type. A named vararg array is checked
against the declared array element type. Trailing lambda syntax is mapped to its function parameter
before contextual body analysis begins.

`ref` and `out` are exact storage positions. A numeric widening or reference conversion cannot be
applied to a ref/out address. If a candidate expects `T` in a ref/out parameter, the actual storage
type must be exactly the selected substituted type. `out var` can acquire its type only after a
candidate has supplied a unique parameter type; it cannot guess the generic argument that would make
that candidate applicable.

## Generic evidence and the selected substitution

The type parameter identity is `(declaration owner, ordinal)`. Parameter spelling is not enough. A
function's `T` and a containing owner's `T` are different identities, even if both are written with
the same letters. The first native specialization slice accepts free functions only, but this identity
rule is needed before any member specialization can be safely added.

For each candidate, the binder starts with explicit arguments and any already-fixed owner mapping.
The remaining parameters can receive only evidence allowed by the source analysis contract:

- ordinary mapped input types provide lower-bound evidence;
- mapped ref/out types provide exact evidence;
- a surrounding expected result can constrain a generic result;
- generic operator operands and their surrounding expected result use the same structural solver;
- supported array, lambda, constructed-base, or effect matching can contribute structural evidence;
- a defaulted parameter that was omitted contributes no evidence;
- a call elsewhere in the file contributes no evidence to this call.

For an overloaded generic operator, expected-result evidence can close a return type parameter that
does not occur in the operands. It does not change the operand conversion rank vector. Constraints
are checked on the completed substitution, and a missing or conflicting result remains deferred or
inapplicable; it is never filled with an arbitrary type.

The result is an ordered vector. If the declaration is `func <T, U> pair(...)`, the stored vector is
`[value-for-T, value-for-U]`, even if inference discovered `U` before `T`. A vector with any open
parameter is not a closed native instance.

The substitution is simultaneous. If `T` maps to `U` and `U` maps to `int`, a direct occurrence of
`T` maps to `U`; the replacement is not recursively rewritten by the same operation. Callers that
compose an enclosing instance with a callee's substitution must explicitly apply the caller mapping
to each replacement before constructing the callee mapping.

### Explicit generic arguments

Explicit arguments fix the named generic declaration's type parameters in declaration order. Their
count must match exactly. The compiler does not truncate extras, fill missing entries from inference,
or silently switch to a same-name nongeneric target because an explicit generic candidate failed.

Explicit arguments still undergo ordinary checks. A type argument must belong to the active type
workspace, satisfy every applicable constraint, produce a valid substituted signature, and admit each
actual argument under its required passing mode. Writing `<String>` does not turn an `int` actual into
a `String` value.

If the explicit candidate is rejected but another source overload remains independently applicable,
normal overload rules determine whether that other candidate can win. That is different from using the
failed candidate's explicit type arguments for another declaration.

### Expected-result evidence

An enclosing type context can solve a generic parameter that appears in the selected result. For
example:

```sol
module docs.generic_fir_specialization
func <T> construct(): T

func makeText(): String {
    val text: String = construct()
    return text
}
```

The local annotation supplies `T = String` for this call. The evidence is scoped to this expression.
An unrelated function's return annotation, an adjacent declaration, or a later use does not backfill
the call.

Expected-result evidence cannot erase an input mismatch. If the call explicitly selects `T = String`
but supplies an `int`, the result context does not convert or rewrite that argument. The candidate
still needs an allowed conversion for every mapped actual.

Expected type also does not imply full bidirectional analysis of arbitrary nested expressions. The
current body binder supports selected expected-result paths, but not every syntax form or every
recursive contextual lambda. Unsupported context propagation must stay deferred when it could change
the source winner.

## Constraint checking

Constraints filter candidates after a substitution is formed. Inline bounds, `where` bounds, and
multiple bounds are conjunctive. Each bound is checked against the concrete type argument after the
candidate's own substitution has been assembled.

An unsatisfied constraint rejects that candidate. An indeterminate proof is not treated as success.
Where an incomplete candidate could change which overload wins, the call must not be reported as a
complete unique source selection. Constraints are not a final preference that ranks one valid
candidate above another; they decide applicability before conversion-vector ranking.

The source analysis layer has constraint and constructed-base queries. The FIR verifier repeats the
constraint check for the concrete function instance, so a modified FIR record cannot claim an
unsupported type argument. That verifier step does not implement generic class layout, interface
boxing, or constrained virtual dispatch.

The current ABI gate accepts only a subset of closed scalar, `String`, and one-level array types.
Being a legal source type argument does not imply that its representation is admitted by the native
backend. `half`, `quad`, ref-like values, nullable values, arbitrary nominal types, and nested arrays
remain outside the initial generic native slice.

## Conversion vectors

Applicable candidates are compared per mapped source argument. The current direct-call rank model
orders identity ahead of intrinsic reference conversion ahead of boxing. Numeric literal candidates
also retain representability and default-type information; those cases are resolved by the ordinary
literal rules before final selection.

Let `A` and `B` be two candidates with rank vectors of equal length. `A` dominates `B` only if every
component of `A` is no worse than the corresponding component of `B`, and at least one component is
strictly better. Formally:

| Condition | Result |
| --- | --- |
| `A = [0, 0]`, `B = [1, 0]` | `A` is strictly better |
| `A = [0, 1]`, `B = [1, 0]` | Incomparable; neither dominates |
| `A = [1, 1]`, `B = [1, 1]` | Equal conversion vector; later tie rules apply |
| `A = [2, 0]`, `B = [1, 1]` | Incomparable; do not sum ranks |

The binder does not add the components into a scalar score. Summing could incorrectly make a strong
match on one argument cancel a worse match on another. It also does not use declaration order as a
tie break.

### Final tie rules

Only after conversion vectors are equal does the binder apply these later criteria:

1. A candidate without vararg expansion is preferred to one using vararg expansion.
2. A candidate requiring fewer defaulted parameters is preferred.
3. An otherwise equal nongeneric candidate is preferred over a generic candidate.
4. If candidates remain equal or incomparable, the result is ambiguous.

The nongeneric preference is deliberately last. A generic candidate with a strictly better
conversion vector can beat a nongeneric candidate. A nongeneric candidate cannot win solely because
its declaration appears earlier. Vararg and default use are not inference evidence; they are mapping
and tie facts after applicability.

The exact implementation is in `BodyBindingBuilder.Calls.cs` for body analysis. The separate direct
call analysis has its own candidate model; do not assume its result is a verified FIR proof.
Both binders consume the shared argument map: `UsesVararg` is true when a supplied source argument
maps to the vararg parameter. An omitted empty pack is recorded separately in
`EmptyVarargParameters` and does not count as vararg expansion for ranking. Supplying an element,
spread, or named array to the vararg parameter does count as vararg use. This is source ranking
metadata; it does not imply that the selected call is supported by FIR or native lowering.

## Ambiguous and deferred outcomes

An ambiguity means the source binder completed enough analysis to find multiple equally best or
incomparable candidates. It is not an invitation to choose the first candidate in a dictionary or
declaration list.

A deferred result means at least one relevant proof is missing. Examples include unsupported user
conversion search, contextual lambda body analysis, incomplete expected-type propagation, or a
resource budget that prevents a candidate from being fully compared. If that candidate might be
better than the current best, the compiler cannot certify a winner.

The distinction matters to later phases:

| Source outcome | FIR action |
| --- | --- |
| Unique, fully bound, native-admitted winner | Create or reuse the closed FIR target instance |
| Unique source winner, unsupported native body | Report the native-subset boundary; do not select a worse candidate |
| Ambiguous source group | Produce no selected target and no successful call FIR |
| Deferred candidate that can affect ranking | Do not certify a source winner for native emission |
| No applicable candidate | Preserve the source overload diagnostic |

The generic source analyzer can be more capable than the current native subset. This is intentional:
source semantics must not change merely because one target cannot lower a construct.

## Body support after the winner is fixed

The initial FIR path supports only module-level generic free functions with complete closed
arguments and admitted native signature types. `FirBuilder` substitutes type-bearing facts into an
ordinary monomorphic `FirFunction`. It composes caller substitutions into nested generic calls,
reuses exact source-plus-type-argument keys, and bounds instance creation.

The open-template analysis defers operations whose applicability depends on an unresolved type
parameter. When a closed generic instance is requested, `BodyAnalysis` creates a fresh source binder
under that instance's substitution and reruns body analysis. This rebind resolves primitive
operators, supported direct calls, overload candidates, and their flow/effect consequences for the
closed types. A generic identity body is a small admitted shape; a generic `value + value` body can
be admitted for `int`, but its source-selected operation and concrete FIR representation still have
to be checked.

This distinction is essential when overloads interact with backend support. Suppose a generic source
candidate is an exact match, while a same-name `Object` overload requires a reference conversion. If
the generic body later hits `SOL2400`, the compiler reports that native limitation. It must not
retry source ranking and silently produce behavior from the weaker `Object` overload.

Per-instance source rebinding is implemented for the closed body-analysis path, but the set of
operations and representations accepted by FIR remains bounded. It is not complete generic body
checking: generic owner/member layout, virtual dispatch, and other unimplemented FIR shapes may
still be rejected after source selection. `FirVerifier` independently checks closed types, call
identities, argument mappings, effects, and flow invariants; it does not use backend rejection to
rerank source candidates.

### Admission table

The boundary is determined from the selected declaration and substituted body facts, not from a
second overload search. The table gives representative cases:

| Selected source body | Closed type | FIR result | Reason |
| --- | --- | --- | --- |
| `id<T>(value: T) = value` | `int` | Admitted | A name read and a concrete return type are verified |
| `id<T>(value: T) = value` | `String` | Admitted | The existing String representation is supported |
| `id<T>(value: T) = value` | `[]int` | Admitted when signature shape is supported | One-level array references use the existing managed-array path |
| `id<T>(value: T) = value` | `half` | Rejected | No native scalar lowering is defined for `half` |
| `id<T>(value: T) = value` | `[][]int` | Rejected | Nested array layout is outside this verifier subset |
| `id<T>(value: T) = value` | arbitrary class | Rejected | General nominal object layout is not admitted |
| `get<T>(value: T) = value.member` | a type satisfying a member bound | Rejected here | Concrete member rebinding is not performed per instance |
| `sum<T>(value: T) = value + value` | `int` | Admitted | Closed source rebinding resolves the built-in `int` addition; the concrete body is within the tested FIR subset |
| `add<T>(left, right: T) = left + right` with `Number` overloads for `Number` and `Object` | `Number` | Rejected after source selection | Closed source rebinding ranks `Number + Number` over `Number + Object`; FIR rejects the nominal body with `SOL2400` and does not retry a weaker overload |
| `forward<T>(value: T) = id(value)` | `String` | Admitted for supported direct generic calls | Caller substitution closes the callee arguments |
| generic member on `Box<T>` | any | Rejected | Generic owners and receiver substitution are not in this slice |
| generic function value | a uniquely inferred closed signature | Admitted for supported free-function and stateless-object targets | FIR records the selected specialization and verifies the exact indirect-call ABI; missing evidence is deferred |

The line between source rebinding and FIR admission is intentional. A closed instance can change
operator and overload applicability; source analysis reruns those selections under the substitution.
The concrete verifier still admits only represented operation and ABI shapes. A source-valid winner
that fails this gate remains the winner and produces a backend diagnostic.

The `forward<T>` row illustrates a direct generic callee with an explicit closed call target after
outer substitution. Its declaration and concrete argument vector can be represented as another
`FirFunction` instance; the target ID and type-argument vector are checked independently. Generic
operator bodies also undergo closed source lookup and ranking. A user-defined operator whose body or
nominal ABI is not represented by FIR is rejected after that source winner is fixed.

### Source-selected, then backend-rejected

Consider a group containing an exact generic candidate constrained by an interface and a wider
`Object` overload. If the actual type satisfies the interface constraint, source ranking may select
the exact generic candidate. If that body uses an interface member and requires concrete receiver
rebinding, native FIR can reject the selected body. The source winner remains the generic candidate.

The correct sequence is:

1. Determine that the generic candidate is applicable from source evidence and constraints.
2. Compare its conversion vector with the `Object` candidate.
3. Preserve the generic declaration identity as the unique source winner.
4. Attempt bounded FIR admission of that winner.
5. Report the native body boundary if the body depends on an operation that is not proven for the
   concrete substitution.
6. Stop compilation for that artifact; do not substitute the `Object` implementation.

The incorrect sequence would inspect the native function table first, drop the generic candidate
because it is not currently lowerable, then select the `Object` overload. That makes semantics depend
on build mode and makes a future backend extension change the result of existing source code.

This rule also applies when the generic candidate would require more machine code, has a cold call
site, or is not present in a cache. Code size, optimization cost, cache hits, and symbol availability
are not source conversion ranks.

### Candidate isolation in practice

Candidate-local state includes more than the inferred type vector. A complete candidate can retain:

- its explicit type arguments and unresolved parameter identities;
- each mapped actual-to-formal relation and the evidence contributed by that pair;
- literal spelling, numeric representability, and selected contextual target;
- any lower-bound join or constructed-base projection used by inference;
- contextual lambda parameter/result/effect facts;
- the candidate's substituted constraints and effect requirements;
- the argument conversion-rank vector;
- the exact rejection or deferral reason, with the relevant source origin.

Suppose two candidates take callbacks whose generic results differ. The first candidate's contextual
lambda body can infer one result type, but the second candidate must start from the pre-candidate
state. A lambda body error discovered only under one candidate cannot leave a stale type on the
shared expression and make another candidate appear to accept it. The binder snapshots or otherwise
isolates these changes, records the chosen mapping, then binds the final selected expression under
the winning formal signature.

The same isolation rule applies to arrays. If one overload expects `[]String` and another expects
`[]Object`, checking an array literal against one target cannot permanently widen its elements before
the other candidate is considered. Each candidate sees a contextual view; the final selected view is
the only one allowed to populate the call binding.

Constraint checks are also candidate-local. A bound failure removes that candidate. It must not erase
type evidence needed by another candidate with a different generic parameter owner. An indeterminate
constraint is retained as deferred when it could affect the selected overload.

## Worked rank matrix

The following table uses the rank names from the source call contract. It describes complete source
applicability; native body admission is a later column and never changes the winner.

| Actual argument types | Candidate A | Candidate B | Source result |
| --- | --- | --- | --- |
| `String` | `(String)` identity | `(<T> T)` identity | Equal vector; nongeneric tie rule selects A |
| `short` | `(int)` not an implicit widening candidate | `(<T> T)` identity with `T=short` | B if the generic signature is otherwise supported |
| `String` | `(Object)` reference | `(<T> T)` identity with `T=String` | B strictly dominates A |
| `String, String` | `(String,Object)` `[0,1]` | `(Object,String)` `[1,0]` | Incomparable; ambiguous |
| `String, String` | `(String,Object)` `[0,1]` | `(Object,Object)` `[1,1]` | First candidate dominates second |
| `String, Object` | `(String,Object)` `[0,0]` | `(Object,Object)` `[1,0]` | First candidate dominates second |
| `String, String` | fixed arity `[0,0]` | vararg candidate `[0,0]` | Fixed arity wins after equal vector |
| `String` with one omitted optional | one default | no default | Candidate with no omitted default wins after equal vector |
| `String` | nongeneric identity | generic identity | Nongeneric wins only after all earlier tie rules |
| `int` to `ref int` | exact mutable storage | `ref long` | Exact ref candidate; no widening |
| `String` to `ref String` | immutable local | mutable local | Immutable storage rejects the candidate |
| `String` under result context `T=String` | generic result context | unrelated Object result | The expected context participates only in the enclosing call |
| untyped `construct<T>()` | no input and no expected result | same | Deferred or rejected for missing evidence, not `T=Object` |

Rank examples are compact descriptions, not literal internal numeric ranks. The call binder stores its
own rank values and compares them by position. A human-readable report should print the rank kind and
source mapping, not expose an unstable enum ordinal as part of the language specification.

### Why ranks are vectors

For `f(String,Object)` and `g(Object,String)`, passing `(String,String)` gives `f=[identity,reference]`
and `g=[reference,identity]`. Neither candidate is best on every argument. A summed score would
produce a tie and might appear to justify an arbitrary tie-break; a maximum score might also conceal
which input is worse. The language rule is the partial order: each candidate must be no worse at every
position and strictly better somewhere to dominate.

Argument positions are derived from the preserved source argument sequence, while each rank is
associated with its selected formal mapping. Named arguments change which formal receives a source
argument; they do not reorder source evaluation. A diagnostic may show both source and parameter
indexes so reviewers can see which conversion created a vector component.

If a candidate omits a default, that fact is recorded separately from the conversion vector. If a
candidate expands a vararg, that is also separate. These tie rules must not be collapsed into an
opaque score because diagnostics need to explain whether a conversion, default, or vararg changed the
winner.

## Evidence and ranking matrix

The table separates evidence from ranking. Finding evidence for a generic argument does not mean the
candidate outranks another candidate.

| Source form | Inference role | Ranking role | Native note |
| --- | --- | --- | --- |
| `id(typedString)` | Input type can infer `T=String` | Argument conversion rank | Scalar/reference ABI must be admitted |
| `id<String>(value)` | Explicit argument fixes `T` | Conversion of `value` is still ranked | Does not skip body admission |
| `val x: String = create()` | Expected result can infer `T=String` | No argument-rank component for result | Only the supplied context is used |
| `fill(ref value)` | Exact storage can infer `T` | Exact mode/type required | Ref/out native storage must be supported |
| `fill(out var value)` | Target signature supplies local type after selection | Cannot independently rank an open type | Local definition/flow proof is separate |
| `use({ expression })` | Supported callback analysis can contribute evidence | Callback conversion/rank follows target type | Closure lowering remains limited |
| `first([values])` | Array literal/context may contribute evidence | Element conversions are candidate-specific | Array ABI remains one-level only |
| omitted `value = default` | No evidence | Default count is a later tie rule | User default lowering is not admitted |
| empty `vararg` | No arbitrary element type | Vararg expansion is a later tie rule | User vararg lowering is not admitted |
| `T: Named` | Constraint can prove candidate applicability | Not a conversion preference | Member use on `T` still needs rebinding |

The source analysis may expose a richer set of candidate facts than the current FIR backend accepts.
That is why the binding's selected target, ordered type arguments, mapped arguments, defaults, and
source origins are retained instead of reconstructing a call from its display string later.

### Explicit arguments versus evidence

An explicit argument takes the evidence-collection branch for that parameter: the binder does not
replace the user's written type with a different inferred type. It still compares the actual value
against the substituted parameter. For a function with two type parameters, one explicit parameter
does not silently make the other disappear. The source syntax's exact arity contract determines
whether partial explicit lists are accepted; under this contract, the list must be complete.

The explicit vector is canonicalized by declaration ordinal, not lexical presentation from another
owner. If both an owner and method declare a parameter named `T`, the source binder must associate
each written occurrence with the correct declaration. The first native slice rejects generic owners,
but the source overload model retains the distinction for future member specialization.

### Constraints versus conversions

A conversion can make an actual acceptable for a formal. A constraint can make a type argument valid
for a generic declaration. They answer different questions. An `Object` constraint is not a request
to box an integer and then call the generic body; a nominal interface constraint does not itself
prove that native interface dispatch or object layout is available.

The order is therefore: map; infer; validate bounds; verify passing modes and conversions; then rank
the complete applicable candidates. If a constraint is unsatisfied, the candidate's rank vector is
irrelevant. If the constraint is indeterminate, the candidate must not be counted as a successful
candidate merely because its conversion ranks look good.


## Selected target identity in FIR

For each generic `FirCall`, the verifier compares:

- the original call-site source origin;
- the `BodyCallBinding.Target` declaration identity;
- the call's ordered type arguments after applying the caller's substitution;
- the target `FirFunction`'s ordered specialization arguments;
- the source-binding parameter and result types after substitution;
- the target function's concrete parameter and result signature;
- the source argument indexes, formal parameter indexes, modes, and expression origins;
- the target's substituted effect set.

Changing a call to another same-name overload cannot pass solely because the name matches. Changing
`FirCall.TypeArguments` while preserving its call span cannot pass if the binding or target instance
uses another vector. Changing the function's specialization vector is also checked against source
constraints and the substituted declaration signature.

`FirVerifier` mints the `VerifiedFirProgram` capability only if all functions and call edges pass.
The LLVM emitter accepts that capability and emits calls by function ID. It does not repeat source
overload lookup, inspect display names, or parse textual LLVM source.

## Review traces

These traces are intentionally phase-by-phase. They make it possible to review a bug report without
conflating an inference issue with a native backend limitation.

### Exact generic versus concrete overload

Given `func <T> choose(value: T)` and `func choose(value: String)`, the call `choose(text)` where
`text: String` proceeds as follows:

| Step | Generic candidate | Concrete candidate |
| --- | --- | --- |
| Lookup | Included | Included |
| Mapping | `value` maps to parameter 0 | `value` maps to parameter 0 |
| Evidence | Input evidence fixes `T=String` | No generic evidence required |
| Constraint | Empty or satisfied constraint list | Not applicable |
| Conversion | Identity | Identity |
| Later tie | Generic | Nongeneric preference wins |
| Source target | Not selected | `choose(String)` |
| FIR consequence | No generic instance for this call | Ordinary function ID |

The result does not depend on which declaration occurs first in the source file. Reordering these two
declarations must leave the selected source identity unchanged.

### Generic exact candidate versus `Object`

Given `func <T> choose(value: T)` and `func choose(value: Object)`, a `String` actual produces an
identity conversion for the generic candidate and an intrinsic reference conversion for the Object
candidate. The generic candidate dominates componentwise. The final nongeneric tie rule is never
reached because the rank vectors are not equal.

If the generic declaration is later rejected by the FIR body gate, compilation fails at that boundary.
It is incorrect to report the `Object` function as selected after the source binder has already found
the generic exact winner. Source-call diagnostics and native-admission diagnostics belong to different
stages and should retain separate source spans.

### Two-argument incomparable candidates

Given `choose(String,Object)` and `choose(Object,String)`, a call with two strings has rank vectors
`[identity,reference]` and `[reference,identity]`. The result is ambiguity even if one implementation
would be cheaper or more convenient to lower. No total order can be created by summing the two
vectors or by preferring a generic candidate.

The inspection output should include both candidate declaration identities and both mapped vectors.
If the output lists only the shared name `choose`, it is insufficient to debug an overload result.

## Failure triage for maintainers

Use the owning phase to decide which source fact to inspect:

| Symptom | Inspect first | Do not infer |
| --- | --- | --- |
| No candidate listed | Visible symbol set and import resolution | That a hidden overload was considered |
| Candidate rejected before ranks | Argument mapping, explicit arity, constraints, passing mode | That its rank was merely worse |
| Candidate deferred | Missing body/lambda/conversion evidence and budget | That it was proven inapplicable |
| Unexpected winner | Per-argument rank vectors and tie criteria | That declaration order is meaningful |
| FIR says unsupported | Selected source target and unsupported body node | That another overload should be selected |
| Verifier rejects target ID | Call source origin and target declaration identity | That same display name is enough |
| LLVM rejects a module | Verified FIR signature and LLVM bridge operation | That source overload resolution changed |

A useful diagnostic or internal trace prints the call span, selected declaration identity, ordered
generic arguments, mapped actual/formal pairs, conversion kinds, default and vararg facts, and the
phase that rejected the candidate. It should avoid printing an absolute host path or treating
workspace-local type IDs as stable public names.

If the native boundary reports `SOL2400`, the first debugging question is whether the source binder
selected the intended declaration. If the answer is yes, inspect the generic body surface and
concrete ABI admission. Do not edit rank ordering to hide a body-lowering gap.

For `SOL2403`, inspect the instance count and nested type depth. A resource result means the
specialization graph was not completed; it is not evidence that an overload lost. For `SOL2402`,
inspect the FIR record against the source call binding. The verifier is expected to reject modified
or inconsistent FIR instead of asking source overload resolution to choose again.

## Diagnostics and failure ownership

Diagnostics identify the phase that owns the failure:

| Phase | Example class | Meaning |
| --- | --- | --- |
| Lookup | unresolved direct call | No visible source target exists |
| Applicability | no applicable candidate | Every visible candidate has a proven mismatch |
| Ranking | ambiguous candidate set | No unique best source overload exists |
| Source analysis | deferred or bounded inference | A required proof is incomplete |
| FIR admission | unsupported native shape | Source winner exists but cannot enter this native subset |
| Specialization construction | `SOL2403` | The bounded instance/type-depth budget is exceeded |
| FIR verifier | `SOL2402` | Constructed FIR violates an independent verifier invariant |
| Backend | LLVM or linker failure | Source selection and verified FIR remain unchanged |

`SOL2400` denotes an unsupported native construct. It is not a source overload rejection. `SOL2403`
must not be reported as successful partial specialization. A verifier failure does not cause a second
overload-selection attempt. Cancellation is not converted into a syntax diagnostic or a fallback
candidate.

## Resource and determinism boundaries

The initial FIR builder caps generated function IDs at 4,096 per compilation. It also limits the
structural type argument depth to 128. These are separate from generic inference and type interning
budgets. The full normative resource appendix proposes additional accounting for body nodes, roots,
and future caches; those limits are not all implemented by this slice.

The worklist is compilation-local. Ordinary functions receive IDs in source enumeration order;
generic instances receive IDs when selected calls are visited and the deterministic queue is
drained. Type IDs are local to the current analysis workspace. They are useful for this compilation's
instance map, not stable cross-build ABI identifiers.

An instance cache hit is valid only when the source declaration and complete ordered type vector
match. Name-only caches are unsafe for overloads. Persistent, cross-version caches and a stable
generic artifact ABI are not part of this implementation.

## Validation status

The managed FIR project currently builds with zero compiler warnings and zero errors in the
development configuration. This statement is limited to compilation of that project. It does not
claim that the overload matrix has run, that all rows parse, that native AOT execution passed, or that
ORC output matched AOT output.

Before a release claim, the conformance work needs separate source-selection, FIR-tampering, LLVM
lowering, native-runtime, AOT, and ORC evidence. Each result should state which phase it covers.
Parsing a `.sol` fixture is not a proof of its `expect` annotation. A successful FIR verifier run is
not a runtime execution result. A single supported `identity<T>` call does not generalize to all
overloads, type arguments, or native ABI forms.

## Maintainer checklist

Before changing candidate ranking or FIR specialization, confirm:

- [ ] Every candidate owns its substitution, evidence, and speculative lambda state.
- [ ] Explicit type argument arity and order are exact.
- [ ] Expected-result evidence is attached only to the enclosing call expression.
- [ ] Ref/out evidence uses exact writable storage and does not apply conversions.
- [ ] Conversion vectors are compared componentwise, not summed.
- [ ] Vararg use, default count, and generic preference are applied only after conversion ties.
- [ ] Declaration order and hash enumeration do not choose a winner.
- [ ] Deferred candidates that may change the winner do not disappear as rejected candidates.
- [ ] Native unsupported-body diagnostics do not cause source overload fallback.
- [ ] The selected source declaration, argument map, and ordered type vector reach FIR.
- [ ] The verifier checks target instance, concrete signature, constraints, effects, and origins.
- [ ] LLVM lowering consumes only verified FIR IDs and the fixed LLVM 23.1.3 C++ API path.
- [ ] Test, CI, AOT, and ORC evidence is reported separately and not inferred from a managed build.

This checklist is a change review aid. It is not evidence that the listed obligations are all covered
by the current native specialization slice.
