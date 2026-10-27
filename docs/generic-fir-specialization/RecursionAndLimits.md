<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Generic FIR instantiation graphs, recursion, and limits

This page explains how a selected generic call becomes a closed FIR function instance, how recursive
calls share or expand that instance graph, and how resource limits stop unbounded work.

## Implementation status

The current FIR builder implements exact-key reuse and a bounded FIFO worklist for a limited set of
module-level generic free functions. It reserves one local FIR function ID when it first sees a
closed key, stores the key before queuing the body, and returns that reserved ID when a later call
requests the same key. This supports the graph-construction mechanism for exact-key recursion.

The builder enforces three current specialization caps:

| Resource | Current cap | What is counted |
| --- | ---: | --- |
| Distinct generic function instances | 4,096 per FIR build | Unique source declaration plus ordered closed argument tuple |
| Specialized source-body AST nodes | 16,000,000 per FIR build | Unique AST nodes visited for each distinct instance body, summed across instances |
| Structural type argument depth | 128 levels | Nested argument/effect structure visited by the current type-argument check |

The three caps measure different work. The 128-level type limit is not a 128-call recursion limit.
There is no separate current maximum active graph path length. A shallow chain of distinct keys is
bounded by instance and body-node counts, while one deeply nested type can reach the type-depth cap
with few instances.

The current queue and key map do not provide a persistent cache, cross-process identity, or canonical
IDs across equivalent inputs whose declaration enumeration changes. Full per-instance source
rebinding, the complete source generic contract, and every graph guarantee on this page are not
implemented by this initial subset. A documented normative case should not be read as evidence that
the compiler already accepts or lowers the corresponding source program.

## The graph model

A specialization graph is a finite directed graph rooted in the ordinary functions selected by one
artifact build policy. Each generic instance is one node. Each selected generic call from an
ordinary function or an instance body is one directed edge. The edge keeps its source call origin,
argument mapping, selected declaration, and target type tuple.

One source call can request an instance that already exists. That call still creates a new origin-
bearing edge, but it does not create another node or body. Several roots can converge on one node.
Cycles are normal graph shapes: they represent recursive call relationships and do not imply that
the source program terminates when executed.

The graph is rooted and closed only when all of these conditions hold:

1. Every reserved key is complete and closed.
2. Every reserved key has exactly one body-construction result.
3. Every selected generic call has one exact target key.
4. Every target is part of the artifact's reachable closure.
5. The body and all required outgoing edges pass source, FIR, and backend verification.
6. No required work remains queued, reserved, building, failed, or cancelled.

An empty queue is not sufficient proof of closure. A missing target or a failed node prevents the
artifact from being considered successful. There is no successful state for a graph whose work was
truncated.

## What makes an instance key

The semantic instance key is:

```text
canonical generic declaration identity
+ complete ordered tuple of closed type identities
```

The declaration identity distinguishes overloads and declaration owners. A callable name, source
line, displayed signature, generic arity, overload index, file path, or parameter spelling is not a
substitute for that identity.

The tuple follows the generic declaration's parameter ordinals. Source argument order, named
argument order, inference discovery order, hash-table iteration order, and the caller's parameter
order do not reorder it. Every declared generic parameter participates, even when the function body
does not use that parameter.

For example, for `func <A, B> first(value: A, ignored: B): A`, these are different keys:

```text
first<int, String>
first<int, byte>
```

The body returning only `value` does not erase `B` from the function signature, constraints,
metadata, or key. Likewise, reversing the types produces a different tuple:

```text
first<int, String>
first<String, int>
```

Type identity is structural within an owned analysis workspace. Nominal declaration identity,
generic argument order, array rank, tuple order, nullability, function passing modes, results, and
effect members participate wherever they are part of the type. Effect members follow the language's
set identity rules rather than accidental insertion order.

The key cannot be formed until source analysis has selected one candidate, completed its argument
mapping, inferred a complete tuple, and proven its constraints. An ambiguous call, an inference
failure, an unresolved parameter, or an open type is not a partially valid instance.

## Exact-key recursion

When an instance is requested for the first time, its key is reserved before its body is traversed.
The body is then queued. If a recursive call in that body requests the exact same declaration and
closed tuple, lookup finds the already reserved key. The recursive edge points to that node. It does
not reserve a second body.

For example:

```sol
func <T> repeat(value: T, again: bool): T {
    if (again) {
        return repeat(value, false)
    }
    return value
}
```

