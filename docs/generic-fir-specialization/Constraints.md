<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->

# Generic constraints from source binding to verified FIR

## 1. Purpose

This guide describes how generic constraints participate in a selected free-function call and in
the first closed native specialization slice. It is for language implementers and maintainers who
need to distinguish a source-level proof from native representation admission.

The normative contract is in [`GenericFirSpecialization.sol`](../../spec/GenericFirSpecialization.sol).
The source scenarios are in
[`GenericFirConstraints.sol`](../../spec/examples/semantics/GenericFirConstraints.sol).
The language-wide type and constraint declarations are summarized by
[`Generics.sol`](../../spec/Generics.sol).

This guide does not claim that the entire generic feature is implemented. Source binding supports
more declaration and constraint forms than verified FIR can currently emit. The native slice
accepts selected module-level generic free functions only when all arguments are closed and the
concrete types are within the FIR ABI subset.

## 2. Current implementation status

The current compiler performs these steps for an admitted call:

1. Source declaration analysis assigns every generic parameter an owner and ordinal.
2. Candidate binding maps arguments, gathers permitted evidence, and checks the selected candidate's
   inline and `where` constraints.
3. `BodyCallBinding` stores the selected target's ordered concrete type arguments.
4. `FirBuilder` substitutes the caller instance into nested call type arguments and creates a
   compilation-local monomorphic instance for an eligible module-level generic function.
5. `FirVerifier` checks that the instance has the right argument count, owns closed native types,
   satisfies declaration constraints, and has a substituted signature consistent with the source.

This first FIR slice substitutes already-bound body facts. It does not rerun full overload and body
analysis under every concrete argument. Constraint rechecking in FIR protects the instance boundary;
it does not turn a declaration-level proof into a full per-instance semantic rebind.

The current native type predicate is narrower than the source type system. It admits the supported
primitive scalar set, `String`, `()`, and one-level arrays of supported scalar or `String` element
types. It rejects ref-like types and nominal user types as generic type arguments. A valid nominal
constraint does not add a native layout for its implementing type.

Generic owners, generic methods, generic constructors, generic property accessors, generic operators,
open type arguments, and cross-container generic exports are not admitted by this FIR slice. A
module-level generic function may be used as a monomorphic value when its expected function type
closes every parameter and the resulting body and signature are supported. A source candidate may
still be valid while native emission reports that its selected construct is outside the subset.

## 3. Constraint forms

A function can put one bound beside a type parameter:

```sol
module docs.generic_fir_specialization
func <T: Named> labelOf(value: T): String = value.name()
```

The `where` clause can declare one or several bounds:

```sol
module docs.generic_fir_specialization
func <T> labelOf(value: T): String where T: Named = value.name()
```

The inline bound and every matching `where` bound contribute to the same requirement set:

```sol
module docs.generic_fir_specialization
func <T: Named> stableLabel(value: T): String
    where T: Stable, T: Comparable<T> = value.name()
```

A single type parameter may have several nominal bounds. All of them must hold. They are conjunctive
requirements, not alternatives. The compiler cannot select one bound because it is easier to prove
or because its member happens to match a use.

Bounds may refer to constructed generic types when their own declaration identity and arguments can
be proven. `T: Comparable<T>` is a recursive type expression, not a request for runtime recursion.
The constraint checker substitutes the candidate argument for the exact parameter identity and
checks the resulting constructed type using the type relation rules.

`where T: allows ref struct` is an anti-constraint. It permits an otherwise valid ref-like argument
for a declaration whose body satisfies the lifetime and storage rules. It is not a nominal interface,
does not provide members, and does not make the argument heap-safe.

## 4. Declaration identity

A type parameter is identified by its declaration owner plus its zero-based ordinal. Its spelling is
only a display name. These parameters are different even though both are written `T`:

```sol
module docs.generic_fir_specialization
func <T> first(value: T): T = value
func <T> second(value: T): T = value
```

Substitution for `first` must never replace `second`'s parameter. The parameter order in a declaration
is the order used for explicit arguments, stored call metadata, instance keys, and verifier checks.

