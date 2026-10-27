<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Generic storage and Yellow Tea roots

This guide connects a concrete generic type argument to native storage, descriptors, barriers,
stack maps and process ownership. The full contract is in
[`GenericFirStorageAndRoots.sol`](../../spec/GenericFirStorageAndRoots.sol); its source matrix is in
[`GenericFirStorageAndRoots.sol`](../../spec/examples/semantics/GenericFirStorageAndRoots.sol).
The wider specialization boundary is described by the
[generic FIR guide](../generic-fir-specialization.md).

## Current status

Development builds admit a narrow native specialization slice for direct calls to module-level
generic free functions. The selected type arguments must be closed and supported by verified FIR.
The current type set includes selected primitives, `String`, and one-level arrays whose elements
are selected primitives or `String`. Bodies are constructed from already-bound source facts; the
compiler does not perform complete per-instance source rebinding.

The native runtime has provisional object, String and array headers, exact descriptor scan shapes
for the generated bootstrap subset, registered shadow roots, cooperative polls, and insertion
barriers. Its descriptor layout is still provisional version 0. The
[current Yellow Tea inventory](../yellow-tea/current-implementation.md) describes those components
and their unfinished limits. They do not establish a stable runtime ABI, full generic layout support,
or a latency guarantee.

Generic nominal layouts, arbitrary nullable types, nested arrays, generic collections, spans,
ref-like storage, broad closures, and general borrow analysis remain outside the native generic
slice. The generic FIR verifier checks the admitted source/type/call facts; it does not yet prove the
complete layout, descriptor, root-liveness, or borrow contract described here.

## Follow the concrete type through the pipeline

A source-level `T` does not have one universal machine representation. For each selected closed
instance the compiler must retain and use this chain:

1. **Source identity:** bind the exact generic parameter owner and ordinal, constraints, nullability,
   ref-like status, and selected declaration.
2. **Substitution:** replace every occurrence of that parameter simultaneously without removing
   nested constructors, array element nullability, effects, or passing modes.
3. **Representation:** classify the resulting concrete value as scalar, managed reference,
   inline aggregate, nullable form, tagged payload, or scoped ref-like view.
4. **Layout:** determine target size, alignment, field offsets, array stride, nullable tags, and
   scan shape from that concrete representation.
5. **FIR proof:** verify that the selected generic body and call graph have the same concrete
   signature and that every instance is reachable and unique.
6. **Root plan:** compute which concrete references are live at each allocation, loop poll, call,
   exception edge, and native transition.
7. **Barrier plan:** classify stores to heap fields, arrays, aggregate members, root slots, and
   collection storage using the same concrete descriptor.
8. **Artifact contract:** confirm that the selected AOT, ORC, library, or container path supports
   the resulting ABI and runtime ownership.

These steps are related but separate proofs. Type substitution alone does not decide field offsets.
A descriptor alone does not prove that a stack reference is live. A root slot does not legalize an
escaping span. An artifact that links does not certify source-level overload selection.

## Representation matrix

| Closed type argument | Value category | Scan obligation | Current generic FIR |
|---|---|---|---|
| `int`, `long`, `single` | Scalar | No managed edge in the value bits | Admitted when body/ABI shape is supported |
| `String` | Managed reference | Root the object and follow its String descriptor | Admitted for the supported subset |
| `[]int` | Managed array reference | Root array; skip scalar element payloads | One-level array support |
| `[]String` | Managed array reference | Root array and scan String element slots | One-level array support |
| `[]String?` | Array with nullable reference elements | Root array; scan non-null slots | Rejected pending nullable element layout |
| `([]String)?` | Nullable array owner | Root owner when present; element type stays non-null | Rejected pending nullable representation |
| `[][]int` | Nested managed array | Root outer array and scan inner-array slots | Rejected |
| `Box<String>` | Constructed nominal type | Scan fields according to substituted layout | Rejected; generic owner layout is outside slice |
| `Pair<String,int>` | Inline value with a reference field | Recursively scan the String field | Rejected pending aggregate layout proof |
| `Span<String>` | Scoped ref-like view | Keep managed origin owner alive; preserve borrow scope | Rejected |
| `Span<int>` over stack memory | Scoped ref-like view | Prove stack origin outlives every access | Rejected |
| `Vector<String>` | Managed collection | Scan active reference entries and owner | Type/API implementation pending |
| `ReadOnlySpan<String>` | Read-only scoped view | Same owner lifetime as Span, with no write route | Rejected |

