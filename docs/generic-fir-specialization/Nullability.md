<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->

# Nullability in generic FIR specialization

## Purpose and implementation status

This guide defines the boundary between source-level nullability and the verified native FIR
subset. It is for compiler maintainers working on type identity, generic inference, source flow,
FIR construction, verification, and eventual native lowering.

The compiler already has a source type node for nullable types. Type construction interns canonical
identities and collapses repeated adjacent nullable wrappers. Generic substitution rewrites nested
type graphs simultaneously, including nullable constructors, arrays, tuples, function signatures,
and thrown effects. Generic inference has rules for nullable templates and nullable evidence.

Source analysis handles a subset of nullable expressions and flow facts. It contains logic for null
literals, nullable conversions, safe member access, null assertions, nullable operators, and stable
local narrowing. This is not a claim that every source nullability operation has complete binding,
flow invalidation, effect, and diagnostic behavior. Each operation should be checked against the
normative source contract in `spec/GenericFirNullability.sol`.

The current verified FIR/native gate does not admit nullable reference types, nullable value types,
nullable array elements, or nullable array containers. A source-valid generic specialization that
contains one of these types must remain outside native output. Existing native support for `String`,
primitive scalars, and a one-dimensional array of supported non-null elements does not imply nullable
support.

| Area | Current implementation | Status of nullable native code |
| --- | --- | --- |
| Canonical source type identity | Nullable type nodes and interning are present | Source identity only |
| Repeated nullable suffix | Normalized during canonical construction | One source identity, no native layout |
| Generic substitution | Owner-sensitive, simultaneous type rewrite | Nullable rewrite works as a type operation |
| Generic inference | Collects input and expected-result evidence | Bound inference is not an ABI proof |
| Source expressions | Some null-aware expression paths exist | Coverage is partial |
| Source flow | Some stable local facts are tracked | Full invalidation and rebinding remain incomplete |
| FIR admission | Closed types checked by verifier | Nullable forms are rejected |
| LLVM lowering and GC | No general nullable representation proof | Do not lower by guessing |

Keep these status statements synchronized with the code. Never describe a nullable specialization as
implemented because its syntax parses, its source type is known, or its generic substitution closes.

## Phase ownership

Nullability facts pass through several compiler phases. Each phase owns a different responsibility.

| Phase | Responsibility | Must not claim |
| --- | --- | --- |
| CST | Preserve tokens, delimiters, and written grouping | A valid type or a runtime representation |
| PSI | Provide navigable source structure and origins | A closed generic substitution |
| AST | Preserve nullable type syntax in the source tree | Successful source binding |
| Type analysis | Construct canonical nullable identities | Native ABI support |
| Generic inference | Choose a unique substitution from allowed evidence | That every substituted call can lower |
| Body analysis | Check operators, conversions, and available flow facts | That source flow facts have a native proof node |
| FIR builder | Construct only a closed native candidate | That unchecked nodes are verified |
| FIR verifier | Independently validate types, slots, calls, and CFG | That a source-valid unsupported shape can be erased |
| LLVM backend | Emit only a verified representation | That a pointer-sized value is safe for every nullable type |

When an invariant fails, report the failure in the phase that owns it. Do not turn a backend admission
failure into an overload-resolution failure. Do not turn a source nullability error into a native
unsupported diagnostic.

The frontend may retain source spelling for diagnostics and formatters. The semantic identity used
for overload comparison, substitution, and FIR validation is canonical and independent of spelling.

## Canonical nullable type identity

`T?` is a nullable constructor around the complete preceding type. A nullable type identity includes
the canonical identity of its underlying type. It is not a boolean annotation stored outside the
type graph, and it is not an instruction to reinterpret a non-null value after inference.

Repeated nullable constructors collapse to one layer:

```text
String?       // canonical nullable String
String??      // same canonical identity as String?
T??           // same canonical identity as T?
([]String?)?? // same identity as ([]String?)?
```