A partial declaration shares parameter identities only when the source declaration merge has already
proved that the parts form one declaration and that their parameter lists agree. A same-spelled type
parameter from an unrelated function, module, or partial declaration is not interchangeable.

The FIR instance key uses source declaration identity plus its ordered closed arguments. Constraint
text, parameter display names, source path spelling, and hash enumeration order are not substitutes
for canonical identity. Numeric type IDs in FIR are local to one type workspace and one compilation.

No current on-disk generic instance cache or stable cross-build identity is implied. The closed-key
rules are compilation-local and must not be persisted as if they were a library ABI.

## 5. Proving nominal bounds

For every candidate, the checker substitutes the candidate's ordered arguments into every inline and
`where` bound. A class argument satisfies a class or interface bound only when the source type
relation establishes the required constructed base or interface path.

The proof follows the declared inheritance graph. It cannot infer conformance from a method that
happens to have the required spelling. Structural resemblance is not nominal membership. A method
named `name` does not prove that the receiver implements `Named`.

Every bound contributes independently:

```sol
module docs.generic_fir_specialization
interface Named { func name(): String }
interface Identified { func id(): int }
func <T> inspect(value: T): String where T: Named, T: Identified = value.name()
```

An argument that implements `Named` but not `Identified` is rejected. An argument that implements
both satisfies the nominal part of the constraint. Its type may still be outside the native FIR
subset, and its body may still use an operation that the native verifier rejects.

Constructed bounds compare their type arguments according to the language's generic variance rules.
The current generic application model is invariant unless the spec explicitly grants variance for a
particular declaration. A `Container<Child>` is not silently treated as `Container<Base>` merely
because `Child` inherits `Base`.

Multiple inheritance paths are all considered. Identical constructed paths may denote the same
requirement. Conflicting constructed arguments do not collapse to one candidate by choosing the
first path. Path identity and member lookup are specified separately in the member-resolution
contracts.

## 6. Bound substitution

Constraint substitution is simultaneous. Given declaration-owned parameters `T` and `U`, the mapping
`T -> U, U -> int` replaces an occurrence of the original `T` with `U`; it does not recursively
rewrite the replacement a second time to `int`.

This rule prevents accidental capture and makes explicit argument order unambiguous. If a language
operation needs repeated substitution, it must state that operation as an explicit sequence of
substitutions with separate identities and checked limits.

Substitution traverses all nested type structure relevant to the source type: generic arguments,
nullable wrappers, array elements, tuple elements, function parameters, results, passing modes, and
declared effects. Substitution preserves canonical identity and nullable normalization.

The source analysis workspace owns the original generic parameter and its type graph. `TypeSpecialization`
owns constructed types created during checking. A foreign workspace type cannot be used as a bound
proof or FIR argument merely because its display name is equal.

The checked substitution is bounded and cancellation-aware. Resource exhaustion is not converted to
an empty substitution, an `Object` fallback, or a successful partial instance. The source-analysis
budget and the FIR generated-function budget are independent limits.

## 7. `Object` constraints and boxing

The `std.core.Object` bound has a reference-type rule. A class type with a valid implicit base
conversion can satisfy it. A primitive value cannot satisfy it by introducing boxing solely to make
the generic constraint pass.

This restriction is observable in source selection and native eligibility. A candidate with
`T: Object` is not applicable to `int` merely because some runtime could box an integer. An
unconstrained generic function `id<T>(int)` may infer `T = int`; that does not mean `int` satisfies
every nominal bound.

The verifier does not synthesize a box for a value argument or silently replace the selected generic
instance with a different overload. If boxing is added to Sol in the future, it needs a source
conversion rule, object layout, GC rooting, allocation and exception behavior, and a separate native
proof.

## 8. `allows ref struct`

The anti-constraint is only permission for a ref-like type to be considered as an argument. The body
must still obey ref-like storage, return, capture, field, array, and escape restrictions.

For example, permission to accept a ref-like argument does not mean this operation is safe:

```sol
module docs.generic_fir_specialization
func <T> capture(value: T): () where T: allows ref struct {
    val callback = { value }
}
```