Starting from `repeat<int>` yields one instance node and one self-edge. The graph builder does not
evaluate the source condition and does not prove runtime termination. Ordinary flow analysis,
recursion semantics, effects, and the target ABI remain separate checks.

This reservation-before-body rule is essential. If an implementation only inserts a key after the
body finishes, exact recursion repeatedly appears new and can recurse without bound during
compilation. In a parallel builder, map insertion and queue ownership need a synchronization rule so
only one worker owns a new key's body.

## Mutual recursion

Mutual recursion uses the same key rule. No function-name stack or special-case cycle breaker is
needed. Suppose `left<T>` calls `right<U>`, and `right<U>` calls `left<T>` with the same concrete
argument. Starting at `left<int>` creates two nodes:

```text
left<int>  ──calls──▶ right<int>
   ▲                    │
   └────────calls───────┘
```

When the second body requests `left<int>`, it resolves to the original reserved node. The graph has
one two-node strongly connected component. The parameter identities named `T` and `U` are distinct;
the selected call edges map them independently to `int`.

A cycle can have more nodes if its edges change the argument tuple. Cycle detection by function name
alone is incorrect. For example, `f<int>` and `f<String>` are different nodes even when both bodies
call the same declaration. A cycle is closed only when an edge returns to a key already in that
component, not merely when it returns to the same source declaration.

If one node in a recursive component fails substitution, body construction, edge resolution, or FIR
verification, dependent nodes cannot be considered verified. A completed local body inside a
failed component is provisional; it cannot support a successful artifact on its own.

## Distinct arguments and type-growing recursion

The same declaration can produce many nodes:

```text
identity<int>
identity<String>
identity<int?>
identity<[]int>
identity<[][]int>
```

Each admitted tuple has a distinct key. The native backend may reject some of these types under its
current ABI subset, but rejection cannot merge them or substitute Object, bottom, or an opaque
pointer to save work.

Type-growing recursion can generate:

```text
grow<int>
grow<[]int>
grow<[][]int>
grow<[][][]int>
...
```

Each step changes the closed tuple. Exact-key reuse does not stop this sequence. A compiler must
continue until source rules or an explicit resource boundary rejects more work. It must not declare
the graph closed after seeing a function name once.

The current implementation has two relevant broad limits for such a chain: 4,096 distinct
instances and 16,000,000 accounted body AST nodes. Its structural type-argument check also rejects
arguments beyond 128 visited levels. Which limit occurs first depends on the source and its type
growth. The emitted `SOL2403` should identify the dimension that actually blocked the request.

The normative resource appendix also describes a separate active graph path limit of 128 edges from
a root. That graph-path limit is not implemented merely because the current argument-type check uses
the number 128. Until a separate path counter exists, do not report a long shallow call chain as
already stopped by a recursion-depth limit.

## Worklist and queue order

The key table deduplicates work; the FIFO queue schedules each new key once. A repeated key adds an
edge and returns the existing node. It does not append a duplicate work item, charge a second body,
or increment the distinct-instance count.

The current builder first reserves and builds ordinary functions in declaration enumeration order.
While lowering those functions it encounters selected generic calls and enqueues new keys. It then
drains a FIFO queue. Each built generic body can enqueue more keys. For the same source snapshots,
options, and traversal order, this gives repeatable discovery and local ID assignment.

Current traversal order is not a canonical order across reordered inputs. Ordinary function IDs and
generic discovery sequence may change if declarations, files, or calls are enumerated differently.
Those local integer IDs are internal references in one FIR graph. They do not change the semantic
meaning of the selected calls.

A complete canonical scheduler should seed roots in semantic declaration order, visit outgoing
edges in defined source evaluation order, and use stable declaration and tuple tiebreakers for
generated edges. It should normalize or commit worker results by canonical key so thread scheduling
cannot affect graph content, diagnostics, or resource outcomes. If IDs are shown in dumps, normalized
dumps should distinguish semantic identity from the local reference number.

Source evaluation order and type-argument tuple order are separate. For a named call with arguments
written in a different order, the call edge preserves its formal-to-actual mapping and evaluation
order. The instance tuple still follows the callee's generic parameter order.

## Local IDs, type IDs, and cache identity

The current key is an in-memory pair of an `AstFunction` object reference and an invariant-culture,
comma-delimited list of workspace-local `ResolvedType.Id` values. This works only within the builder's
own analysis snapshot and lifetime. The type ID is a fast local handle, not a stable semantic name.