The current admission column is not a statement about all runtime paths or all Sol syntax. A source
type may be well-formed, inferable and constraint-satisfying while remaining unavailable in FIR.

## Nullable arrays have two independent positions

Sol's array type spelling distinguishes element nullability from container nullability:

| Type | Container nullable | Element nullable | Root and scan behavior |
|---|---:|---:|---|
| `[]String` | No | No | Non-null array owner; each element is a String reference. |
| `[]String?` | No | Yes | Non-null array owner; each element slot may be null. |
| `[](String?)` | No | Yes | Same nullable-element shape as `[]String?`. |
| `([]String)?` | Yes | No | Optional array owner; present elements are non-null Strings. |
| `([]String?)?` | Yes | Yes | Optional owner plus nullable element slots. |

The `?` after `String` applies to the element. Grouping the complete array before `?` applies to
the owner. Parentheses around the element type preserve the same array shape. Neither the analyzer
nor backend may collapse these cases into “nullable array” as a single bit.

A nullable reference element remains a reference edge when it is non-null. A nullable value element
may require a presence tag and a conditional nested scan program. The current supported native
arrays do not establish this broader tagged-element ABI. A read-only source flow fact can make one
loaded value non-null on one branch; it does not change the declared element descriptor or make a
different slot non-null.

## Generic aggregates and descriptor shape

A generic nominal descriptor must be tied to a constructed owner identity and the ordered closed
argument tuple. `Box<int>` and `Box<String>` can have incompatible field scan shapes even though the
source declaration and field name are identical. A generic method parameter and a containing type
parameter also have distinct owner and ordinal identities.

For an inline struct, the descriptor recursively classifies every field. `Pair<String,int>` scans
only its reference field; `Pair<int,String>` scans a different offset. Copying a struct containing
references creates new reference values that must be visible independently while both copies live.
It does not deep-copy the referenced objects.

Nullable values need an explicit representation. The collector first validates and reads the
presence tag, then scans the payload only when present. An enum or tagged union follows the selected
variant and ignores inactive payload bytes. Scanning every payload word as a potential address is
not an acceptable substitute for type-directed metadata.

Descriptor construction must reject infinite by-value recursion, inconsistent partial declarations,
invalid offsets, overflowed sizes, unsupported alignment, foreign type IDs, and layout-version
mismatches before native execution. Cycles through managed references are finite object graphs and
are handled by tracing, not by recursive inline layout.

## Arrays and collections

An array is a managed owner with fixed length and a concrete element stride/descriptor. Allocation
checks `count * stride + header` for overflow. A loop retains the source array reference, snapshots
its length, loads items in ascending order and copies each value into its local item binding. A
reference item that stays live across a call or poll needs a root. A `var` loop item remains a local
copy; writing an element requires explicit indexing.

The current runtime has a 32-byte array prefix with count, stride and element type. The descriptor
scan shape distinguishes supported String arrays from scalar arrays. This does not establish support
for nested arrays, fixed arrays, nullable element arrays or arbitrary value-layout elements.

The intended `std.collections` APIs include `Vector<E>`, hash and ordered sets/maps, queues, deques,
stacks, priority queues, `Span<T>` and `ReadOnlySpan<T>`. Imports and type signatures do not imply
that these implementations exist. Collection-specific storage needs its own representation and
mutation contract:

| Collection | Storage question to settle before native admission |
|---|---|
| `Vector<E>` | How count, capacity, relocation, inactive slots and reference-bearing elements are scanned. |
| `HashSet<E>` | How occupancy, tombstones, hash/equality callbacks and stored references interact. |
| `HashMap<K,V>` | How keys and values are traced and removed independently. |
| `TreeSet<E>` | How nodes, rotations, parent/child links and payloads are traced. |
| `TreeMap<K,V>` | How key/value nodes stay live through comparator calls and mutation. |
| `Queue<T>` | Which ring slots are active and how consumed slots are cleared. |
| `Deque<T>` | How wrapped front/back ranges map to the live scan interval. |
| `Stack<T>` | Why capacity slack is not a root and a popped value stops being a container edge. |
| `PriorityQueue<T>` | How heap order, payloads, and comparator failure preserve the active set. |