The closure may outlive the argument's lifetime, so body analysis must reject the escape. Similarly,
putting a ref-like value in a managed array, ordinary class field, or long-lived heap object is not
made legal by the anti-constraint.

Anti-constraints do not cancel nominal bounds. If a ref-like type must also implement `Readable`,
both requirements must be proven. Implementing an interface does not make boxing a ref-like value
safe. Constrained dispatch and boxing remain different operations.

The first verified FIR slice rejects ref-like generic arguments even when the source declaration
uses `allows ref struct`. This is a native subset boundary. It is not a source-level rejection of
the anti-constraint or a statement that ref-like generics can never be implemented.

## 9. Candidate selection

Constraints filter candidates after the argument mapping and permitted inference evidence are
available. They do not preselect an overload by name. Candidate-local substitutions remain isolated:
one candidate's failed bound must not contaminate another candidate's evidence or diagnostics.

An explicit type argument fixes the named generic parameter, but it does not waive its bound. An
inferred type argument is checked by the same constraint rules. Expected-result evidence can help
determine the argument before the bound is checked; it cannot override a failed constraint.

If no candidate satisfies its bounds, the call is inapplicable and receives the ordinary source
diagnostic for candidate failure. If two equally ranked candidates remain applicable, the call is
ambiguous. Native code availability does not change that ranking.

If the source winner is valid but its body or concrete argument is unsupported by FIR, the native
stage reports that admission boundary. It does not rerun overload resolution and choose a worse
source candidate simply because that candidate was easier to lower.

An indeterminate constraint is not equivalent to satisfied. A deferred conversion or incomplete
constructed inheritance path may keep the candidate open; if it could change the result, the compiler
must not claim a complete selected instance.

## 10. Inference is not a constraint

Constraints describe requirements on a chosen argument. They are not general inference evidence.
The solver must first obtain a unique closed argument from the evidence kinds allowed by the
generic-inference spec, such as mapped arguments or an expected result.

An empty array does not become `[]Object` merely because `Object` is a convenient bound. An empty
vararg does not select an arbitrary instantiation. A default value is not an inference input unless
the language's argument-mapping contract explicitly says otherwise.

The `allows ref struct` anti-constraint is not an inference hint. It cannot cause the solver to
prefer a ref-like type. It only affects the admissibility of a concrete candidate argument.

Generic type parameters in a callback's parameter, result, or effect positions require the exact
candidate-local evidence rules defined by the callback and effect inference contracts. Constraints
cannot repair a callback shape mismatch or invent a missing checked effect.

## 11. Constraint checking in source analysis

The source binder owns overload choice and candidate validity. It checks the actual generic
declaration, not a similarly named method or a synthesized display signature.

The body-call binder preserves the selected generic argument vector because FIR needs to verify the
same choice later. Inferring type arguments again from the substituted signature is insufficient:
unused parameters may not appear in that signature, and two generic declarations may have identical
display types while remaining distinct by owner identity.

When the generic function body refers to a member through a constraint, source body analysis can use
the members guaranteed by those bounds. This is a source semantic capability. It does not prove that
the generic member call has a native lowering or that the concrete implementation can be dispatched
without a generic dictionary, vtable, or boxing operation.

Closed generic instances run a fresh `BodyAnalysis` under the concrete substitution before FIR is
built. This rebinds the operation and call forms represented by that analysis path, then the
independent verifier checks source correspondence and native support. It does not provide generic
member dispatch through bounds, owner layouts, or every operation in the language; those remain
outside the verified subset.

## 12. Constraint recheck at the FIR boundary

The source call's selected arguments are carried into the function instance in declaration order.
The verifier validates ownership, arity, closure, native support, and the generic declaration's
constraints before accepting the instance.

For each generic function instance, its specialization arguments must match the source declaration's
generic parameter count. A missing, extra, default/uninitialized, or open argument cannot be treated
as an empty list. Every argument must belong to the same `TypeSpecialization` workspace used by FIR.

The verifier builds a substitution from each exact function-owned parameter identity to its closed
argument. It compares the resulting source result, parameters, and declared effects to the FIR
function signature. A target call also has to match the type-argument vector recorded on the selected
source binding and the specialization metadata of the target function.

