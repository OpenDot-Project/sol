<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Receiver and member resolution

This page describes the development `BodyAnalysis` receiver lookup. It does not describe a complete
type checker or executable backend. The normative source rules are in
[MemberResolution.sol](../spec/MemberResolution.sol); the public implementation boundary is in
[body analysis](body-analysis.md).

The additional contracts cover [nullable receiver flow](../spec/MemberResolution.NullableFlow.sol),
[partial type identity](../spec/MemberResolution.PartialIdentity.sol),
[path ownership](../spec/MemberResolution.PathOwnership.sol),
[path-sensitive hiding](../spec/MemberResolution.PathSensitiveHiding.sol),
[protected calls](../spec/MemberResolution.ProtectedCalls.sol), and
[accessor candidate behavior](../spec/MemberResolution.AccessorCandidates.sol),
[pathwise reduction](../spec/MemberResolution.PathwiseReduction.sol),
[protected receiver rules](../spec/MemberResolution.ProtectedReceiverMatrix.sol),
[accessor operation effects](../spec/MemberResolution.AccessorOperationEffects.sol), and
[visibility recovery](../spec/MemberResolution.CandidateVisibility.sol), including behaviors that
remain planned for complete flow analysis and cross-file symbol validation.

## What the analysis records

`BodyAnalysis.Create(sourceAnalysis, cancellationToken)` uses the immutable source set, its declared
symbols, written type identities, and lexical scopes to inspect expression bodies. For a receiver
member, the binder resolves the receiver expression first. It then uses the resulting constructed
type to find a member group. Calls retain the selected callable, receiver type, specialized
parameter types, result type, source argument mapping, and default parameter indexes in a
`BodyCallBinding`.

The current resolver handles:

- class, struct, ref struct, object, and companion member tables;
- generic receiver arguments substituted across each nominal base edge;
- constrained type parameters with inline and `where` bounds;
- inherited overload groups, with duplicate paths to the same constructed declaration collapsed;
- a more-derived same-signature declaration replacing its inherited call candidate during recovery;
- member calls, properties, array `length`, primary-constructor properties, and source constructors;
- nullable receiver diagnostics and safe-call result lifting;
- deterministic candidate ordering, cancellation, foreign-node rejection inside the resolver, and
  bounded graph traversal.

`MemberLookupResolver` is an implementation service, not a public SDK API. Consumers inspect the
resulting `BodyAnalysis.Expressions`, `BodyAnalysis.Calls`, diagnostics, and `UnverifiedNodes`.
There is no independent command-line mode that prints member lookup tables.

## Receiver categories

For an instance expression, lookup begins at the static type of that expression. A generic type is
already constructed in the source type identity, so its own declaration members receive the type
arguments before base members are visited. Each base edge substitutes again. For example:

```sol
module docs.member.receiver

interface Reader<T> {
    func read(): T
}

open class Base<U>: Reader<[]U> {
    public val values: []U

    override func read(): []U = values
}

class Child<V>: Base<V> { }

func readChild(value: Child<int>): []int = value.read()
```

The member declared on `Base<U>` is seen as `Base<int>` through `Child<int>`. In an open generic
body, the same lookup preserves the owner's type parameter instead of prematurely specializing it.

For a constrained type parameter, every nominal bound participates. Two bounds that inherit the
same declaration on the same constructed type produce one candidate. A different constructed
signature is retained and can make a call ambiguous. An unresolved bound or exceeded resource limit
does not become an empty successful lookup.

A type expression can qualify a companion object, while an object expression accesses that
singleton's ordinary members. A type expression cannot be used as an implicit instance. Module
qualifiers are handled by source name analysis; a local with the same name shadows an import alias
and is not retried as a module after a missing member.

## Inheritance and overload groups

The resolver visits constructed base paths, with a depth cap of 128, an 8192-path-state cap per
lookup, and a 128-path cap for any single declaration/constructed-owner candidate. It records each
distinct route so a future public inspection API can explain whether a member was declared
directly, inherited, or found through a generic bound. Reaching the same constructed owner along
two routes does not erase either route. The current `BodyAnalysis` API does not expose this path as
a stable serialized contract.

Functions with the same name remain a candidate group across base paths. Calls compare each
applicable signature after owner substitution and method inference. The analysis does not choose
the first declaration in the nearest source file. A same-signature derived declaration replaces
the base recovery candidate; declaration validation still requires a legal `override` and checks
return type, accessibility, finality, and checked effects.