Every collection write that publishes a managed reference uses the collector's barrier protocol.
Removing or clearing an entry must stop retaining its former reference after other roots disappear.
Growth must keep old storage and values valid until the replacement is fully initialized and
published. Mutation during iteration uses the collection's documented version or snapshot rules;
an array's fixed-length semantics do not automatically apply to Vector.

## Span, ReadOnlySpan and ref-like storage

`Span<T>` and `ReadOnlySpan<T>` are intended scoped ref structs, not heap collection owners. A view
contains the origin/provenance, bounded offset and length, and read/write capability. Copying the
view header does not copy the elements or extend the origin lifetime. Slicing composes checked
offsets and lengths against the same origin.

For a view over a managed array, a live view must keep the array owner rooted. For a view over stack
storage, the compiler proves the stack region outlives all accesses and that no root-map registration
is mistaken for a longer lifetime. An interior pointer is not independently rooted: retain the base
owner and offset. A view cannot be boxed, placed in an ordinary managed array or heap field, captured
by an escaping closure, sent to another thread, passed across the ORC process boundary, or retained
by asynchronous native code unless a separate owned-copy/handle protocol replaces the borrow.

`ReadOnlySpan<T>` prevents writes through that view. It does not promise that aliases cannot mutate
the underlying storage. A writable `Span<T>` store to `Span<String>` is still a managed reference
store and needs a barrier. A `Span<int>` store does not create a reference edge. The concrete element
type determines that distinction; the spelling `Span<T>` does not.

The generic anti-constraint `allows ref struct` permits the type checker to consider ref-like type
arguments. It is not proof that the generic body avoids heap storage, boxing, capture, return escape,
cross-thread use or an unbounded native lifetime. The current generic FIR verifier does not provide
the complete escape and borrow analysis required to admit arbitrary `Span<T>` instances.

## ref/out locations and interior pointers

A `ref` or `out` parameter identifies exact writable storage. Generic inference for such a call uses
the storage's exact declared type; numeric widening or null-flow does not change its ABI. If a
location refers to a managed field or array element, the compiler roots its base object and tracks a
checked offset/path. It does not put a naked interior address into the object root set.

The base owner must remain alive through the full callee use, any nested allocation/call, and cleanup.
A write through a ref alias to a heap reference field uses the ordinary insertion barrier. The
caller and callee frame maps must describe their live reference slots, and the `out` parameter must
be definitely assigned on every normal return path. A returned ref cannot outlive its source owner.

For a concrete `T = String`, a by-value call result must become a caller root before any later
safepoint. In left-to-right argument evaluation, an earlier reference result remains rooted if a
later argument can allocate. These lifetime facts are not inferred from source variable names or
from the generic function declaration alone.

## Frames and root maps

Each concrete function instance needs frame metadata that describes root slots and their scan
shapes at each collection-capable point. Two instances with the same source body can need different
maps: `T = int` has no root for the value bits, while `T = String` does. A generic aggregate may
require recursively nested offsets or a conditional payload program.

At a cooperative handshake, generated code publishes every live reference needed at the
safepoint, then acknowledges the epoch. Register copies used after the poll must be spilled or
otherwise represented. A stale slot value cannot accidentally keep an object alive forever, but a
live value cannot be omitted because an optimizer moved it out of a source local.

Root obligations cover more than named local variables:

- reference parameters and locals live after the safepoint;
- expression temporaries spanning another operand evaluation;
- call results between callee return and the next safe store/return;
- array owners, String items, index state and loop-carried references;
- references inside concrete tuples, structs, nullable values and enum payloads when those layouts
  become supported;
- capture cells, delegate environments, pending Throwables and cleanup locals;
- base owners of interior ref/out locations and managed-array spans;
- strong native handles, callbacks and retained buffers until native ownership actually ends.

Frame pop occurs once after normal return or completed exceptional cleanup. `finally`, `defer`, and
suppressed exception processing can extend root liveness beyond the last ordinary source read. An
optimizer or runtime helper cannot pop a frame early merely because the user-level function body has
reached its final statement.

The current bootstrap conservatively retains generated reference locals and temporaries until reuse
or function exit. More precise root liveness and general nested value scan programs remain future
work. Generic specialization does not add a separate root plan: admitted instances use the existing
qualified backend/runtime path, and unrepresentable instances are rejected.