Constraint rechecking catches corrupted or mismatched instance metadata. It does not independently
prove that source binding selected the best overload; that proof is anchored through the immutable
source call binding and target declaration identity.

A verifier failure does not mint `VerifiedFirProgram`. The LLVM emitter accepts only that capability,
so a failed bound or mismatched instance cannot become a machine-code function through an alternate
path.

## 13. Constraint categories and native outcomes

| Source constraint outcome | Source call outcome | Native specialization outcome |
| --- | --- | --- |
| All nominal bounds satisfied | Candidate may remain applicable | Continue checking concrete type support and body shape |
| Any nominal bound unsatisfied | Candidate is rejected | No instance is reserved for that candidate |
| Constraint result indeterminate | Candidate remains deferred | No verified instance is emitted from incomplete evidence |
| `Object` bound on primitive | Candidate rejected; no implicit boxing | No instance |
| `allows ref struct` with an admitted ref-like source type | Candidate may be valid if body lifetime rules pass | Current FIR rejects the ref-like native argument |
| Valid bound on an unsupported user class | Source candidate may be valid | Current FIR rejects the nominal native type |
| Valid bound on `String` or supported scalar | Candidate may be valid | Continue through body and signature verification |
| Valid bound with unsupported member/operator body | Source function may be valid | Native gate rejects the operation or body shape |

This table separates three questions: whether a type satisfies a language constraint, whether a
source candidate is applicable, and whether a verified native implementation exists. The answers
must not be conflated in diagnostics or release claims.

## 14. Error and diagnostic ownership

Source overload and generic inference diagnostics describe source-level candidate resolution. They
should retain the call span and the failed declaration/parameter identity when available. An
unsupported native body is not a parser error and does not prove the source generic call was
ill-typed.

`SOL2400` identifies a construct outside the current verified native subset. `SOL2402` is emitted by
the independent verifier when a FIR invariant fails. `SOL2403` identifies the FIR specialization
resource limit. Those codes belong to different phases and must not be rewritten as one another.

A resource diagnostic must not claim that the generic constraint is false. Conversely, an ordinary
constraint failure must not be described as resource exhaustion. Cancellation is not a candidate
failure and cannot publish a partial specialized program.

Where a selected generic instance is rejected by the native ABI predicate, preserve the source call
and generic declaration origins. Avoid turning an unsupported argument into an unrelated “no
overload” diagnostic after source analysis already chose the candidate.

## 15. Nested generic calls

When a closed instance calls another generic function, the outer substitution is applied to the
nested call's ordered type arguments before a new key is computed. If `forward<T>` calls `identity<T>`,
the instance `forward<String>` targets `identity<String>` rather than retaining the caller's open `T`.

The callee's parameters are separate identities even when both declarations use the spelling `T`.
The mapping is positional by declaration ordinal after source binding has completed. The callee's
constraint check runs for the closed values passed from the caller.

Exact repeated keys are reused. Recursive calls to the same declaration with the same closed
arguments point back to that existing function ID. A growing type sequence must not collapse to the
same key merely because the function name is unchanged.

Current FIR reserves instances from source-ordered calls in ordinary bodies and processes a bounded
work queue for nested instances. Its queue limit is 4,096 generated functions per compilation, with
a 128-level structural type depth cap. The fuller normative generic contract describes additional
accounting and cancellation obligations not yet implemented.

Generic-owner and member substitutions are not currently admitted. A generic method's type
arguments are not enough by themselves: the declaring object's type arguments, receiver path,
virtual dispatch, and member identity would also have to be composed and independently checked.

## 16. ABI admission examples

The following examples distinguish source constraints from native type support:

```sol
module docs.generic_fir_specialization
func <T> identity(value: T): T = value
func <T: Object> referenceIdentity(value: T): T = value
```

`identity<int>(1)` and `identity<String>("x")` have closed arguments within the current native
type predicate. Their concrete bodies still pass through the FIR verifier.

`referenceIdentity<String>("x")` can satisfy the `Object` constraint and uses a supported String
representation. `referenceIdentity<int>(1)` cannot satisfy the bound by boxing.