Duplicate visits to a symbol at the same constructed owner are removed. Distinct interface
declarations are kept even if their signatures and source text match. If two unrelated inherited
methods are both applicable and neither is a valid override, ordinary overload ranking reports an
ambiguity. The same applies to incompatible constructed base paths.

The resolver treats a nearest non-callable declaration as owning its name on each route. A
more-derived method group hides a base property of that name along paths through the method owner;
an alternate path that does not pass through that owner keeps its candidate. A failed call does not
fall back to a hidden property on the same route. A property or nested type at the nearest value
depth hides more distant declarations on that route. Mixed method and value declarations at one
effective depth are ambiguous. The compiler does not use declaration order to resolve the conflict.

### Path state and candidate ownership

The current implementation's traversal work item is a tuple of constructed type, depth, complete
route, and whether the route originated in a type-parameter bound. The route uses canonical
`ResolvedType` identities from one `TypeSpecialization` workspace. A path key is the ordered
sequence of those identities. Reaching one owner through a second route is a new route state, even
when the terminal constructed type is equal.

The resolver collects a candidate by `(declaration symbol, constructed declaring owner)`. The
candidate keeps every distinct route that reaches that identity. It also stores the shortest
inheritance depth, a deterministically chosen display path, and the bound-origin bit. The display
path is for stable inspection and diagnostics; it does not replace the full route list for hiding.

| Work item or candidate field | Purpose | Limit or equality rule |
|---|---|---|
| constructed receiver | identifies substituted owner | identity, not printed name |
| depth | limits recursive base traversal | 128 edges maximum |
| full route | supports route-aware hiding and overrides | ordered owner identities |
| bound origin | explains constrained receiver lookup | accumulated along the route |
| path key | prevents exact duplicate work | same ordered identity sequence |
| candidate key | merges duplicate symbol visits | symbol identity plus constructed owner |
| retained route list | preserves diamond alternatives | maximum 128 routes per candidate |
| total visits | bounds adversarial path expansion | maximum 8192 path states |

The 8192 limit is a path-state visit budget, not an 8192 unique-type limit. A graph may reach the
same constructed owner through many different paths and consume multiple states. An exact repeated
path is skipped. A repeated identity inside the currently active route ends that cycle without
recursing forever. The candidate path limit may be reached before the global path-state limit.

On either limit, the result is `LimitExceeded` with no completeness claim. Callers report the
resource diagnostic and preserve the lookup site as unverified. A partial candidate set cannot be
ranked as complete, and “no candidate collected so far” is not returned as `Missing`.

### Name hiding and override reduction

Hiding compares each candidate's effective declaration owners to the prefix of each of its routes.
A method candidate is hidden when a relevant value declaration precedes it on that route. A
non-callable candidate is hidden by a nearer name-owning declaration according to its category.
The candidate is removed globally only if all retained routes are hidden. This retains an alternate
path that reaches the same candidate without crossing the hider.

Override recovery is also route-aware. The resolver compares specialized callable parameters and
passing modes after each candidate's own owner substitution. Method type parameters are alpha
matched by ordinal. If the signatures match, an inherited candidate is suppressed only when every
route to its owner passes through the derived declaration first. A single alternate route preserves
the inherited recovery candidate.

Recovery suppression does not validate the declaration. The body/declaration analysis separately
checks `override`, openness, accessibility rank, result compatibility, and checked effects. Keeping
those phases separate lets later body errors bind against an intended derived method without
claiming that its override declaration is legal.

| Relationship | Name reduction | Signature reduction |
|---|---|---|
| hider is on every path to candidate | candidate is hidden | not applicable |
| hider is on one of several paths | candidate retained on remaining routes | not applicable |
| derived same-signature declaration precedes base on every route | retain derived, suppress base recovery | declaration validity checked separately |
| derived same-signature declaration precedes base on one route only | keep alternate base route | possible ambiguity remains |
| derived declaration has another parameter type | both method candidates remain | normal overload ranking |
| same name at equal depth on unrelated interface owners | preserve both | ambiguity or explicit merge rule |
| same printed name, different owner identity | preserve both | never identity-deduplicate |

The graph walk can see properties, nested types, and methods with one source name. Reduction groups
those declarations by the declared name-category contract. A method/value mix at one effective
owner is an ambiguity, not an iteration-order choice. A getter absence is checked after the property
candidate remains known; it does not make the property disappear before name ownership is resolved.

### Access checks after path collection

The receiver expression is analyzed before member enumeration. For a nullable expression, plain
access reports the nullable error unless flow has a valid narrowing; safe access uses the non-null
payload for member lookup and keeps the source receiver type for protected-access checking.