Do not persist a local function ID, type ID, process pointer, hash value, printed signature, or
displayed type string as a specialization-cache identity. Different analysis workspaces can assign
different numeric IDs to semantically equal types. Two overloads can also print the same display
signature while remaining different declarations.

A future persistent cache needs a canonical declaration identity and an unambiguous structural
encoding of every ordered type argument. It also needs semantic dependency fingerprints and relevant
language, target, ABI, and backend versions. A hash can accelerate lookup, but the cache must compare
the complete identity after a hash match. A cache hit never replaces current source checking,
constraint validation, graph closure, or independent FIR verification.

The present implementation has no persistent specialization cache and no cross-container generic
instance ABI. A `.solib` consumer cannot reuse an integer ID from a producer build or infer a generic
export from matching display text. Stable generic import/export is a separate artifact contract.

## Current budgets and their owners

The FIR generic builder currently enforces:

| Cap | Scope | Owner | Status |
| --- | --- | --- | --- |
| 4,096 distinct instances | One FIR build | Generic specialization map/worklist | Implemented |
| 16,000,000 specialized body AST nodes | Sum across distinct instances in one FIR build | Specialized body traversal | Implemented |
| 128 structural type levels | One argument under current supported-type traversal | Generic argument admission | Implemented |
| 128 active graph edges | One root-to-instance construction path | Dedicated graph depth counter | Normative; not implemented |

The instance count is based on unique keys, not call-edge count. Multiple roots and repeated calls to
one key produce more edges but one node. Distinct closed tuples produce distinct nodes. The body-node
budget charges each distinct instance body once, even when internal immutable source syntax can be
shared. This reflects logical specialization work rather than memory allocation strategy.

Limits are hard correctness boundaries. Once the next unit would exceed a cap, the compiler fails
the graph. It cannot truncate the queue and report success, emit an empty placeholder body, omit an
edge, choose a less-preferred overload, or return an artifact that depends on a missing instance.

## `SOL2403` resource diagnostics

`SOL2403` identifies generic FIR specialization work that cannot continue within a hard resource
limit. The current builder uses it for the generic instance, specialized body-node, and
specialization type-depth limits. The diagnostic should point to the source call that requests the
rejected work, with a declaration origin only as a fallback.

A useful diagnostic identifies the exhausted dimension and configured cap. It may include an
observed count or the attempted next unit and a bounded instantiation trace in source terms. For
example, it can show `growByArray<int>` followed by `growByArray<[]int>`. It must not expose a
workspace-local numeric type ID, absolute local path, pointer, cache directory, or machine-specific
identifier.

Duplicate requests for an already reserved exact key do not repeatedly report the limit. Once
closure has failed, dependent errors can be summarized, but at least one primary resource diagnostic
must remain visible. A truncated diagnostic list must not conceal the fact that the artifact failed.

Current diagnostic wording can be refined, but tooling and tests rely on the stable `SOL2403` code
and its resource-limit meaning. A separate analysis/interner diagnostic should not be relabeled as a
FIR instance overflow merely because both occurred while processing a generic call.

## `TypeSpecialization` and inference budgets are separate

Three phases own independent resource domains:

### Source generic inference

Inference decides which candidate is applicable and what complete substitution it has. Its own
limits bound candidate work, structural matching, projections, constraint graphs, lambda fixed
points, and body analysis. The current code contains multiple 128-level or 128-pass bounds and
other bounded graph/fact traversals. They belong to the operation that performs inference.

If inference cannot produce one selected complete candidate, FIR receives no key to reserve. A
resource-limited or deferred inference result is not a partially selected instance and cannot be
fixed by substituting a guessed type in the graph builder.

### `TypeSpecialization` interner

`TypeSpecialization` extends one `TypeAnalysis` with an owned synchronized type interner. Its default
capacity is 100,000 additional interned type instances and its default structural rewrite depth is
128. Callers may lower these values within the API constraints. The workspace checks that every type
belongs to the owning analysis/interner; numeric ID equality from another workspace is insufficient.

This budget counts type construction and rewriting. It does not count generic function instances,
specialized source bodies, call edges, or machine code. Interning 100,000 types does not create
100,000 function bodies. A type interner failure is reported by the source-analysis operation that
required the type.

### Generic FIR specialization