A user class that implements `Named` may satisfy `T: Named` in source analysis. It remains outside
the generic native argument subset until the runtime object layout, field access, dispatch, and
managed-reference proofs for that class are admitted.

A ref struct that satisfies an interface and is passed to a function constrained by
`allows ref struct` may be valid source if its body respects lifetimes. FIR still rejects that native
argument because ref-like values have no supported slot and call ABI in this first slice.

## 17. Bounds that expose members

A bound can make member lookup well-defined for source analysis. For example:

```sol
module docs.generic_fir_specialization
interface Named { func name(): String }
func <T: Named> describe(value: T): String = value.name()
```

The source body can bind `name()` through the `Named` contract. The implementation must preserve the
selected bound member and constructed receiver identity; it cannot later search by method spelling.

Current verified FIR does not yet rebind this call for every concrete `T`, nor does it implement a
generic dictionary or interface dispatch path. The direct member operation therefore remains outside
the current generic FIR slice even when the actual argument is a concrete implementation of
`Named`.

This boundary is important for generics whose body contains arithmetic, equality, indexing, property
access, operators, or conversions selected from a constraint. A closed argument type may support a
native builtin operation, but source analysis must have selected that exact operation and FIR must
carry enough identity to verify it. A type substitution alone must not invent a new operator binding.

## 18. Overload interactions

An overload group may contain generic and nongeneric candidates. Constraint satisfaction is one
applicability check; it is not an overload preference. Once all applicable candidates are known, the
ordinary ranking rules select one winner or report ambiguity.

An unsupported body on a losing generic candidate does not invalidate a different unique winner.
The compiler should not specialize every candidate eagerly. It reserves a FIR instance only after
source selection has produced a unique candidate and complete type arguments.

If the selected generic candidate is later rejected by FIR, the compiler must not silently choose a
lower-ranked overload. This keeps native code generation from changing source semantics.

Two overloads with identical parameter display strings may still be distinct because their
declaration identities or generic parameter owners differ. Call metadata retains the exact selected
target rather than serializing only a name and reconstructing it later.

## 19. Partial declarations and owner constraints

Partial nominal declarations merge their type parameter lists, constraints, and owner identity under
the partial-declaration rules. A mismatch is a source declaration error before generic call binding.

The first native FIR slice does not specialize generic nominal owners. A method in `Box<T>` may need
both the owner substitution `Box<int>` and the method substitution `<U>`. Recording only `U = String`
would be incomplete; both identities, receiver construction, layout, and lookup route must match.

For this reason, the current builder rejects generic owner/member specialization even when a member
itself declares no type parameters. A nongeneric method on `Box<T>` still depends on the unresolved
owner parameter and cannot be emitted as an ordinary monomorphic body.

Partial source declarations do not provide a shortcut around this boundary. The canonical merged
type is still generic, and a method's inherited or declared constraints still require the owner
substitution to be present.

## 20. Interdependent constraints

Dependent bounds require substitution in declaration order without confusing display names. A
contract such as `T: Comparable<U>, U: Comparable<T>` checks two constructed relations over the
selected argument pair. Satisfying only one side is insufficient.

The source binder must not solve a cyclic dependency by assuming the missing bound. It may use the
declared type relation graph and finite solver rules. If the result is unresolved or indeterminate,
the candidate remains incomplete.

Bound checking and type inference are separate but coordinated. Inference may use input evidence to
produce the candidate vector; constraints then validate that vector. A constraint is not inverted
into arbitrary candidate types unless the generic inference contract explicitly defines that rule.

Concrete constructed arguments receive their own recursive constraint checks. A valid outer type
does not excuse an invalid nested argument, and a nested failure must be associated with the nested
parameter that failed.

## 21. `where` ordering and duplicates

Source spelling order does not change the logical conjunction of bounds. For deterministic
diagnostics, the implementation may retain declaration order for reporting, but it must not treat
the first valid bound as permission to skip the rest.

Duplicate identical bounds do not create additional runtime work or change overload ranking. A
conflicting bound remains a real failure. The canonical semantic representation should preserve the
declaration origin needed for diagnostics even if equivalent facts are interned.