The collapse applies at each nullable node. It does not erase other constructors or move a wrapper
through an array. In particular, the parser and type builder must preserve the distinction between
the nullable element and nullable container forms below.

| Source type | Canonical meaning |
| --- | --- |
| `[]String?` | Non-null array; nullable String elements |
| `[](String?)` | Same identity as `[]String?` |
| `([]String)?` | Nullable array; non-null String elements |
| `([]String?)?` | Nullable array; nullable String elements |
| `[][]String?` | Non-null nested arrays; nullable String leaves |
| `([][]String?)?` | Nullable outer array; nullable String leaves |

The dynamic array constructor consumes its element type after `[]`. To make the array reference
nullable, group the entire array type before applying `?`. A formatter may normalize equivalent
spellings such as `[]String?` and `[](String?)`; it must never print `([]String)?` as if it were
`[]String?`.

The type graph should retain constructor position even if its human-readable `Name` is empty or its
display string omits redundant parentheses. Diagnostic origins are a separate property: collapsing
`T??` to `T?` must not discard the span of the source token that introduced the redundant suffix.

For canonical identity, include at least:

- The type kind and nominal declaration identity, when applicable.
- Generic parameter owner identity and ordinal, rather than its written name alone.
- The ordered argument identities for arrays, nullable wrappers, tuples, and functions.
- Function parameter passing modes and the declared effect identities.
- Nullable wrapper position at every point in the type graph.

Do not use a rendered type name as the sole cache key. Two distinct declarations can share a name,
and two source spellings can normalize to the same canonical identity.

## Generic substitution

Generic parameters are identified by the declaration that owns them and their ordinal within that
declaration. A `T` belonging to a function is not the `T` belonging to an enclosing object or a
different function. The same textual spelling does not make two parameters interchangeable.

Substitution is a simultaneous rewrite over the original type graph. A replacement is inserted as a
closed replacement node for that rewrite; the algorithm does not recursively apply another mapping
entry to the contents of that replacement during the same pass.

```sol
module docs.generic_fir_specialization
func <T> echo(value: T): T = value
func <U> maybe(value: U?): U? = value
```

For `maybe<String>(null)`, the selected map is `U -> String`. Both formal and result become
`String?`. For `maybe<String?>(value)`, substitution first forms a nullable wrapper around `String?`;
canonical construction collapses that nested wrapper to one `String?` identity.

The rewrite must recurse through:

- A nullable node and its complete underlying type.
- Dynamic arrays and fixed arrays, preserving their element and extent constructors.
- Tuple fields, preserving order and each field's nullability.
- Function parameters, results, passing modes, and effects.
- Nominal type arguments and all nested occurrences of a parameter.
- Thrown type identities, without erasing checked-effect information.

Substitution must preserve owner and ordinal identity for parameters that are not in the selected
map. It must not replace a parameter merely because another parameter has the same source spelling.
The resulting map must be complete before FIR specialization begins.

Useful rewrite checks include:

| Template | Substitution | Result |
| --- | --- | --- |
| `T?` | `T = String` | `String?` |
| `T?` | `T = String?` | `String?` after normalization |
| `[]T` | `T = String?` | `[]String?` |
| `([]T)?` | `T = String` | `([]String)?` |
| `([]T?)?` | `T = String` | `([]String?)?` |
| `[][]T` | `T = String?` | `[][]String?` |
| `(T?, String)` | `T = int` | `(int?, String)` |

These are source identity results. They do not state that the result can be serialized, verified, or
lowered to LLVM today.

## Nullable references and nullable values

At source level, `?` expresses the same presence property across categories: a value can be null.
Its concrete runtime representation depends on the underlying type and ABI. A nullable managed
reference may have a null reference representation. A nullable integer may require a tag, a spare
bit representation, an expanded aggregate, or another explicitly defined layout. The compiler
cannot infer a correct layout from the source marker alone.

A nullable representation contract must define, for every admitted type and ABI position:

- Parameter and return calling convention.
- Local, spill, tuple, closure, and aggregate storage layout.
- Null construction, comparison, and extraction behavior.
- Conversion behavior between nullable and non-null types.
- Root liveness and GC descriptor behavior for reference payloads.
- Array element layout and write barrier behavior, when the type is stored in an array.
- AOT and ORC agreement, including runtime helper signatures.
- Serialization and verifier invariants, if the type crosses the FIR JSON boundary.

The same exact representation must be used at every occurrence of a generic type argument. If a
specialization binds `T = String?`, then `T`, `T?`, `[]T`, tuple fields, callback return types, and
runtime calls must all be checked after substitution. A backend cannot erase the nullable wrapper in
only a local variable or one call argument because the underlying value happens to fit a register.

Do not reinterpret nullable values by pointer casts, use null as a sentinel for a nullable numeric
value without a language-level representation rule, or pass a nullable reference to a non-null
function and expect that callee to guard it implicitly.

## Nullable arrays

Array nullability has two independent positions: the container reference and the element type. A
null array and an empty array are observably different. The former has no length until it is proven
present; the latter has length zero. An array with nullable elements is still a present container.

```sol
module docs.generic_fir_specialization
func elementMaybe(): []String? = ["Sol", null]
func containerMaybe(): ([]String)? = null
func bothMaybe(): ([]String?)? = null
```

Expected source identities:

| Expression or formal | Container can be null | Elements can be null | Index result |
| --- | --- | --- | --- |
| `[]String` | No | No | `String` |
| `[]String?` | No | Yes | `String?` |
| `([]String)?` | Yes | No | `String` after container proof |
| `([]String?)?` | Yes | Yes | `String?` after container proof |

Generic substitution can introduce element nullability even if the declaration has no explicit
nullable suffix at the array site:

```sol
module docs.generic_fir_specialization
func <T> copy(values: []T): []T = values
```

With `T = String?`, the array is `[]String?`: the container is non-null and its elements are
nullable. Conversely, `func <T> maybe(values: ([]T)?): ([]T)?` with `T = String` produces a nullable
array of non-null String elements. These forms cannot share an ABI by moving a wrapper between
container and element.

Indexing a nullable-container array requires a successful container proof or safe access. A
container proof does not prove the selected element non-null. For `[]String?`, each indexed result
has type `String?`; a prior check of index zero does not prove index one present. Mutable aliases,
array writes, and repeated index evaluation require the flow analyzer to track location stability.

The current FIR verifier only admits its selected non-null, one-dimensional array subset. It rejects
nullable containers, nullable element arrays, nested arrays, and fixed arrays. Keep this gate in
place until the array layout, element descriptor, nullability flow, bounds behavior, and GC rules are
specified and verified together.

## Generic inference evidence

Inference is candidate-local and source-owned. An explicit type argument fixes its parameter before
argument conversion checks. Input arguments and a permitted expected result can contribute evidence
according to the generic inference relation. No backend capability or preferred machine type may
contribute source inference evidence.

For a nullable template such as `T?`, an actual nullable type retains its nullable identity while the
solver collects underlying evidence for `T`. A non-null actual can also provide evidence for `T`
when that candidate and its conversion rules admit the nullable formal. This evidence does not make
all other conversions nullable-aware: a `T` formal remains non-null when `T` is inferred as
`String`, and a nullable actual cannot be silently passed to it.

The null literal is represented by the nullable form of bottom in source analysis. It has no
underlying concrete type to infer. A call that supplies only `null` to a generic nullable formal and
has no explicit type argument or permitted expected-result evidence must stay unresolved. Never
default it to `Object`, `String`, a pointer type, or whichever type happens to be in the FIR subset.

Expected-result evidence is local to the enclosing expression and the defined inference rules. It
must not be pulled from a later assignment, sibling branch, unrelated overload, or anticipated
native layout. If argument evidence and expected-result evidence disagree after canonicalization,
report a source inference conflict. Do not discard nullable information to force a unique result.

Before constructing a generic FIR call, ensure that:

1. Exactly one source candidate has been selected.
2. Every type parameter has one canonical closed substitution.
3. The substituted signature passes source conversions and constraints.
4. The body has the source analysis status required by the FIR builder.
5. Nullable nodes are independently checked against the FIR native subset.

An inference result marked `Solved` means only that source types were selected. It does not mean the
resulting specialized function has a native layout or an LLVM body.

## Source flow facts

Flow analysis can prove a particular value non-null on a particular control-flow edge. It does not
mutate the declared type or generic type argument. For a stable local `value: String?`, the true edge
of `value != null` can use `String`; the declaration remains `String?` outside that proven region.

```sol
module docs.generic_fir_specialization
func size(value: String?): int = {
    if value == null { 0 } else { value.length }
}
```

The false branch has a non-null fact for `value`. The function result is `int`, and the parameter
type remains nullable. If this function's complete signature or body is outside native FIR, successful
source flow analysis does not override that rejection.

Flow facts must be tied to a stable value identity and program point. Invalidation may be required
after:

- Assignment to a mutable local or property.
- A write through an alias that can reach the value.
- A call that may mutate a referenced location.
- Closure capture or callback invocation with observable mutation.
- A suspension or concurrent operation that can race with a write.
- Re-evaluation of an unstable property or array element expression.

An array container check refines the container only. An element check refines the checked value only
when the analyzer can prove that the same read is used afterward. A loop item from `[]String?`
starts nullable on each iteration. Checking one item does not narrow later items or change the
declared array element type.

At a control-flow join, keep only facts valid on every reachable incoming edge. A branch returning
`!` has no normal edge and is removed from the join. A branch returning `null` remains reachable and
can make the joined result nullable. Loop facts require a fixed-point treatment; a check in one
iteration cannot be assumed to hold before every iteration.

Current source analysis tracks some null checks and local narrowing, but it is not a complete generic
per-instance rebind. Do not claim support for complex alias, capture, asynchronous, or concurrent
invalidation until each case has explicit source analysis and FIR proof behavior.

## Nullable operators

### Safe call

For `receiver?.member(args)`, evaluate `receiver` exactly once. If it is null, skip member access and
all argument evaluation. If it is present, perform ordinary lookup and invocation on the underlying
type. Lift the result to nullable. If the result was already nullable, canonicalization leaves one
nullable layer.

A safe call does not narrow `receiver` after the expression. It is not automatically writable
storage, an addressable property, or a valid `ref`/`out` argument. The FIR needs an explicit null
branch and must preserve evaluation order and effect edges.

### Null assertion

`value!!` asserts presence. On its normal path it has the non-null underlying type. The null path
performs the specified assertion failure behavior and has no normal value. It is not a cast, generic
constraint, or proof that two distinct values are equal. If the assertion can throw, its effect and
source origin must remain attached to the correct control-flow edge.

### Elvis

`left ?: right` evaluates `left` once. It evaluates `right` only when `left` is null. The expression
type follows the nullable-left and right conversion rules. A non-null fallback can produce a
non-null result. A fallback of type `!` has no normal result and contributes no value to the join.
Checked effects raised by either reachable operand remain in the function's effect obligations.

These operators are source-level contracts even when the native subset rejects their representation.
Do not lower them as unconditional eager calls, pointer casts, or a default-zero value. Source
acceptance of an operation does not prove that FIR has its control-flow node or runtime behavior.

## Bottom type and effects

`!` is the bottom type: it has no values and no normal continuation. It converts to any source type
on a normal-flow typing rule because there is no normal value to convert. `!?` denotes nullable
bottom; under the source contract its only value is null. Repeated nullable wrapping still
normalizes to one layer.

Bottom is not a placeholder for failed inference, an uninitialized value, or an unconstrained generic
argument. It cannot be stored as an ordinary FIR slot. A `return`, throw, or other terminating
expression must be represented as control flow rather than a made-up payload.