## Barriers and collection phases

Yellow Tea uses concurrent marking and a precise non-moving object graph. A reference store can add
an edge from a black owner to a white object. The active marking barrier protects that edge under
the collector's synchronization/memory-order contract. The exact fast path depends on collection
phase; a source store cannot omit the required operation because it happens in a generic body.

Barrier classification follows the concrete destination. Scalar field/array writes do not create
managed edges. Reference slots, nested reference-bearing value fields, reference array elements,
collection entries, handles and root publications follow their documented protocol. Nullable
references use the same reference barrier for non-null values and may clear an edge when storing
null. A nullable value field requires its tag and payload rules to be atomic with respect to the
scan protocol.

Initialization may use a no-barrier path only while a new object is unreachable and private. If the
object escapes through `this`, a global, callback, handle or reentrant call before publication, its
fields must use ordinary barriers. A copied reference temporary remains rooted/protected when an
old heap edge is overwritten. Collection mutation and barriers share ordering rules with allocation
epochs and handshakes.

The current runtime has insertion barriers for generated reference stores and exact array descriptor
scans for the qualified subset. No complete generic collection write-barrier implementation is
claimed here.

## Exceptions and closure environments

The native Throwable carrier owns a strong runtime handle across native copies, catch transfer and
rethrow. Message, cause and suppressed references are traceable runtime state. Generated Sol
exception unwinding, source catch/finally/defer and generic effect escape remain gated; native C++
carrier tests do not prove a generic source frame is correct through Windows EH.

A closure is code plus an environment. Only the environment is a managed edge. A captured mutable
local uses a shared cell when required by source semantics. The cell's concrete fields have a
descriptor, and the closure keeps it alive until no closure references remain. A span cannot enter
an escaping environment; an immediate non-escaping callback is allowed only if the compiler proves
the call ends before the borrow's origin expires.

## Host and guest ownership

The compiler's CLR heap belongs to the host process. AOT applications own their own SolRT and
Yellow Tea instance. ORC executes in a separate native guest process with independent descriptors,
frames, handles and object heap. The host and guest exchange only documented transport values such as
bitcode, arguments, streams, cancellation, diagnostics and exit status. They do not share object
references, descriptor addresses, roots or callbacks.

Process isolation establishes ownership separation; it is not an OS sandbox. Import allowlists,
IPC framing, child lifetime, output bounds, cancellation and exit status remain separate checks.
Static SolRT linkage changes deployment choice and does not merge the guest runtime into Solc's CLR
or create cross-process roots.

## Verification before code generation

Before lowering an instance, a future complete verifier must establish all of the following:

1. The source declaration and ordered type tuple are owned by the current analysis workspace.
2. Every argument is closed, satisfies constraints, and has one exact target-specific representation.
3. Signature, fields, locals, array elements, effects and expression types agree after substitution.
4. Generic calls target the exact selected instance; no backend fallback changes overload ranking.
5. Descriptor layout, target alignment, size arithmetic, field offsets and nullable tags are valid.
6. Every scan edge is covered by a descriptor and every live root is published at each safepoint.
7. Every reference mutation uses the correct phase-aware barrier; scalar bits are never traced.
8. Borrowed locations retain an owner/provenance and cannot escape, be captured, or cross processes.
9. Exception and cleanup edges preserve roots and frame ownership on every reachable path.
10. The selected artifact/runtime combination accepts the descriptor and ABI versions.

Today, the source and FIR verifier cover a narrower type/call subset and the native bootstrap has
specific qualified descriptor/root paths. The full checklist is a normative future requirement; it
must not be described as already proven by schema-5 JSON or by successful parsing.

## Artifact and compatibility consequences

A generic source declaration is not automatically a C ABI symbol or a stable `.solib` export. A
concrete wrapper can call a private closed helper only if its transitive type, descriptor, root and
runtime closure is declared. The current `.solib` schema does not define external generic
instantiation or durable generic type IDs.

Descriptor layout, frame maps, handles, object headers, array stride, or scan programs changing
incompatibly requires a runtime/container compatibility update. AOT and ORC must agree on the same
source layout and root semantics even though they use different execution paths. Dynamic SolRT is
the default; `--static-solrt-lib` changes linkage, not source type identity or GC meaning.