Inline constraints and `where` constraints are normalized together only after the parser has
associated each bound with its exact parameter identity. A malformed clause cannot be attached to
another parameter based on a same-spelled name.

## 22. Nullable and structural arguments

The source type system can represent nullable, array, fixed-array, tuple, and function types, but a
source-valid constructed type does not automatically satisfy the native generic ABI predicate.

The current native verifier supports a single dynamic array layer only when its element type is one
of the admitted scalar or String types. It does not admit nested arrays, nullable native values,
ref-like elements, function values, or general tuple storage as generic type arguments.

Constraint matching is performed on the exact source constructed type. It does not erase nullability
or normalize a nested shape merely to match a bound. The native check follows the source substitution
and rejects shapes without a verified layout.

## 23. Exception and effect bounds

Generic exception types still have to derive from `std.core.Throwable`. If a function declares a
generic effect parameter, the selected substitution must preserve the declared effect order and
checked-versus-unchecked classification used by source analysis.

FIR verifies that substituted effect entries belong to its type workspace and satisfy the Throwable
root. Calls compare the selected source target's substituted effects with the target instance's
effect metadata. Checked effects remain subject to the caller's declared budget.

This does not implement native checked-exception unwinding, catch dispatch, or exceptional CFG edges.
A valid generic effect constraint must not be advertised as native exception support.

## 24. Resource and failure boundaries

Source specialization and FIR instance construction use separate resource limits. The native FIR
builder currently caps the generated function graph at 4,096 IDs per compilation and the structural
type depth at 128 levels. `SOL2403` identifies a FIR specialization resource failure.

Source-level type inference has its own graph, fact, and lambda-pass budgets. Those limits are not
interchangeable with the FIR graph cap. Reaching one budget must fail the owning phase without
emitting a partial program.

`SOL2400` reports an unsupported native construct. `SOL2402` reports an FIR verification invariant
failure. A false constraint is a source candidate failure. The compiler should retain these separate
ownership boundaries when reporting an error.

Cancellation stops source analysis, type substitution, FIR reservation, verifier work, or lowering
at the phase that observes it. It cannot publish a verified program whose specialization queue was
only partly processed.

## 25. Conformance records

Each source scenario in `GenericFirConstraints.sol` uses an `// expect:` annotation to state a
semantic outcome. The annotation distinguishes source applicability, selected ordered type
arguments, constraint status, and native FIR disposition.

`Source:Applicable; FIR:Rejected(unsupported ABI)` means the language call is valid but the current
native subset cannot represent its type. It is not equivalent to a source constraint failure.

`Source:Rejected(constraint)` means the candidate cannot be selected. No FIR instance should be
reserved for it. `Source:Deferred` means the evidence is incomplete; it cannot produce verified FIR.

`Source:Applicable; FIR:Eligible` is only the first-stage expectation. A test of an emitted executable
would additionally need to verify AOT/ORC output, runtime results, and artifact boundaries. The
fixture's normative rows do not imply those executions have occurred.

## 26. Maintainer review checklist

Before extending native generic constraints, confirm:

- Parameter identity is declaration plus ordinal, never spelling alone.
- Every inline and `where` bound is checked after candidate-local substitution.
- Nominal conformance follows constructed inheritance paths.
- No implicit boxing is introduced for `Object` constraints.
- `allows ref struct` remains separate from storage and lifetime validation.
- Unresolved constraint state cannot be treated as a successful candidate.
- Candidate ranking remains independent of backend support.
- Body call binding retains ordered type arguments even for unused parameters.
- Nested generic calls compose the current caller substitution.
- FIR rechecks closed arguments, constraints, and substituted signatures.
- Source origins remain attached to the generic declaration and each call site.
- Unsupported native shapes fail before `VerifiedFirProgram` is minted.
- `SOL2400`, `SOL2402`, and `SOL2403` remain phase-specific.
- No constraint proof is described as object layout, dispatch, boxing, or GC proof.
- Release notes distinguish the implemented slice from the full normative generic contract.

## 27. Future work

The next constraint-related implementation work should be ordered by dependency:

1. Rebind generic function bodies under each closed substitution rather than projecting declaration-
   level facts without re-evaluation.
