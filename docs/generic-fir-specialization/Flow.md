<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Flow analysis through generic FIR specialization

This guide describes how definite assignment and control flow travel from a generic source body
into one closed FIR instance. It is a compiler-maintainer guide for the bounded native slice. The
normative rules are in [`spec/GenericFirFlow.sol`](../../spec/GenericFirFlow.sol); the scenario
matrix is [`GenericFirFlow.sol`](../../spec/examples/semantics/GenericFirFlow.sol).

The current compiler builds a CFG for each closed specialized FIR body and verifies its slots after
substitution. That is useful and necessary, but it is not a full source semantic rebind of the body
for each type argument. Keep that implementation boundary visible when describing examples,
diagnostics, release state, and follow-up work.

## At a glance

| Layer | What it sees | What it proves | What it does not prove |
| --- | --- | --- | --- |
| Source body analysis | Generic declaration with open, owner-scoped parameters | Source types, reachability, assignment, return, ref/out, effects | That every concrete type has a native lowering |
| FIR construction | Selected call plus closed ordered substitution | A concrete body shape projected from already-bound facts | That open-type operations were fully rebound for this instance |
| FIR verifier | Closed slots, instructions, calls, CFG and origin data | Structural and definite-assignment safety of this FIR instance | That machine code behaves correctly for every runtime input |
| Runtime | One compiled artifact and concrete inputs | Observed execution behavior | Static safety of unexecuted CFG paths or other instances |

The rows are complementary. A source `Bound` result cannot mint verified FIR. A verifier success
cannot stand in for native ABI or runtime validation. A successful execution cannot prove that all
reachable paths or other generic instances are safe.

## Current implementation status

The source analyzer binds a generic function once in its declaration context. Generic pass-through
flow works with an open parameter when no type-dependent operation is required. For an admitted
closed generic free-function call, FIR substitutes types into the existing bound body facts and
creates a monomorphic function. It applies the outer instance substitution to nested generic call
arguments before requesting inner instances.

`FirVerifier` then builds control-flow graphs for those specialized bodies. It verifies concrete
slot types, reads and writes, branch targets, call parameter modes, return obligations, and
definite assignment. This is an independent proof over FIR; it does not trust source summaries or
serialized status bits as a replacement for examining the graph.

The missing step is full per-instance source semantic rebinding. The current body-surface gate
rejects open-type-dependent operators, members, and calls to nongeneric functions when their
applicability depends on an unresolved generic parameter. A concrete `T=int` specialization does
not retroactively make `value + value` or `mark(value)` valid if the generic declaration could not
bind those expressions while T was open. The current rejection is `SOL2400` for the unsupported
native subset, with source diagnostics preserved when the body is invalid at the language level.

The generic free-function native slice is limited further by closed ABI shapes. Primitive scalar
arguments (except `half` and `quad` in this bootstrap slice), `std.core.String`, and a single array
layer over admitted primitive/String elements are the intended subset. Generic nominal layout,
multi-level arrays, byref-like values, function values, dynamic dispatch, and generic container
exports need separate contracts.

## Three kinds of statement

When reading this guide, distinguish these terms:

- **Source rule:** required behavior of the Sol program independent of the present FIR subset.
- **FIR requirement:** a property the compiler must verify before marking a graph verified.
- **Runtime observation:** behavior recorded for one emitted artifact with one concrete input.

Some contracts and matrix entries describe future correct behavior so the implementation team can
track completeness. Such a normative statement does not say that the current implementation
already supports or tests that behavior.

Likewise, “source valid” does not mean “native admitted.” The source language may accept a generic
body that the current compiler cannot lower. The native gate must reject unsupported syntax before
verified FIR or output artifacts are published.

## Owner-scoped type identity

A generic parameter is identified by its declaration owner and ordinal, not by the text used to
name it. In the declarations below, `T@identity/0` and `T@relay/0` are distinct identities:

```sol
func <T> identity(value: T): T = value
func <T> relay(value: T): T = identity(value)
```

The selected call edge inside `relay` maps `identity`'s T to `relay`'s T. A closed caller may then
map `relay`'s T to `String`. The resulting inner key is `identity<String>`. The caller substitution
is composed into the inner call's ordered type arguments; it is not copied into every target by
position or by matching spellings.

This matters for flow because slot types, return types, and ref/out formal types all retain owner
identity until substitution. Equal display text is not evidence that two slots have the same type.
Numeric IDs are compilation-local and must not be reused as stable cache, ABI, or cross-workspace
identities.

## Flow-state ingredients

At minimum, source and FIR flow states need to track:

