<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Generic inference in source analysis

This page explains the development implementation. The normative language rules are in
[GenericInference.sol](../spec/GenericInference.sol), and method/member lookup is described in
[member resolution](member-resolution.md). `CallAnalysis` and `BodyAnalysis` are inspection APIs;
neither is verified FIR or an executable backend.

The detailed contracts are split into [common types](../spec/GenericInference.CommonTypes.sol),
[recursive bounds and cycles](../spec/GenericInference.Cycles.sol),
[flow-sensitive evidence](../spec/GenericInference.Flow.sol), and
[literal/context evidence](../spec/GenericInference.Literals.sol). These files describe required
language behavior, including cases the current binder does not yet prove.

## Inputs and outputs

The solver receives declaration-owned type parameters, a fixed substitution, and a set of typed
constraints. A constraint retains the source template, actual/expected type, relation, and origin.
The fixed substitution normally includes constructed receiver parameters and explicit type
arguments. The result reports one of:

| Status | Meaning |
|---|---|
| `Solved` | Every requested parameter has one unique closed type. |
| `MissingEvidence` | At least one parameter has no source argument or expected-result evidence. |
| `ConflictingEvidence` | Exact, lower, or upper bounds have no unique compatible solution. |
| `IncompatibleShape` | An invariant nested type or callable shape cannot match. |
| `ResourceLimit` | Structural or constructed-base traversal exceeded a fixed bound. |

The service is internal to the compiler SDK. Public analysis results expose candidate outcomes and
substituted types instead of exposing a solver object whose implementation limits could later
change.

Each collected bound retains its relation and source origin. Candidate reasons summarize the
distinct relevant argument or expected-result origins in deterministic order, capped at eight
displayed origins. This makes a conflict inspectable without exposing machine paths or publishing
the solver's internal type identities as a stable SDK contract.

## Input, exact, and expected evidence

For a value argument, a direct formal parameter `T` receives the actual type as a lower bound. For
`Box<T>`, the outer nominal constructor must match `Box`; then its invariant type argument provides
exact evidence. For `ref` and `out`, a writable exact storage type is required. The generic solver
does not convert or widen a ref/out location.

The expected type of a call expression is an output bound. If `identity<T>` returns T and the call
is assigned to a `String`, the result context can infer T as String. For a generic nominal result,
expected-result inference can project the open result type through its base graph before collecting
invariant nested evidence. The specialized result is still checked against the expected type after
inference.

Each candidate receives a private copy of its substitutions and evidence. One rejected overload
cannot alter the inference state, diagnostics, flow, or lambda context of another candidate.

## Constructed base projection

Input evidence can pass through a base projection. In this example, the source parameter of
`Reader<T>` is recovered from the implemented interface rather than from the concrete class name:

```sol
module docs.generic.projection

interface Reader<T> {
    func read(): T
}

class StringReader: Reader<String> {
    override func read(): String = "value"
}

func <T> readValue(reader: Reader<T>): T = reader.read()

func demo(): String = readValue(StringReader())
```

If one type reaches the same generic base through several paths, every distinct constructed base
identity contributes evidence. A repeated path to the exact same type identity is deduplicated. If
the paths imply conflicting type arguments, the candidate is rejected. Invariance prevents a
projection from changing `Reader<Derived>` into `Reader<Base>`.

Generic bounds may themselves provide projections. For a parameter `T: Reader<String>`, the
parameter's bound is traversed after its enclosing fixed substitution. Every inline and `where`
bound participates. Constraint satisfaction remains separate from inference and is checked after
the substitution is complete.

## Common input types

When several input arguments contribute lower bounds, the solver considers the constructed nominal
and constraint graph reachable from the bounds. It selects a type only if all lower bounds convert
to it, it satisfies every expected-result bound, and there is exactly one most-specific candidate.
Thus two values with a unique shared base can infer that base. Two equally suitable unrelated
interfaces leave the inference ambiguous.