2. Preserve selected member, operator, conversion, and effect identities in the substituted body.
3. Add independent positive and negative evidence for constructed constraints and dependent bounds.
4. Define owner-plus-method substitution for generic nominal members.
5. Define safe ref-like ABI and lifetime rules before admitting `allows ref struct` to native FIR.
6. Add object layout and dispatch proofs before using nominal user-defined types in native slots.
7. Extend serialization and artifact metadata only with versioned, source-anchored identities.

Each expansion requires a matching native verifier rule. Source support alone is not completion.

## 28. Concrete constraint decision sequence

For a single call, the implementation should preserve this phase order:

1. Parse the type-parameter list and attach each inline and `where` bound to one owner/ordinal.
2. Resolve the visible callable declarations and preserve their source identities.
3. Map arguments independently for each candidate, including names and passing modes.
4. Apply explicit type arguments as fixed facts before inference.
5. Solve permitted inference evidence without borrowing facts from another candidate.
6. If evidence is incomplete, preserve the deferred state instead of guessing a type.
7. Build one simultaneous substitution for the candidate's function parameters.
8. Substitute every inline and `where` bound under that same map.
9. Check all nominal bounds, nested arguments, object rules, and anti-constraints.
10. Verify conversions and exact ref/out requirements against the selected signature.
11. Rank only the fully applicable candidates using the ordinary source rules.
12. If one candidate wins, store its complete ordered type arguments in the body binding.
13. If none wins or several tie, report source resolution and produce no specialization request.
14. If the selected call is closed, let FIR evaluate native ABI eligibility separately.
15. Recheck the same declaration and ordered argument vector inside the FIR verifier.

An implementation must not move a target-specific failure ahead of source overload selection in a way
that changes which declaration is selected. The source language decides the callable before the
native backend decides whether it can emit that body.

The order also prevents one class of confusing diagnostic: the compiler should not say “constraint
failure” when all constraints passed but `FirVerifier.Supported` rejected the concrete type. The
source call is applicable in that case; native admission is the failing phase.

## 29. Minimal implementation evidence

When reviewing a new generic constraint feature, ask for evidence at each boundary rather than one
end-to-end success message:

- Parser evidence shows the inline or `where` syntax is represented with its original source span.
- Declaration evidence shows the exact generic parameter owner and ordinal.
- Type evidence shows constructed bounds use canonical type identities in one workspace.
- Candidate evidence shows losing and deferred candidates remain isolated.
- Constraint evidence distinguishes satisfied, unsatisfied, and indeterminate outcomes.
- Body evidence shows only bound members are visible through a parameter's constraints.
- FIR evidence shows the selected vector survives call binding without display-name reconstruction.
- Verifier evidence shows constraint and closed-signature checks use the source declaration identity.
- ABI evidence shows supported scalar/String forms lower through their existing representations.
- Runtime evidence shows array/String references retain the expected GC root behavior.
- Error evidence shows unsupported native types do not turn into source constraint errors.
- Serialization evidence shows function and call type arguments appear under FIR schema 7.
- Build evidence shows the managed FIR assembly compiles with the current SDK and analyzers.
- CI evidence shows the branch's mandatory workflow has exercised the changed paths.

The current branch's managed FIR build is evidence only for compilation of the managed layer. It does
not independently establish semantic outcomes, LLVM module verification, runtime behavior, AOT
imports, or ORC execution.

## 30. Source examples are not runtime demonstrations

The `.sol` scenarios record intended language behavior. A source example with
`Source:Applicable; FIR:Rejected` is deliberately useful even though it cannot currently produce a
native binary. It pins the distinction between the language contract and the present backend.

An annotation that says `FIR:Eligible` defines an expected admission result for that case. It is not
a claim that a test has run, the LLVM bridge has been loaded, or a Windows artifact has executed.
Those claims require separate recorded build, CI, and runtime evidence.

If an implementation change intentionally changes one scenario, update the annotation and the
corresponding language contract together. Do not edit the expected status only to make a failing
implementation appear correct. Keep source, native-subset, and runtime claims individually
reviewable.
