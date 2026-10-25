<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Receiver and member resolution

This page describes the development `BodyAnalysis` receiver lookup. It does not describe a complete
type checker or executable backend. The normative source rules are in
[MemberResolution.sol](../spec/MemberResolution.sol); the public implementation boundary is in
[body analysis](body-analysis.md).

The additional contracts cover [nullable receiver flow](../spec/MemberResolution.NullableFlow.sol)
and [partial type identity](../spec/MemberResolution.PartialIdentity.sol), including behaviors that
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

The resolver visits constructed base identities, with a depth cap of 128 and an 8192-identity cap
per lookup. It records the path so a future public inspection API can explain whether a member was
declared directly, inherited, or found through a generic bound. The current `BodyAnalysis` API does
not expose this path as a stable serialized contract.

Functions with the same name remain a candidate group across base paths. Calls compare each
applicable signature after owner substitution and method inference. The analysis does not choose
the first declaration in the nearest source file. A same-signature derived declaration replaces
the base recovery candidate; declaration validation still requires a legal `override` and checks
return type, accessibility, finality, and checked effects.

Duplicate visits to a symbol at the same constructed owner are removed. Distinct interface
declarations are kept even if their signatures and source text match. If two unrelated inherited
methods are both applicable and neither is a valid override, ordinary overload ranking reports an
ambiguity. The same applies to incompatible constructed base paths.

The resolver treats a nearest non-callable declaration as owning its name. A more-derived method
group hides a base property of that name; a failed call does not fall back to that property. A
property or nested type at the nearest value depth hides more distant declarations. Mixed method and
value declarations at one effective depth are ambiguous. The compiler does not use declaration
order to resolve the conflict.

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

- complete protected receiver restrictions and distribution-level `internal` metadata across
  separate source/binary analyses;
- interface default conflict resolution and full explicit conflict declarations;
- all generated data-class members and complete inherited property/indexer merge rules;
- candidate-local contextual typing for every lambda and array literal shape;
- generic function-effect set inference;
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