The FIR graph owns its separate 4,096-instance, 16,000,000-body-node, and 128-structural-level
limits. The counters reset at the FIR compilation boundary specified by the builder. They neither
consume nor replenish the `TypeSpecialization` capacity or inference budgets.

Equal numeric limits do not make two counters interchangeable. An inference depth of 128, a type
rewrite depth of 128, a generic argument depth of 128, and a planned graph path depth of 128 measure
different structures, have different owners, and may fail at different source locations.

## Cancellation and publication

Cancellation is a distinct result from invalid source and resource exhaustion. A graph builder checks
the token before root processing, each worklist dequeue, body traversal, substitution and constraint
operations with potentially unbounded work, verifier traversal, and artifact publication. Shared
operations also check at their documented polling intervals.

After cancellation is observed, the builder stops accepting reservations and prevents workers from
publishing new bodies. It releases owned synchronization and discards provisional graph state. If a
native or external operation cannot be interrupted, the host waits for its safe boundary and still
suppresses success.

A partially built recursive component is never Verified. A reserved target referenced by a recursive
back edge is not an executable body until graph closure and verification succeed. Cancellation does
not produce `SOL2403`, a normal source failure, a successful FIR dump, a persistent cache entry, or a
partially replaced output file.

Independent compilations keep separate mutable queues, key tables, counters, diagnostics, and
staging outputs. Immutable source/type facts can be shared only under their ownership rules. A
cancelled build must not corrupt another active build.

## Debugging a graph issue

When a recursive generic case behaves unexpectedly, inspect it in this order:

1. Confirm the source call has one selected declaration after normal overload resolution.
2. Read the declaration's canonical owner and complete ordered type-argument tuple.
3. Check that every argument is closed, belongs to the current analysis workspace, and satisfies
   all declaration constraints.
4. Find whether the key was already Reserved, Queued, Building, or Built when the edge was created.
5. Compare the edge's target key, argument mapping, and call origin with the selected source call.
6. Check distinct-instance, body-node, and structural type counts independently.
7. Inspect the queue sequence, remembering that current local IDs follow current input traversal.
8. Verify all reserved nodes and targets after the worklist drains.
9. Run the independent FIR verifier before interpreting any LLVM or artifact result.
10. Confirm cancellation and resource failure did not publish partial state.

For a type-growing chain, print shortened structural type terms rather than numeric IDs. For a
mutually recursive cycle, show nodes and edges explicitly. For two same-spelled overloads, show the
canonical declaration identity that owns each key. If the only evidence is that two FIR numbers
match, the identity diagnosis is incomplete.

## Conformance cases

The source scenarios are collected in
[`GenericFirRecursion.sol`](../../spec/examples/semantics/GenericFirRecursion.sol). A complete graph
suite should cover at least these cases:

| Case | Required observation |
| --- | --- |
| Repeated exact calls | One target node, one body charge, separate call edges |
| Direct exact recursion | One node and a self-edge |
| Mutual exact recursion | One node per declaration and tuple; closed strongly connected component |
| Different concrete types | Separate nodes for each ordered tuple |
| Unused type parameter | Still part of the key |
| Named argument reordering | Declaration tuple order and source evaluation mapping both preserved |
| Type-growing recursion | New key per changed tuple until a named hard limit fails |
| 4,097th distinct key | `SOL2403`; no successful truncated closure |
| 16,000,000 body-node boundary | Per-instance accounting with repeat-edge deduplication |
| Structural depth beyond 128 | Type-depth failure, not graph path failure |
| Type interner overflow | Failure owned by `TypeSpecialization`, separate from FIR instance count |
| Inference budget failure | No selected candidate and no reserved graph key |
| Reordered input files | Same normalized semantic graph under the normative canonical scheduler |
| Cancellation in a cycle | No Verified component and no artifact/cache publication |
| Hash collision | Full key comparison keeps different declarations/tuples separate |
| Foreign workspace ID | Ownership validation rejects numeric equality as identity |

Tests should inspect graph nodes, edges, keys, origins, body counts, diagnostics, and verifier
results. Parser acceptance alone, an inference success, an emitted local ID, or a native executable
alone is not enough to establish all graph invariants.

## Current support versus normative contract