## Lifetime across common operations

The generic body has the same source evaluation order after specialization. Lowering may choose a
different representation, but it cannot reorder an allocating expression past a use that keeps a
managed value live. The table lists the minimum ownership transition that an admitted native path
must make explicit.

| Operation boundary | Values that may remain live | Required handoff |
|---|---|---|
| Function entry | Reference parameters and incoming aggregate fields | Establish the frame before any allocation or poll. |
| Generic call setup | Receiver, earlier arguments, ref/out bases, selected type instance | Root earlier evaluated arguments while later arguments may allocate. |
| Generic call return | Reference result and caller locals used afterward | Publish result in a caller slot before the next safepoint. |
| Array allocation | Array owner, element initializer temporaries | Root values before allocation can trigger a collection. |
| Array iteration | Owner, current reference item, live loop locals | Keep owner/item slots visible at the loop backedge poll. |
| Collection growth | Old owner, new owner, copied values, allocation state | Keep old storage valid until complete replacement publication. |
| Collection removal | Removed value and active mutation state | Preserve aliases until mutation completes; clear the slot after it stops being live. |
| Span access | Borrow origin, checked offset, length, element type | Keep owner rooted or prove stack origin and bounds. |
| Exception transfer | Pending Throwable, cleanup captures, primary error | Transfer strong ownership once and retain frames through cleanup. |
| NativeSafe transition | Approved retained buffers and managed roots | Publish roots before transition; do not expose arbitrary managed references. |
| Callback reentry | Callback code lifetime, closure environment, thread state | Attach/re-enter before using Sol values and reject released callbacks. |
| Function return | Returned references and live cleanup state | Transfer result ownership before popping the frame. |

This table is not a source-to-LLVM instruction recipe. It states the liveness facts that any
instruction selection or optimization must preserve. A safepoint may be introduced by allocation,
an explicit collection request, loop polling, a runtime transition, or a call whose effect may
allocate. The compiler must know which operations are collection-capable instead of assuming that
only visible `Gc.collect()` can suspend mutators.

## Allocation and mutation transaction boundaries

Dynamic arrays and heap collections need a complete object before publishing a descriptor scan
shape. The implementation must validate count, stride and total size with checked arithmetic before
reserving memory. A failed reserve, commit, constructor, element initializer, or comparison cannot
leave a partially reachable owner whose descriptor describes uninitialized reference slots.

For a resize, a safe sequence has distinct old and new ownership:

1. Keep the old collection owner rooted while allocating replacement storage.
2. Initialize replacement metadata so the collector can distinguish unused capacity from live
   elements before any collection can observe it.
3. Copy or move each live element using the type-specific representation and barriers required by
   the destination's publication state.
4. If element copying/comparison fails, keep the old logical collection usable and release the
   unreachable replacement according to runtime rules.
5. Publish the new storage and count as one source-visible state transition.
6. Retire old storage only after no owner or in-flight operation can access it.

This sequence does not prescribe one allocator or collection algorithm. It forbids exposing
half-initialized reference slots, dropping old roots before transfer completion, and reporting
success after a failure left the owner in an undocumented state. Hash-table tombstones, queue
wraparound and deque split ranges are part of the scan state, not incidental implementation bytes.

## Safepoint and descriptor audit points

For each candidate generic instance, the review should be able to answer these questions from
compiler-owned metadata:

- Which exact closed type identity selected each parameter and field layout?
- Which values are managed references, and which are scalar payloads that must never be traced?
- Where are nullable tags, active enum tags, array element slots and nested aggregate offsets?
- Which root slot covers each live reference at each call, allocation, loop poll and exception edge?
- Does the root slot describe the concrete value shape, or merely a source variable that may change?
- Which store writes each reference edge, and which barrier operation protects that edge in the
  current Yellow Tea phase?
- What base owner and checked offset support every interior ref/out/span location?
- Which scope ends the borrow, and can a closure, callback, native request or other thread retain it?
- Which frame owns the pending Throwable and cleanup references until all unwinding is complete?
- Which target/runtime descriptor version is expected by the selected artifact and all consumers?

If any answer depends on guessing from an identifier such as `T`, an unversioned JSON record, a
machine address, a collection's current contents, or a currently empty array, the instance is not
ready for native emission.

