<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Automatic generic member body analysis

A selected member signature does not prove that its original body remains valid when the owner and
method arguments close. The compiler now requests this body evidence automatically during source
analysis. The normative contract is in [GenericMemberBodyCalls.sol](../../spec/GenericMemberBodyCalls.sol).

For example, a method on `Root<T>` can select `choose(Object)` while `T` is open, but select
`choose(Widget)` when its owner becomes `Root<Widget>`. The closed body must repeat that selection.
If the two overloads have different results, an inferred member result must be computed before an
enclosing source call is ranked or its expected type is checked.

## Phase ownership

The source binder continues to own accessible member discovery, receiver substitution, argument
mapping, generic inference, and overload selection. It retains the original source receiver as well
as the constructed declaring owner. `Leaf<Widget>` and its inherited `Root<Widget>` are separate
facts even though both refer to the same selected method body.

For an inferred return on a closed top-level generic owner, the binder requests a closed body view
while computing the candidate's result. This request uses the completed owner and method vectors.
The closed result is available to enclosing calls and inferred local/function results. A private
source/type workspace makes the query available before the final `BodyAnalysis` is published.
No unfinished analysis snapshot is returned to an API consumer.

While a method's arguments are still being inferred, a separate owner-closed return template keeps
that method's own parameter identities open. Its source body can supply expected-result evidence
under the closed owner. This template is not a completed closed body proof and does not enter the
successful body cache. Method constraints are checked after their argument vector is solved. This
prevents an overload chosen only in an open owner body from rejecting a valid closed candidate
before method inference finishes.

Recursive calls read the callee's original signature graph. Captured outer parameters keep the
caller substitution, while the callee's owner and method vectors replace its own parameters once.
For example, `hop<int>` calling `hop<long>` must check the nested argument against `long`; the
current caller's `int` must not rewrite the callee's method parameter before that step.

An inferred-return request that repeats the same active instance cannot infer a new result by
assuming that result. It stays unresolved. The inference stack has a 128-instance bound and each
source inference workspace permits 1,024 uncached body/template attempts. Completed cache hits do
not consume another attempt. This bounds repeated invalid requests as well as distinct instances. A type
specialization failure or cancellation never installs a successful body cache entry. Cancellation
propagates to the caller. A source return annotation can close a recursive signature independently
of this return-inference stack.

After ordinary bodies are bound, the compiler collects selected member calls that need a generic
owner body proof. A separate bounded dependency graph checks the selected closed instances. The
same graph check runs for member calls that appear after a free function is specialized.

Generic free-function calls reached through those bodies also enter the dependency graph, including
eligible stateless object methods. A member body cannot be certified solely because a generic free
callee's signature matched: the callee's closed body and its outgoing generic dependencies must be
checked too. Finite recursion can cross these callable boundaries without recursively starting a
second independent proof analysis.

Locally bound free-function snapshots and dependency-checked snapshots use separate caches. The
dependency pass works on a copy of immutable local facts, so an invalid transitive dependency neither
mutates a reusable local snapshot nor enters the successful dependency-checked cache. Generic local
functions that require an enclosing lexical instance retain an explicit gap.

## Instance graph

One graph node identifies the original source method, its canonical constructed declaring owner,
and its ordered method argument vector. Owner arguments and method arguments never share a spelling
based key. Two distinct source receivers may share the same body instance while preserving their
own receiver projection paths and source call origins.

Each node rebinds its original body and records selected member-call dependencies. Local errors,
deferred source nodes, and dependency results remain distinct. Failure propagation uses the order
`Bound` → `Deferred` → `Invalid`: an invalid dependency invalidates its callers, and a deferred
dependency prevents a locally bound caller from becoming complete.

The entire finite graph is discovered before statuses propagate. An annotated cycle whose local
bodies and outgoing dependencies are all bound is a complete source proof. An error or missing body
reachable from a cycle propagates around the cycle and to callers outside it. A recursive reference
in the immutable proof tree identifies another already checked instance; it does not assert that
the function terminates at runtime.

The graph expands at most 128 distinct dependency levels, 1,024 canonical body instances, and 8,192
generic-call dependency edges, including free-function bridges. A
repeat visit to an existing instance is a graph edge and does not consume another instance. A chain
that changes its type arguments on each recursive step still consumes new instances and therefore
cannot evade these bounds. Exhaustion produces deferred evidence, never a truncated successful
proof. These are separate limits from constructed inheritance projection's 128 edges, 8,192 path
states, and 128 paths per owner candidate.

## Published source facts

An incomplete selected call is present in `UnverifiedNodes` and makes the analysis incomplete. Its
expression and containing expression/body statuses are updated. Enclosing lambda and anonymous
function summaries lose their complete flag. The public arrays and lookup methods expose the same
updated records in the same source order. A sibling body outside the affected source subtree keeps
its original status.

Closed body diagnostics keep their original source origins and are deduplicated and sorted under
the existing diagnostic limit. A source error is not replaced with a successful signature proof.
Specialized free-function views apply the same state propagation to their expression/body snapshots
before being considered for the successful-instance cache.

Open owner or method vectors, missing implementations, and nested generic owners do not publish a
closed body proof. A discrepancy between the selected source result and the closed body result also
stays deferred; the compiler cannot reuse a mismatching source signature as proof.

## Runtime boundary

These results prove source binding and supported source flow obligations. Generic owner layout,
virtual/interface dispatch, receiver ABI, constructor publication, runtime type metadata, exception
cleanup, and Yellow Tea integration require independent FIR/runtime work. The generic owner/member
native gate remains `SOL2400`. A bound source body does not grant AOT, ORC, or artifact admission.