Nullability does not change checked-effect rules. A nullable `Throwable?` is not a non-null thrown
value and does not satisfy a non-null Throwable bound. Prove presence or assert it before throwing.
Substituting a checked exception type into a generic effect preserves the declared effect identity.
Do not erase the effect because a given path appears not to throw.

## Current FIR admission and diagnostics

The current native gate is intentionally narrower than source type analysis. `FirVerifier.Supported`
admits selected non-null primitive types, the empty tuple used for no value, `std.core.String`, and a
one-dimensional array of supported non-null primitive or String elements. Nullable wrappers do not
match that predicate. Nullable generic arguments, nullable slots, nullable results, nullable array
elements, and nullable array containers are rejected.

The source/FIR boundary should retain these distinctions:

| Situation | Owning phase | Expected diagnostic family |
| --- | --- | --- |
| Nullable actual passed to a non-null formal without proof | Source binding | Source conversion/nullability diagnostic, such as `SOL2243` where applicable |
| Source-valid nullable type outside native subset | FIR admission | `SOL2400` |
| An independently invalid FIR graph reaches verifier | FIR verification | `SOL2402` |
| Documented generic specialization budget is exhausted | Specialization | `SOL2403` |

Do not report a source-valid but unsupported nullable program as a syntax error or unresolved type
argument. Do not fall back to another overload because the selected candidate is harder to lower.
Do not silently erase nullability or change `[]String?` into `([]String)?`. No partially lowered
body, native cache entry, or artifact is a successful result after the nullable admission check fails.

Diagnostics should identify the source type or operation position and preserve the user-authored
span even if canonicalization normalized a repeated suffix. The wording should name the unsupported
native representation or operation, not imply that the source language forbids the nullable type.

## Work required before native admission expands

Enabling one nullable shape in the verifier is not enough. The compiler must demonstrate a complete
chain of proofs from source type identity to emitted representation.

### Type and inference

- Keep a canonical representation for each nullable node position after substitution.
- Preserve generic owner/ordinal identity and simultaneous substitution semantics.
- Define explicit and inferred nullable argument behavior, including null-only evidence.
- Validate constraints without erasing nullable wrappers.
- Rebind or equivalently validate generic body operations for each closed substitution.

### Flow and operations

- Carry stable null facts with source origins and CFG dominance.
- Invalidate facts after mutable writes, aliases, captures, calls, and asynchronous boundaries.
- Represent safe-call gates, assertion failure edges, and Elvis fallback edges exactly.
- Preserve effects and evaluation order through normal and exceptional CFG successors.
- Keep array element facts distinct from container facts and from other array slots.

### Runtime and native ABI

- Specify nullable reference and nullable value layouts separately.
- Define nullable parameters, returns, locals, spills, tuples, and function values.
- Define nullable array descriptors and reference-slot treatment for GC.
- Define root visibility, write barriers, and safepoint behavior for null values and payloads.
- Use one checked proof for FIR verifier and the C++ LLVM API bridge.
- Verify parity for AOT and ORC without adding an implicit boxing or pointer-erasure path.

### Validation and diagnostics

- Reject unsupported shapes before artifact emission and specialization-cache insertion.
- Preserve source and type identities across FIR serialization and round trips.
- Keep source errors, verifier failures, and bounded resource failures distinct.
- Document exactly which nullable shapes are admitted in each release.

Until these requirements are implemented, retain conservative rejection. A prototype that handles
nullable String does not prove nullable numeric values, nullable arrays, reference-bearing structs,
generic callbacks, exception flow, or GC correctness.

## Maintainer review checklist

Use this checklist when changing nullable type construction, inference, body analysis, or FIR
admission:

- Did the parser preserve grouping needed to distinguish a nullable array from nullable elements?
- Does canonicalization collapse only adjacent nullable wrappers?
- Are owner and ordinal identities used for every generic substitution?
- Is substitution simultaneous, bounded, and owned by one type workspace?
- Does null-only evidence remain unresolved without a unique source context?
- Are expected-result bounds applied only where the source inference contract allows them?
- Does each array check refine the container, the selected element, or both as specified?
- Can any write, alias, capture, callback, or concurrency event invalidate the recorded fact?
- Do safe call, `!!`, and Elvis preserve single evaluation, branch behavior, and effects?
- Are nullable reference and value layouts defined separately for every ABI position?
- Do GC descriptors distinguish null from a live reference payload?
- Does FIR verification reject a node if its representation proof is missing?
- Are AOT, ORC, serialized FIR, and diagnostics consistent for the same closed type?
- Are current implementation limits stated as limits rather than future behavior?

If any answer depends on a displayed type name, guessed pointer layout, absent source span, or
unrecorded flow assumption, the specialization must remain rejected until the invariant is explicit
and verified.

## Scope and non-goals

This guide does not change the Sol command-line interface, entry point, artifact types, LLVM version,
runtime process isolation, or generic specialization resource limits. It does not introduce new
syntax, boxing, an on-disk specialization cache, a stable generic C ABI, or a promise that source
nullable operations are already native-capable.

The normative source rules live in `spec/GenericFirNullability.sol`; this guide explains how those
rules intersect the current generic FIR implementation. Source examples are in
`spec/examples/semantics/GenericFirNullability.sol`. Their `expect` comments distinguish source
analysis from native FIR outcomes. Do not treat them as evidence of an executed test run or emitted
native artifact.

## Current code map

The implementation is split across Analysis and FIR. Keep changes in the phase that owns the
invariant, then check that the next phase still validates the result independently.

| Concern | Current implementation area | Review focus |
| --- | --- | --- |
| Resolved type shape | `ResolvedType.cs`, `TypeBindingBuilder.cs` | Nullable node and written source origin |
| Constructed identity | `TypeSpecialization.cs` | Interning, ownership, duplicate nullable collapse |
| Substitution | `TypeSpecialization.Substitute` | Simultaneous rewrite and constructor traversal |
| Inference | `GenericInferenceSolver.cs` | Nullable template relation and unresolved evidence |
| Candidate mapping | `CallBindingBuilder.*` | Source conversions and selected candidate stability |
| Body expressions | `BodyBindingBuilder.Expressions.cs` | Null literal, `!!`, and expression result type |
| Members and calls | `BodyBindingBuilder.Members.cs`, `BodyBindingBuilder.Calls.cs` | Safe receiver behavior and lifted result |
| Flow | `BodyBindingBuilder.Flow.cs` | Dominance, local narrowing, and invalidation |
| Native admission | `FirBuilder.cs`, `FirVerifier.cs` | Unsupported source types and independent verification |

This map is a navigation aid, not a statement that every semantic edge in each row is complete.
Before extending one path, inspect the corresponding tests and diagnostics in the owning project.
For this task no tests were added or run; the examples in `spec` are normative documentation.

## Inference implementation notes

`GenericInferenceSolver` records evidence in relation buckets rather than mutating a source type
parameter in place. The relations distinguish argument input, exact constraints, and expected-result
evidence. The selected substitution belongs to the candidate that produced the evidence. Do not
share a partially solved map between overload candidates.

When the template node is nullable, the solver matches its underlying type against the actual
payload when the actual is nullable, and against the actual type when it is non-null. Thus a
`T?` formal can provide underlying evidence without changing the formal into `T`. In the reverse
direction, an actual nullable type does not match an unwrapped non-null template merely because its
payload would fit. This is the key guard against accidental nullable erasure.

The null literal is typed in analysis as nullable bottom. Bottom supplies no arbitrary concrete
generic type. If no other input or result context supplies evidence, the solver reports missing
evidence and the call is not closed. The caller must preserve that status rather than constructing a
FIR function with an open parameter identity.

For a candidate that is source-valid, close every type parameter, substitute the full signature,
then check source conversions and constraints. If the source signature closes to `String?`, the FIR
builder still checks that nullable result separately. The inference solver must not look at
`FirVerifier.Supported` to decide whether to infer `String`, `String?`, or another underlying type.