- slot type identity, including any declaration-owned generic parameter;
- definite assignment on every reachable normal predecessor;
- whether a `val` has consumed its allowed write;
- whether a mutable slot has been reassigned since a refinement was established;
- nullability and other refinements tied to a particular storage identity;
- reachability, lexical region, loop target, and successor kind;
- writes to ref/out locations and their normal-completion point;
- relevant cleanup and checked-effect context.

Do not reduce a flow state to one “initialized” Boolean for a whole function. Conditions, loops,
exceptional edges, calls, and cleanup produce distinct snapshots. A flow summary must make the edge
to which each fact applies explicit.

## Assignment transfer for generic locals

Generic pass-through locals should be uninteresting to the assignment engine:

```sol
func <T> copy(value: T): T {
    val first: T = value
    var second: T
    second = first
    second
}
```

The `first` slot has type expression T and begins assigned. `second` has type expression T and
begins unassigned; its assignment marks it assigned on that normal edge. The last expression reads
an assigned slot. For `copy<int>`, FIR slots and expressions substitute to int. For `copy<String>`,
they substitute to String. The assignment lattice is not changed by that substitution.

At a join, definite assignment is a must fact. A slot is assigned after the join only if every
reachable normal predecessor carries an assignment. An unreachable predecessor contributes no
state. Return, throw, break, and continue edges are not folded into the enclosing normal successor.

A `val` has a separate write-once condition. Mutually exclusive writes can both be legal when each
path performs no more than one write. A second write on one concrete path is invalid even if other
paths write only once. A `var` can be written again, but a read still requires assignment on every
reachable incoming path.

## Branches and result expressions

An `if` creates its reachable successors before generic substitution. If there is no `else`, the
false edge carries the pre-branch state. If each arm writes a slot, it can be assigned after the
join. If one arm returns, that arm does not reach the normal join. If it throws, it has an
exceptional continuation rather than satisfying a normal-return obligation.

For an expression-valued branch, every reachable normal arm must provide a compatible result. The
result type may contain T, but the eventual instance is not allowed to decide which source arm is
well typed. Source binding must establish a generic result before FIR closes the type.

Calls in different arms keep different source locations and effects even if both calls intern to the
same generic function instance. Sharing a specialized body does not hoist, merge, or reorder calls.
An out write in one branch cannot become a write on the opposite branch through instance sharing.

## Definite assignment and returns

For a non-unit function, every reachable normal exit must produce a compatible result. An explicit
return and a block's final expression follow the same result contract. A tail after an unconditional
return is unreachable; it is not a missing result and cannot supply evidence to reachable code.

For `func <T> choose(...): T`, each reachable return expression must be source-compatible with T.
FIR substitutes both the return signature and the expression types, then checks that every return
terminator returns the correct closed result. A branch that falls through still needs a valid result
under the function's block rules.

Exception edges retain the state at the operation that failed. A value written later cannot be
borrowed by an earlier throw edge. A call's out effect reaches its caller only on normal completion
as specified by the call contract. The backend's register class does not merge normal and
exceptional continuations.

## Loops, break, and continue

Treat a loop as a graph with entry, body, back edge, and exits. Unless sound source flow proves the
body executes, include a zero-trip path. An unknown generic call returning `[]T` is not evidence
that there is at least one item. A dynamically sized array has an exhaustion edge.

The state after a loop joins reachable exhaustion/condition-false edges and `break` edges targeting
that loop. `continue` transfers to the iteration edge. Return and throw leave their enclosing
regions. A write immediately before `continue` reaches the back edge; a continue before that write
does not.

Consider:

```sol
func <T> lastOrDefault(values: []T, fallback: T): T {
    var result: T = fallback
    for val item in values { result = item }
    result
}
```

The entry value means `result` is assigned even if `values` is empty. If the initializer is removed,
the body assignment alone cannot justify the post-loop read. The same reasoning applies when a
break branch writes `result`: exhaustion remains another predecessor unless it is unreachable.

An inner loop's break exits only that loop. An inner-loop body write does not prove that the outer
loop ran, nor even that the inner loop had an item. A labeled transfer must retain the target
resolved by source binding; specialization cannot retarget it.

FIR verification solves a finite dataflow fixed point over concrete slots. Resource exhaustion is a
failure/defer result. It must not be “fixed” by dropping a back edge or assuming an uninitialized
slot is assigned.

## Exact ref flow

A `ref` actual is a writable, definitely assigned location. The source formal and actual types must
be exact after generic inference and substitution. A conversion or equal low-level register shape
does not satisfy exactness.