The selected symbol's declaration accessibility and the effective operation's accessor visibility
are checked after lookup. Protected access uses two proof inputs: the lexical current type must
derive from the declaration owner, and the actual source receiver must meet the derived receiver
restriction. Inherited declaration type is not substituted for receiver static type. `super` remains
an explicit base route.

Method candidates that are inaccessible cannot win or contribute generic evidence. A property can
remain readable while its setter is inaccessible, or writable while its getter is inaccessible,
subject to the source visibility contract. Simple write analysis adds setter effects only; read
analysis adds getter effects. Compound operation lowering is not implemented by this analysis pass.

The current internal check treats one `SourceAnalysis` as one distribution for `internal` access.
Separate binary metadata and package identity are not modeled here. The implementation therefore
does not claim complete cross-distribution accessibility.

### Operation and access outcome examples

The access result belongs to an operation, not just to a property symbol. Consider a public
property with independently restricted accessors:

```sol
class Credentials {
    public val userName: String
        get

    public var token: String
        get
        private set
}
```

An outside caller can read `userName`, but the syntax shown has no setter. A caller can read
`token` but cannot write it. A compound update of `token` needs both getter and setter and is
therefore unavailable outside the declaring scope. The read capability is retained even though the
write capability fails.

| Expression operation | Declaration exists | Required operation access | Result |
|---|---:|---|---|
| read `userName` | yes | getter absent | deferred/error for read; no value binding |
| write `userName` | yes | setter absent | reject write |
| read `token` | yes | public getter | bind read and getter effects |
| write `token` externally | yes | private setter | access diagnostic; read remains valid |
| update `token += suffix` externally | yes | public getter and private setter | reject whole update |
| read protected method through `this` | yes | derived caller and allowed receiver | continue to overload resolution |
| read protected method through base-typed local | yes | derived caller, receiver too broad | reject access |
| call missing method on complete lookup | no | none | missing-member diagnostic |
| call inaccessible nearest method | yes | access fails | access diagnostic; no same-path base fallback |
| lookup exceeds path budget | unknown complete set | not decidable | resource diagnostic and unverified node |

The table is illustrative of the phase order. An actual property declaration must satisfy the full
property grammar and accessor signature rules. The implementation does not invoke the getter while
deciding whether the setter exists. Static analysis checks operation availability and records the
effects declared by the selected accessor; runtime invocation is a later lowering concern.

Protected access also keeps caller and receiver proofs separate. A function declared at module scope
cannot call a protected method merely because it has a variable of the derived type. A derived
method cannot call the member through an arbitrary base-typed variable just because a runtime object
might be derived. `this`, `super`, and an appropriately typed derived receiver have distinct source
semantics, summarized in [the receiver matrix](../spec/MemberResolution.ProtectedReceiverMatrix.sol).

For generic receivers, the call signature and access receiver intentionally use different views.
For `Car: Vehicle<String>`, the inherited method may specialize its parameter/result with String,
while the access check still observes a source receiver of type Car. Replacing the receiver with
`Vehicle<String>` for both jobs would cause false protected failures; replacing it with Car for both
could corrupt method signature substitution.

An unresolved receiver type or unfinished base graph is not an access grant. The analysis retains
the call as incomplete. This behavior matters when one candidate is already known: the resolver
cannot claim that the known candidate is uniquely selected if an incomplete, accessible candidate
could have a better conversion rank. Conversely, an access-rejected candidate is not allowed to
block an otherwise valid accessible candidate merely because its inaccessible signature appears
more specific.

| Resolver budget | Current bound | Exhaustion consequence |
|---|---:|---|
| constructed path depth | 128 | stop lookup with `LimitExceeded` |
| queued/visited path states | 8,192 | do not publish a complete partial group |
| retained paths per candidate | 128 | keep the lookup incomplete |
| protected ancestry work | 128 depth / 4,096 identities | do not assume access |
| generic projection closure | 4,096 identities | keep inference incomplete |

These are engineering limits, not source-level collection sizes or ABI promises. They can change
only with corresponding implementation, documentation, and regression-coverage updates. Any
increase must be checked against cancellation responsiveness and worst-case memory use; any
decrease must not silently turn valid bounded programs into successful missing-member results.

### Cancellation and deterministic publication

The resolver checks cancellation before it expands queued work and during pairwise signature
comparisons. `OperationCanceledException` is propagated. It is not converted to `Missing`, an
inaccessible candidate, or a `SOL2215` source diagnostic.