| Behavior | Current implementation | Complete contract |
| --- | --- | --- |
| Exact declaration-plus-argument lookup | Source object identity and local type IDs in an in-memory key | Canonical declaration and structural type identity |
| Exact-key recursive reuse | Reservation before body queueing; duplicate key returns reserved ID | Required for every admitted closed recursive key |
| Mutual cycles | Same queue/map mechanism can close exact-key edges | Required with component-wide verification |
| Distinct closed argument tuples | Different local IDs yield different keys | Structural tuple identity independent of local numbering |
| FIFO worklist | Implemented for fixed current traversal | Canonical semantic order, independent of incidental file enumeration |
| Instance/body/type limits | 4,096 / 16,000,000 / 128 | Same initial caps plus separately specified graph-path budget |
| Persistent generic cache | Not implemented | Requires canonical versioned semantic cache identity |
| Full source body rebinding | Not implemented for every open-type operation | Required before general generic body support |
| Cancellation-safe graph publication | Token checked during builder operations; full transactional guarantees remain normative | No partial verified graph, cache entry, or artifact on cancellation |

Status changes should be based on implementation and verifier evidence for the individual row. Exact
key reuse does not imply a canonical scheduler. A bounded queue does not imply all source bodies can
be rebound. A successful recursive native call does not establish a persistent cache identity.

## Reading graph dumps and diagnostics

When a FIR or diagnostic dump shows an integer target, interpret that integer only inside the current
dump. Resolve it to the node's declaration and ordered closed type tuple before comparing it with a
second build. A useful normalized dump includes both the local reference and a source-rendered key.

For each node, inspect:

- the canonical source declaration selected by body analysis;
- the complete tuple in declaration parameter order;
- whether the node is ordinary or a generic instance;
- its lifecycle state during a diagnostic or verifier trace;
- its root reachability and incoming/outgoing edges;
- the body charge and structural type depth;
- any constraint, source-binding, or backend admission result.

For each edge, inspect the original call origin, selected declaration, generic type arguments,
formal-to-actual mapping, and target key. A pair of calls to one node should still display two edges.
If a named call's source argument order differs from the formal order, both orders should be visible
without changing the target tuple.

For a cycle, show a bounded path that ends at a repeated full key. Repeating only a function name is
not enough to show exact recursion. The trace should make clear whether the last edge is a self-edge,
a mutual-cycle back edge, or a new key with a different tuple. A deeply growing chain should be
shortened with a count and boundary key instead of printing an unbounded diagnostic.

For `SOL2403`, first identify the budget dimension, then the owning phase and count. If the error
occurred before overload selection, it belongs to inference or type construction and no graph key
exists. If it occurred while reserving a new key or accounting an instance body, it belongs to FIR
specialization. Similar numbers in two phases do not identify the same resource.

## Resource-limit policy for callers

The current caps are correctness guards rather than project tuning knobs. Source annotations,
environment variables, a cached artifact, or a command-line flag cannot silently raise them. A lower
host policy may be added if the compiler contract states the resulting limit and diagnostic behavior.
Raising a normative cap requires an intentional contract change and dedicated bounded regression
coverage; it should not happen as a side effect of changing an interner constructor default.

A caller should receive a terminal resource result rather than a truncated graph. If tooling offers
an explanation mode, it can report the key chain and exhausted cap without executing the source
program or generating a partial native artifact. This diagnostic path remains subject to the same
bounded rendering rules as compiler errors.

## Maintainer checklist

Before changing graph construction, answer these questions in the implementation review:

- Is the source target selected before a key is formed?
- Are the complete generic arguments in declaration order?
- Does every key include all declared parameters, including unused ones?
- Are type and declaration identities valid in one ownership domain?
- Is a new key reserved before any body traversal can discover a recursive edge?
- Can duplicate requests enqueue or account one key more than once?
- Are self and mutual cycles represented as ordinary exact target edges?
- Does a changed type tuple create a different node even for the same declaration?
- Does a type-growing chain stop only at a named source or resource boundary?
- Are all current budget units and ownership boundaries explicit?
- Does `SOL2403` identify the actual exhausted dimension and source origin?
- Are inference, interner, and FIR graph counters kept independent?
- Can input enumeration or worker scheduling change semantic graph content?
- Are local IDs prevented from becoming persistent or cross-workspace identity?
- Does cancellation discard the whole provisional closure and suppress publication?
- Does the independent verifier check exact targets, reachability, and cycle members?
- Does failure in one required node prevent artifact commit?

If any answer is unknown, record the limitation accurately. Do not describe a planned graph guarantee
as implemented because the current subset happens to pass one small example.