For `func <T> update(ref value: T, replacement: T)`, a call with `ref slot` may infer T from the
location type, subject to the rest of candidate inference. The callee can read the slot at entry and
can replace it. Call binding must retain the caller slot identity, target parameter index, passing
mode, and substitution.

After a ref call, stale flow refinements on the aliased mutable location may be invalidated. The
precise invalidation follows the ordinary aliasing contract. The ref edge is not a value copy and
must not be lowered as an unrelated temporary unless the ABI contract explicitly models the
copy-back semantics.

## Exact out flow

An `out` formal begins unassigned in its callee. Source flow rejects a read before a write and
requires each reachable normal return to assign the formal. A throw is not a normal return. If an
out call completes normally, the caller's actual becomes assigned; if control skips the call or the
call exits exceptionally, that call has not established a normal-return assignment fact.

At the call site, an existing out actual need not be assigned before entry. It must be writable and
have the exact selected formal type. The specialized verifier checks the concrete type, slot ID,
passing mode, call target, and normal successor. It must not accept only because the LLVM types
match.

The generic form `func <T> fill(out target: T, source: T)` does not infer T from `out var target`
alone. A second value argument, explicit type argument, or permitted expected-result evidence may
close T. The new local is created only after a unique candidate is selected, receives that
candidate's closed formal type, and is assigned after normal call completion.

No speculative local should leak from an ambiguous/rejected overload. A short-circuit or guarded
call assigns the out local only on the branch that invokes the call. The local must not be read on a
path that skips invocation.

## Nested generic calls

Every call has its own selected target and argument map. The inner generic arguments are inferred
from their own mapped source arguments, explicit arguments, constraints, and allowed expected-result
evidence. They appear in the inner declaration's ordinal order. The enclosing function's type
argument vector is not copied positionally.

When FIR builds a closed caller, it applies the caller substitution to the inner call tuple. For a
generic pass-through:

```sol
func <T> relay(value: T): T = identity(value)
func root(value: String): String = relay(value)
```

the call edge in `relay<String>` targets `identity<String>`. Source span and evaluation order stay
associated with the actual inner call. Exact recursive keys can refer to a reserved instance; a
type-growing chain must terminate with a resource diagnostic before partial FIR is certified.

The call's normal return type flows into the caller expression. Out/ref effects flow to the actual
slots through the call's normal/exceptional successors. An unresolved callee cannot leave behind a
successful caller return or assignment proof that depended on that call.

## Arrays and element flow

For `[]T`, substitution applies to the element type, array slot, index result, generic call result,
and loop item slot. Index evaluation and bounds-failure behavior remain represented. Assignment from
`values[index]` is complete only on the normal edge after the element read succeeds.

In `for val item in values`, the source-bound item type is T and the item is assigned at body entry.
The loop can execute zero times. Therefore, assigning some other local from `item` inside the loop
does not prove that local assigned after the loop unless the entry or all exits also establish it.

An empty array literal requires a target element type or another valid source inference path. Its
runtime emptiness does not infer T. A dynamic array is not known to be nonempty merely because one
runtime test used a nonempty value.

The current native ABI admits only a bounded set of closed arrays. A source-level `[][]T`, nullable
element, view, span, generic collection, or ref-like value needs separate layout, ownership, and
lifetime evidence. Closed T alone is insufficient.

## Open-type operations

Pass-through assignment/return can preserve T without operating on its representation. Arithmetic,
comparison, member lookup, conversions, indexing a T itself, formatting, and calls to nongeneric
functions may need concrete type information.

Such an operation must be supported by a declared constraint and source binding rule while T is
open, or by future complete per-instance source rebinding. The current implementation does not
perform that rebinding. It rejects the open-type-dependent operator/member/nongeneric-call surface
even if a particular eventual substitution might have made one candidate applicable.

This boundary avoids an unsound inference: replacing the token T in an already-bound body is not the
same as re-running overload resolution, member lookup, conversion selection, checked-effect
analysis, and flow for the concrete type. A future implementation must perform those steps and then
run the independent FIR verifier.

## What the FIR verifier must inspect

For each specialized body, verifier logic should derive its conclusions from the body and typed
slots rather than relying only on source summaries. At minimum it checks:

- every function type argument is closed, ordered, owner-correct, and satisfies constraints;
- every local, parameter, expression, and return type agrees with substitution;
- every read refers to a slot that is assigned on all reachable incoming normal paths;
- `val` writes respect the source write-once result represented in FIR;
- branch and loop successors have valid targets and correct ownership;
- break, continue, return, throw and fallthrough are distinct terminators;
- every normal return returns the declared result and satisfies out-formal completeness;
- ref reads are valid at entry and ref/out actuals match exact concrete slots and modes;
- an out caller-slot assignment is present only on the normal call successor;
- nested call type arguments are composed and agree with exact target instance keys;
- array item/index types and exceptional bounds paths match the admitted layout;
- source origins link each critical flow-affecting instruction to its declaration/call/expression.

A forged source flow summary, deleted call edge, omitted branch, or missing slot cannot be repaired
by setting a verified flag in serialized FIR. Corruption should produce verifier rejection (currently
the `SOL2402` family) with generic instance context and source origin where possible.

## Diagnostics and outcomes

Keep these failure classes distinct in logs and public status:

| Failure | Example | Meaning |
| --- | --- | --- |
| Source flow | Read local before write | Program violates language flow rules |
| Generic inference | `out var` is sole evidence for T | No unique closed substitution exists |
| Unsupported body surface | `value.length` for open T | Source body cannot enter current native subset |
| Unsupported closed ABI | `[][]int` specialization | Source facts may be valid; concrete layout is excluded |
| FIR verifier | CFG reads an unassigned slot | Compiler-created graph is inconsistent or unsafe |
| Resource limit | Instance/body budget exhausted | Graph not fully verified; no executable result |
| Runtime/backend | Verified graph emits or executes incorrectly | Later stage does not preserve verified semantics |

Current generic subset diagnostics include `SOL2400` for unsupported native shapes, `SOL2402` for a
specialization verifier rejection, and `SOL2403` for resource limits. A source-level DA/return/out
failure should use the existing source flow diagnostic and should not be disguised as a native
subset rejection.

Recovery may continue analyzing unrelated declarations. It cannot publish a successful instance
with an unsupported body, omit an edge, substitute an empty function, or fall back silently to a
generic runtime call.

## Reading the conformance fixture

[`GenericFirFlow.sol`](../../spec/examples/semantics/GenericFirFlow.sol) places scenarios in topic
groups. The oracle vocabulary distinguishes `SourceBound`, `SourceRejected`, `NativeAdmitted`,
`NativeRejected`, and `PlannedRebind`. Read those labels as contract outcomes, not as assertions
that a test suite has already executed them.

The fixture intentionally separates similar cases. For example, writes in both arms can satisfy a
join, while a body-only loop write cannot justify a post-loop read if zero iterations are possible.
A write before `continue` reaches the back edge, while a continue before the write skips it. A break
write does not remove a separate exhaustion edge. These distinctions are valuable even when the
closed generic type is a primitive.

Some source matrix entries show operations on unconstrained T to expose the boundary. They are not
positive programs until the language has the required constraint/rebinding semantics. A comment
describing a hypothetical `T=int` result is not permission for the compiler to treat the source
operation as already bound.

## Auditing a change to generic flow

When reviewing a generic flow change, trace one scenario through all stages:

1. Identify the source declaration and owner-scoped type parameter IDs.
2. Identify the unique selected call and its formal-to-actual mapping.
3. Record the closed ordered type tuple for the root generic function.
4. Compose it into each nested call argument before interning the target instance.
5. Compare source slot/expression types with their substituted FIR identities.
6. Enumerate normal, exception, return, loop exhaustion, break and continue CFG edges.
7. Recompute assignment at each join; do not infer iteration counts from runtime data.
8. Check exact ref/out location type, mode, caller slot, and completion successor.
9. Confirm that unsupported open-T operations failed before verified FIR.
10. Confirm that the verifier rejects malformed or forged graph data independently.
11. Confirm backend/runtime behavior for the precise claimed ABI and artifact.
12. Describe source validity, FIR verification, and runtime validation separately.

Questions to ask:

- Can a `val` receive two writes on one path after the new CFG transform?
- Does a no-else branch leave the incoming state on its false edge?
- Does an early return remove only the normal predecessor it actually exits?
- Does the exceptional path carry state from before the failing operation?
- Does a `continue` bypass statements that follow it in the body?
- Does a loop keep its zero-trip edge when its iterable is a generic call result?
- Does an inner break target the correct loop after cloning?
- Does `out var` become visible only after unique candidate selection?
- Is `out` assignment established only after normal completion?
- Does a nested call infer its own tuple in its own declaration order?
- Could a concrete substitution change member or overload applicability?
- Could unsupported array layout, nullability, or ref-like lifetime invalidate the clone?

If an answer is unknown, the affected instance is not ready for a native-success claim.