Candidate output is sorted by minimum depth, qualified declaration name, signature, and constructed
owner identity. Sorting is only for deterministic inspection. Overload ranking examines all
applicable candidates and cannot use sorted order as a tie breaker.

The result is internal to Analysis. A future public inspector may expose declaration identity,
constructed owner, route summaries, hiding reason, and override relation, but no serialized format
is promised by the current SDK. Compiler implementation changes may alter route storage without
changing the source-level lookup rules.

Constructors are selected on the declared constructed type and are not inherited through ordinary
member lookup. `this(...)` and `super(...)` constructor delegation are validated separately because
they also affect initialization flow.

## Generic substitution and inference

The owner substitution and method substitution have independent parameter identities. The receiver
may fix a class parameter named `T`, while a member method still has a distinct method parameter
named `T`. The call binder applies the receiver substitution first and only infers the method-owned
parameters that remain open.

Argument expressions contribute input bounds. `ref` and `out` storage contribute exact bounds.
Expected result types contribute upper bounds for type parameters appearing in a return type.
Nominal generic applications remain invariant: `Box<Derived>` does not provide `T = Base` for a
formal `Box<T>` merely because `Derived` converts to `Base`.

An input whose type derives from the nominal type in a formal parameter can contribute evidence
through the constructed base projection. For example, an argument of type `StringSource` can infer
`T = String` for a formal `Source<T>` when `StringSource` implements `Source<String>`. The
projection must preserve each constructed argument along the inheritance path.

When several input bounds exist, the solver searches the intrinsic nominal/bound graph for a unique
most-specific candidate that accepts all input bounds and satisfies all expected-result bounds. It
does not invent numeric widening. Incomparable common interfaces remain ambiguous. A ref/out exact
bound must agree with every other bound and requires exact writable storage after substitution.

Constraints are checked after inference. A failed class or method constraint rejects that candidate.
An invalid or unresolved constraint remains indeterminate and marks the call unverified. Ref-like
arguments still require `allows ref struct`, and a successful nominal constraint does not imply
boxing or heap storage is legal.

The solver caps structural depth at 128, visits at most 4096 constructed identities per projection
or common-type closure, and stores at most 256 distinct bounds per type parameter. These limits are
in addition to the `TypeSpecialization` workspace limit. Cancellation propagates to the caller;
it is not converted into a source diagnostic or an empty result.

## Nullable access and evaluation

For a nullable receiver, plain member access reports `SOL2243` unless flow has narrowed the receiver.
Safe access evaluates the receiver once, skips arguments and invocation when it is null, and lifts a
non-null result to one nullable layer. Safe property access is read-only as an expression result and
cannot be used as `ref` or `out` storage. A safe call that returns `!` remains bottom on the non-null
path and null on the skipped path; complete flow joining is still an implementation obligation.

The receiver is evaluated before explicit call arguments. Arguments are evaluated once in source
order after overload selection has determined their expected types. Default expressions are
evaluated only when omitted, after explicit arguments and in declaration order. Candidate discovery
and generic projection do not execute source expressions.

## Status and limitations

This work expands source analysis only. It does not create FIR, prove every body, produce LLVM IR,
or execute the bound target. The following remain open:

- distribution-level `internal` metadata across separate source/binary analyses;
- interface default conflict resolution and full explicit conflict declarations;
- all generated data-class members and complete inherited property/indexer merge rules;
- candidate-local contextual typing for every lambda and array literal shape, including complex
  cross-argument dependencies beyond the bounded replay currently implemented;
- multi-open generic function-effect set inference;
- executable safe-call, virtual/interface dispatch, ref/out, and checked-effect lowering;
- ownership/borrow and escape proof for all ref-like generic receiver paths;
- complete FIR generation and native backend support.

`BodyAnalysis.IsComplete` only describes the analysis facts this pass currently emits. It must not be
read as “the full Sol specification is implemented.” A missing fact appears in `UnverifiedNodes` or
as a diagnostic rather than an implicit successful fallback.

## Diagnostics

| Code | Meaning |
|---|---|
| `SOL2210` | No current instance receiver is available. |
| `SOL2211` | A lexical value name could not be resolved. |
| `SOL2212` | The selected name has ambiguous value/member declarations. |
| `SOL2214` | The declaration or accessor is inaccessible. |
| `SOL2215` | The receiver has no member with this name. |
| `SOL2243` | A nullable receiver requires flow proof, safe access, or assertion. |
| `SOL2290` | A bounded member/type analysis operation exceeded its resource limit. |

Diagnostics are ordered by source path, span start, and code. Candidate order used in debug output is
stable for inspection but never selects an overload.