## Per-artifact boundary

| Output path | Generic storage question | Current qualification boundary |
|---|---|---|
| AOT `bin` | Can the executable entry and every reachable instance share one supported SolRT descriptor/root ABI? | The closed bootstrap subset has native AOT qualification; this document adds no broader GC claim. |
| ORC `bin` | Are descriptors, root frames and objects wholly guest-owned for the JIT code lifetime? | ORC runs in a separate process; generic storage cases remain subject to the same verified FIR gate. |
| `cdylib` | Can every exported wrapper and private helper use the declared C/runtime closure? | Only the current concrete scalar wrapper surface is qualified; generic C exports are not implied. |
| `staticlib` | Will the native consumer provide one compatible runtime ownership domain? | Static SolRT is an explicit link choice; it does not make the archive freestanding. |
| `solib` | Does the container schema declare the exact types, descriptors and runtime compatibility? | Current schema does not define external generic instantiation or generic descriptor payloads. |

A program that passes one artifact path cannot be used as evidence for another. AOT and ORC share
source semantics, but ORC's process lifetime and import boundary differ from an executable's. A
private helper included in an archive is not a public generic export. A container checksum detects
payload changes; it does not authenticate a producer or confer verifier trust.

## Current implementation versus normative proof

| Subsystem | Current development inventory | Remaining proof before broader generic use |
|---|---|---|
| Object headers | Provisional object/String/array prefixes | Stable layout version, general type and partial field descriptors |
| Array descriptors | Supported scalar/String one-level arrays | Nullable slots, nested arrays, fixed arrays, aggregate elements |
| Root frames | Explicit attach/detach, registered roots and cooperative polls | Per-instance precise liveness through full CFG and exceptional cleanup |
| Stores | Insertion barrier for generated supported references | Generic field/aggregate/collection writes with exact descriptor class |
| Spans | Source contracts and semantic examples | End-to-end borrow owner, stack-map, ref/out, and escape verification |
| Collections | API/type contracts in specifications | Allocation, resize, removal, traversal, failure, and barrier implementations |
| Closures | Source-level capture rules | Environment layout, capture cells, root maps, code lifetime and escape checks |
| Exceptions | Native Throwable carrier root ownership | Generated Sol EH, catches, cleanup, and specialization-aware root transfer |
| Process boundary | Host CLR and ORC guest have separate processes/heaps | Continued validation of transport/import/handle boundaries for each artifact |

This inventory is a status snapshot, not a compatibility promise. The runtime implementation page
and release documentation remain authoritative for which commands and qualification jobs currently
exist. The normative specification remains the authority for future language/compiler guarantees.

## Locality, memory pressure and performance claims

Generic specialization can multiply function bodies, concrete descriptors, frame maps and machine
code. A finite set of type arguments does not guarantee a small live set or cheap root snapshot.
Many instances may share a source body while carrying different reference offsets. The compiler and
runtime need to account for descriptor metadata separately from managed heap bytes and emitted code.

Yellow Tea's current design groups marking work in resident per-span words and coalesces ready-region
claims. That locality strategy does not remove the cost of publishing all active roots, traversing
large arrays, following scattered object graphs, or applying an active reference-store barrier.
Generic collections can add indirection and wide reference-bearing capacity. Memory pressure may
force a collection wait, a failed allocation or a controlled limit error; it cannot permit dropping
an unscanned edge.

A wall-clock pause, throughput, barrier overhead, memory overhead, or latency improvement claim
requires measurements over representative graphs, instance counts, collection capacities, root
counts, thread counts and allocation pressure. Work-unit bounds and scan counters are useful
diagnostics, but they are not substitutes for latency distributions. This guide makes no measured
performance claim for generic specializations or for the collector as a whole.

## Evidence boundaries

The runtime documentation describes current native implementation and qualification commands.
This new specialization slice has not independently qualified every generic storage matrix case.
Do not infer a sanitizer pass, cross-artifact ABI, source exception-handling support, or comparative
GC performance from the existence of a source example or a successful managed FIR build.

The detailed cases in the spec separate source binding, current FIR admission, descriptor shape,
root/barrier obligations and process boundary. A future implementation can claim broader generic
support only when those obligations are implemented, independently verified, and qualified for each
artifact/target contract it advertises.