## Common review mistakes

**Treating a source summary as a verifier certificate.** A source analyzer can prove a generic
declaration's own assignment rules. The FIR verifier still has to inspect the closed slots and CFG
created for one instance. The summary is valuable provenance, but it does not certify a mutated or
malformed body.

**Treating substitution as rebinding.** Replacing T by int in an expression tree does not redo
member lookup, overload ranking, implicit conversions, constraint dispatch, or effect analysis.
The current gate rejects constructs whose validity depends on that missing work.

**Treating a runtime input as a static fact.** A test that always passes a nonempty array does not
remove the zero-trip edge from the generic function. A flag value observed at runtime does not
make the other branch unreachable in the CFG.

**Joining every exit together.** Return, throw, break, and continue have different destinations.
For example, a return inside a loop exits the function; it does not assign a slot for the path where
the loop exhausts without entering its body. An exception snapshot does not satisfy an out
parameter's normal-return rule.

**Using representation equality for exact ref/out.** Equal LLVM pointer or integer types do not
prove equal Sol types, correct source location identity, definite assignment at ref entry, or
correct out completion. Check the type IDs and passing mode before lowering.

**Creating out-var during candidate probing.** Candidate-local inference can fail or tie. The
compiler should commit the new variable only after one candidate wins and its formal type is closed.
Otherwise a rejected candidate can leak a symbol or influence another candidate's inference.

**Assuming arrays are nonempty after closure.** Substitution can identify an element type and ABI
layout. It cannot infer a particular runtime length. Static literal-shape facts are separate and
must be explicit in source-flow analysis.

**Sharing an instance as if it shares a call site.** Instance interning may point two calls at one
body, but their expression order, spans, data dependencies, effects, and branch placement remain
distinct. The verifier must validate each edge independently.

## Keeping documentation honest

When an implementation change adds one case, update the status of that exact case. Do not promote
the whole matrix from “planned” to “supported” based on one `T=int` example or one successful
backend artifact. Record the admitted closed types, call forms, control constructs, and artifact
target separately.

For every new positive claim, point to the source rule, the FIR invariant, the relevant verifier
path, and the runtime/backend evidence that supports the claim. A source-only fixture is useful for
normative semantics but does not prove code generation. A verifier-only test does not prove source
binding. A runtime example does not prove that rejected control-flow paths are safe.

For every rejection, say whether it is a language error, a missing generic inference result, a
current native-subset boundary, a malformed FIR graph, a resource limit, or a backend/runtime
failure. The user-facing diagnostic should match that stage. In particular, `SOL2400` must not be
described as proof that the language forbids the source construct when the limitation is current
native lowering.

When a case changes from `NativeRejected` to `NativeAdmitted`, preserve the old negative case with a
more precise unsupported shape if the general boundary still exists. This keeps the matrix useful
for preventing accidental widening, especially around nested arrays, nullable values, ref-like
storage, and operations on open T.

## Source, FIR, and execution are different claims

Use precise status wording:

- “Source analysis accepted the generic declaration and selected this call” describes a front-end
  outcome.
- “FIR contains a closed instance and its CFG/slots passed the verifier” describes a compiler proof.
- “The AOT/ORC artifact returned this value for these inputs” describes observed runtime behavior.
- “Per-instance source rebinding is implemented” is a broader semantic claim and is not true merely
  because the first three statements hold for pass-through examples.

When reporting a failure, state its layer. `SOL2400` at the native admission boundary is not proof
of a source language error. A source DA diagnostic is not a backend failure. A verifier rejection
should not be translated to an unsupported-source diagnostic unless the source contract actually
excludes the construct.

## Completion criteria

Full generic flow through native FIR requires more than CFG construction. Completion includes:

- fresh per-instance semantic contexts with simultaneous, owner-aware substitution;
- rebinding every operator, member, index, conversion, and overload affected by the concrete type;
- recomputing assignment, nullability, returns, effects, ref/out, borrow and exception flow;
- independently verifying closed slots, call modes, origins, CFG edges and normal-exit rules;
- finite and deterministic nested specialization under count, depth, body-size and cancellation limits;
- correct AOT and ORC behavior for the admitted scalar/array/ref/out cases;
- diagnostics and serialization that cannot publish a partial graph as verified;
- conformance scenarios for positive, negative, recursive, branch and loop behavior;
- release documentation that accurately scopes the tested source/ABI/artifact subset.

Until then, describe the current state as: **FIR builds specialized generic CFGs and verifies closed
slots after substitution; complete per-instance source semantic rebinding remains unfinished.**