This process does not add numeric widening. For example, an already typed `byte` variable and an
`int` variable do not infer a common numeric target merely because a machine instruction could
convert one representation. Numeric variables retain Sol's explicit conversion rules. Literal
values retain their original lexical value and are checked against the specialized target after
inference.

Boxing is considered only where intrinsic conversion and the generic constraints permit it. A
ref-like argument cannot infer a heap `Object` substitution by boxing. `T: Object` does not accept a
struct merely because a regular value conversion to Object exists.

## Call mapping and generic members

Argument mapping runs before inference and supplies the formal index for each source argument. A
vararg element contributes evidence against the element type; a spread array is checked as an array
before its element type is used. A named vararg array must satisfy the invariant element identity.
Empty varargs and empty array literals supply no element evidence.

Defaults do not infer a type parameter. They are evaluated at the call site under the selected
substitution after explicit arguments. An `out var` is typed after a unique candidate has been
selected, so it cannot be used as speculative evidence that would decide which candidate wins.

For a method on `Store<T>`, receiver substitution resolves the owner `T` before method inference.
A method's type parameter with the same spelling remains independent. For a constructor, the
constructed nominal arguments and constructor method arguments (if any) are validated against their
own constraints.

## Lambda, array, literal, and effect evidence

Typed lambda parameters may provide evidence when their full function shape is known. An untyped
lambda must wait for a candidate's expected function type. Positional parameters such as `$0` have
no standalone type and cannot choose an overload by guessing. If candidate-specific contextual
typing is incomplete, the candidate remains deferred.

Array literals use an expected element type or a unique element common type. `[]` does not become an
arbitrary array type. A non-empty literal must check each element against the one selected element
type; it cannot infer a different type per element.

An uncontextualized integer or floating literal contributes the default source type `int` or
`double`. The exact literal value is then checked for representability against every candidate's
target. Literal fitting does not permit an already typed numeric variable to widen implicitly.
Binary128 literal rounding remains separately deferred.

Function effect sets are unordered. Type parameters appearing only in an effect set need a set-aware
matching algorithm; pairing effects by list position would make inference depend on source order.
The current solver marks such evidence incomplete. Declared effects are substituted after successful
type inference and checked by the separate effect analysis.

## Determinism and resource limits

The structural matcher has a depth limit of 128. One constructed-base projection or common-type
closure visits at most 4096 unique types. A single type parameter accepts no more than 256 distinct
bounds. `TypeSpecialization` independently limits its workspace to 100,000 new interned identities.
Limit failures produce explicit deferred/resource results; they are not interpreted as empty bounds
or a successful fallback to Object.

Every traversal observes cancellation. `OperationCanceledException` propagates, and no partial
substitution is published as a successful binding. Type identities are owned by the current
`TypeSpecialization`; IDs from another analysis cannot be inserted by numeric equality.

Results are deterministic for the same source set regardless of input-file enumeration order.
Sorted candidate output is for inspection only. It never breaks an inference or overload tie.

## Current implementation boundary

The body call binder now uses the shared structural solver for generic member and direct call
candidates. It supports input, exact ref/out, expected-result bounds for written returns,
invariant nested applications, and constructed base projections. Constraints are evaluated for both
generic methods and constructed type owners. A missing parameter marks the call deferred; a
conflicting/incompatible candidate is rejected so another independently valid overload can remain.

The direct `CallAnalysis` SDK has no enclosing expression-context parameter. It can use argument
evidence but cannot infer a method type from an expected result outside the call. It reports that
case as deferred when argument evidence is insufficient.

Not yet implemented: all lambda/array-literal evidence, function effect-set inference, full common
interface/boxing interactions, generic body rechecking for each monomorphized substitution,
user-defined conversion inference, checked-effect coverage, safe-call lowering, complete flow and
borrow verification, FIR generation, or native code generation. The open work is also listed in
[generic calls](generic-calls.md) and the [compiler status](status.md).