## Flow facts and source expression review

The Analysis expression builder currently creates nullable bottom for the null literal, unwraps the
source type of `!!` on its normal result, and uses `GetNullable` for selected lifted expression
results. Member and call binding mark safe access and apply a nullable result wrapper. Flow analysis
records a narrowed type for selected stable locals after null comparisons.

These facts need careful boundaries:

- A `value!!` result type does not alone encode the exceptional null edge. The eventual FIR node
  must model the assertion behavior.
- A safe member's nullable result does not prove that the receiver remains present after the member
  expression.
- A narrowed local must be associated with a control-flow region and invalidated when its value can
  change.
- A nullable array element is a separate value from its container and from every other element.
- A generic declaration-level body summary is not automatically a proof for each concrete
  substitution.

When adding a source expression rule, record its source type, normal and exceptional edges, result
nullability, operand evaluation count, checked effects, and stable-value facts. When adding FIR
support, represent each of those facts explicitly or reject the expression. A result type by itself
is not sufficient to reconstruct correct evaluation behavior later.

## Native boundary review sequence

Use this sequence when considering whether a nullable node can pass the native gate:

1. Locate the source declaration and confirm source binding selected it without backend influence.
2. Inspect its complete generic specialization argument tuple and constraint result.
3. Substitute every signature, body, array, tuple, callback, and effect type.
4. Confirm no nullable wrapper is hidden inside a supported nominal, array, or function type.
5. Identify the exact ABI and runtime representation for every nullable position.
6. Confirm the FIR node carries the null branch, null assertion, or element-map proof it needs.
7. Confirm the verifier independently checks that proof before constructing `VerifiedFirProgram`.
8. Confirm the LLVM API bridge uses the same layout and no implicit erasure or boxing path.
9. Confirm root maps, GC descriptors, and array barriers treat null and live references correctly.
10. Confirm AOT and ORC both follow the same semantic and diagnostic contract.

If any step is missing, keep the rejection at FIR admission. Do not add a one-off check only in the
builder: malformed or serialized FIR must not bypass the verifier. Conversely, do not teach the
source type system that a well-typed nullable form is invalid merely because native lowering is
unfinished.

The verifier's supported-type predicate is intentionally a conservative boundary. A change that
allows a nullable identity there is a semantic/runtime expansion, not just a type-list update. It
requires corresponding node validation, lowerer support, runtime integration, and release-facing
status updates before it can be called implemented.

## Quick distinction table for reviews

When a review comment says “nullable array,” ask which constructor position is nullable. Use these
four forms as the minimum terminology:

```text
[]String       // present container, present String elements
[]String?      // present container, nullable String elements
([]String)?    // nullable container, present String elements
([]String?)?   // nullable container, nullable String elements
```

For a generic `[]T` declaration, `T = String?` yields the second form. For a generic `([]T)?`
declaration, `T = String` yields the third form. The substitutions do not exchange their wrappers.

In review discussions, describe a position explicitly as “nullable element,” “nullable container,”
or “both.” Avoid saying only “nullable array,” because that phrase does not identify the source type
identity, flow check, ABI requirement, or GC descriptor involved.

Likewise, distinguish a nullable generic argument from a nullable formal: `T = String?` means the
substituted type argument is nullable; a formal written `T?` adds a nullable constructor around the
substituted argument and is then normalized. This distinction matters for constraints, cache keys,
overload identity, and native representation review.

For diagnostics and design notes, include the complete type shape in examples, not just the generic
argument. Record both the source outcome and native FIR outcome. This makes it clear whether a
reported failure is a source rule, an unresolved inference result, a verifier invariant, or a current
backend limit.

Keep this distinction in code comments, review summaries, and release notes whenever nullable
specialization work changes the source analyzer or native boundary.
The source contract and the native admission table should be reviewed together at that point.
Do not let a temporary backend constraint redefine the language-level type identity.
