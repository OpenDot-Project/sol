<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Current runtime implementation

The development branch contains an experimental precise, non-moving concurrent collector in
`src/native/runtime`. This is unreleased work, with provisional descriptor layout version 0.
The design chapters describe the intended complete runtime; this page records the narrower code.
No production-readiness or measured pause guarantee follows from the implementation.

## Process ownership

The managed compiler uses the host .NET GC. Its LLVM bridge produces compiler data and COFF objects;
it does not run guest Sol code or link the guest collector into the CLR process. AOT programs run
as native Windows executables. ORC runs in the separate native `sol-exec-host.exe` child process.
Yellow Tea, SolRT descriptors, roots, handles, and guest objects belong to those guest processes.
IR bytes, arguments, standard streams, and exit status cross the boundary; object references and
collector callbacks do not. Process separation is not an OS security sandbox.

## Implemented baseline

- Small allocations use 64 KiB virtual-memory spans and the 44 documented size classes through
  32,768 bytes. Resident allocation/touched bitmaps support slot reuse without embedding free-list
  pointers in object payloads. Larger allocations have dedicated reserved/committed Windows regions.
  An epoch change removes old spans from allocation availability in a fixed 44-class operation.
  Old spans become available only after every previous object record in that span has been swept.
  New spans and previously empty spans can serve black allocations during concurrent collection.
  At most one empty span per class is retained; commit pressure trims that bounded reserve.
- A refill first charges a pending commit permit while holding the heap mutex, then prepares its
  metadata and Windows reservation/commit with that mutex released. Allocating mutators publish
  their roots and enter native-safe mode for this slow path. Publication reacquires the mutex and
  stamps the span with the allocator's current epoch; preparation may cross collection cycles.
  Small preparation publishes an empty span. Slot reservation occurs only after managed reentry,
  so an unregistered small slot cannot remain hidden across collection epochs.
  Failed reserve/commit returns its permit and releases an incomplete reservation before retry.
- Reclaiming a large region or an excess empty span detaches it before releasing the mutex for
  `VirtualFree`. Its commitment remains charged until release completes and the mutex is acquired
  again. Competing refills cannot spend memory that the OS is still releasing. Cached-span trimming
  revisits shared list entries only after reacquiring the mutex; it retains no iterator across a release.
- Object prefix: 16 bytes. String prefix: 24 bytes with valid immutable UTF-8, embedded NUL support,
  and atomic Unicode scalar-count caching. Array prefix: 32 bytes with count, stride, and element type.
- Immutable descriptors identify base types and exact reference offsets. Scalar-looking addresses
  are not traced. Reference arrays scan element slots; scalar arrays have no reference scan shape.
- Explicit thread attach/detach, registered root frames, native-safe transitions, and cooperative polls.
  Generated reference locals and temporaries have reusable root slots; stores use an insertion barrier.
  Functions poll after publishing parameters; loop backedges also poll. Temporaries are retained
  conservatively until reuse or function exit; fine-grained root liveness remains future work.
- A collector worker coordinates start and termination root handshakes, concurrent mark, and
  concurrent sweep. Marking examines at most 64 reference slots per locked batch. Gray work is
  intrusive resident allocation metadata; the store barrier does not allocate a work queue.
  Outstanding termination work returns to concurrent mark instead of draining the heap while parked.
- Sweep stops at its initial allocation-list boundary. Newly appended black allocations cannot
  indefinitely extend the current sweep. It releases the heap lock after at most 64 allocation
  records or 256 KiB of examined allocations, whichever comes first. This is a work bound, not a
  measured pause bound. No full-heap STW collection fallback exists.
- Strong handles preserve objects through an intrusive list containing only open strong owners.
  Closing unlinks in constant time; root handshakes do not visit weak handles or closed history.
  Weak resolution validates both the address and a nonzero, process-monotonic allocation identity
  under the heap mutex before publishing directly into a root slot. Missing, replaced, or sealed
  white targets resolve as null. Sweep removes the allocation index entry before storage retirement;
  it does not scan the handle table. A weak record may retain stale address bits until resolution
  clears it, but the resolver never dereferences them before identity validation. Immutable
  process-lifetime literals are explicitly distinguished from collectible targets.
  Closed handle identities remain tombstones until shutdown; a generation-based reusable table is
  pending. Allocation identities survive runtime shutdown and fail on exhaustion rather than wrap.
- Each attached thread caches at most eight reserved slots from one span for one aligned request size.
  The first slot is consumed immediately, leaving at most seven unused entries. Cache hits take a
  slot under thread ownership without acquiring the heap mutex. A size change returns the old cache;
  start handshakes return every thread's unused slots before advancing the allocator epoch. Detach
  and shutdown also return them. Returning unused entries performs no OS release or graph traversal.
  The batch's handed-out object record remains present until the next start flush, so reserved cache
  slots cannot leave an old span's sweep count permanently outstanding. Existing polls and allocation
  index publication still acquire the heap mutex; the complete allocation call is not lock-free.
- `SOL_GC_HEAP_LIMIT` validates bytes or `KiB`/`MiB`/`GiB`, with a 16 MiB minimum. Budget reservations
  serialize budget decisions and count pending commit permits as well as published/retiring managed
  regions, including class slack and retained empty spans. Native collector metadata, uncommitted
  address-space reservation and process RSS are
  excluded. Exhaustion attempts a concurrent cycle before controlled failure.
  Allocators more than 8 MiB above the current collection threshold wait for a concurrent cycle
  with their roots published, preventing sustained allocation from indefinitely outrunning sweep.
  Blocking output waits use native-safe transitions. Fail-stop invariant/OOM paths skip static
  destruction so reporting while holding a runtime lock cannot deadlock shutdown.
- Large unpublished zeroing and rooted String byte copies use native-safe regions.
  UTF-8/scalar/hash loops poll periodically. A temporary native frame protects new copy results
  before transitioning; a raw result pointer alone would not survive a collection in that region.
- The native Throwable carrier owns a shared strong handle across copies, catches, and rethrows.
  Message/cause/suppressed references are traced. Suppression is bounded and rejects cycles.

## Deliberate limits

Shared slot metadata and the allocation index still use the heap mutex. The bounded thread slot cache
does not remove poll or registration locks. Span mark/gray bitmaps, allocator assists and the event
ring are not implemented. Marking still uses
per-object records and intrusive gray work. Lock contention, tombstone overhead, large active root
inventories and weak lookup costs require measurement. Region reserve/commit/release and refill metadata
preparation run outside the heap mutex. Object-record/index registration still uses that mutex and can
allocate native metadata. This is not the completed fast path or a measured latency guarantee.

Locality still depends on per-object gray records; span-coalesced mark/gray traversal is pending.
Every reference store takes the heap mutex for insertion shading. Memory pressure can wait for a
cycle or fail within the physical budget; scan-proportional bounded assists are not implemented.
Root snapshots still scale with active frame slots and strong owners, and cache flushing adds bounded
work per thread. These costs require separate locality, barrier, pressure and safepoint measurements;
the thread slot cache does not establish improvements in those areas.

The native-only `sol_gc_allocator_stats` snapshot reports committed/reserved bytes, live slot/page
capacity, span/large/small allocation counts and reused slots. Its counters exclude native metadata;
reuse is cumulative until runtime shutdown. Region counters cover published and retiring storage;
pending permits protect the budget but are not included in that unchanged snapshot. During an OS
operation the snapshot is not an atomic measurement of Windows commitment. Counts of active spans
and objects can already fall while retiring storage remains charged. The existing `GcStats` layout stays unchanged, and
`GcStats.HeapBytes` remains aligned object bytes rather than physical committed capacity. These
internal snapshots are not a new implemented Sol standard-library API or a stable ABI 1 promise.

`sol_gc_thread_allocation_stats` reports cache hits, successful batch refills, unused cached slots,
returned slots, nonempty flushes and dedicated large allocations. Detached thread totals remain until
shutdown. Per-thread counters use relaxed atomics so concurrent diagnostics do not race cache hits;
the aggregate is observational rather than one transactional instant. Existing allocator slot/capacity
counters include reserved cache entries. `GcStats.ObjectCount` and `HeapBytes` count registered objects
and their aligned requests. Physical commitment includes all cache reservation and class slack.
`ReusedSlots` counts reused slot reservations, including cache entries returned without being handed
to a caller. The allocation-pressure wait is checked on cache misses; up to seven cached acquisitions
can precede the next check, while physical budget enforcement still covers the entire reserved region.
Start flushing adds at most seven unused entries per attached thread to handshake work.

`sol_gc_handle_stats` adds a native-only snapshot of open strong/weak counts, closed tombstones,
cumulative strong-root visits, collectible weak-target lookups and rejected weak targets. A cleared
weak handle remains open until released; rejection counts advance only when a non-null target is
cleared. Root visits count list entries examined, including null strong owners, rather than objects
marked or elapsed time. Counts and work totals reset at runtime shutdown. Existing object prefixes,
root frames, `GcStats`, and `AllocatorStats` layouts are unchanged. Handle pointers are valid only
within their owning runtime lifetime; shutdown does not preserve stale pointer-handle validity.

Generated programs currently allocate Strings and single-level arrays of supported scalars or String.
General class construction, virtual/interface dispatch, closures, nested value scan programs, source
try/catch/finally/defer lowering, full borrowing, native callbacks, and source threading remain gated.
Native C++ carrier tests do not establish generated Windows EH or ORC unwind qualification.
Root polls within long straight-line functions, full source stack traces, and fine-grained root
liveness also remain incomplete. Runtime ABI 1 compatibility is not established.

## Validation entry points

`eng/test-runtime.ps1` builds a CLR-independent ASan harness. It exercises UTF-8/scalar boundaries,
cyclic graphs, precise array scans, handle lifetime and weak clearing, exception copy/rethrow roots,
suppression, seeded reachability comparisons, concurrent allocation/collection, and heap limits.
The span harness also checks every size class, boundary rounding, address uniqueness/reuse, sealed
epochs, empty-span retention, actual Windows region accounting, budget trimming, zero-on-reuse and
shutdown. Two intentionally invalid child runs must produce ASan use-after-poison diagnostics for
class slack and a reclaimed cached slot. Poisoning starts after the aligned request, not after a
String's logical byte length; logical bounds remain the runtime operations' responsibility.
Harness-only hooks pause real reserve/commit/release paths and inject reserve or commit failure.
Tests check small and large pending permits, three epoch advances before refill publication,
incomplete-reservation cleanup, small/large retirement accounting, and three complete collection
cycles while a mutator is paused inside refill. The hooks are absent from production binaries.
Handle tests leave 50,000 closed owners behind and check that two leaf strong owners cause exactly
four visits across start and termination root snapshots. They force cached-slot address reuse and
reject the old weak identity, verify process-lifetime literal/null behavior, race close against
resolution and collection, and exercise the actual identity sequence at its exhaustion boundary.
These work-count checks are not latency benchmarks.
Thread-cache checks cover all 44 classes, size changes, epoch sealing, detached ownership, rooted
objects in four native-safe threads across repeated cycles, and a poisoned returned-cache slot.
An additional harness-only hook pauses after empty-span preparation and before managed reentry;
three collections must complete with no reserved small slots before allocation resumes.
The bounded barrier model explores two mutators and a marker and detects an intentionally missing
barrier. It is a model of those transitions, not a proof of all native code or schedules.

`eng/test-backends.ps1` compares actual AOT/ORC String/array/GC behavior in both runtime linkage modes,
links native consumers against libraries, checks deterministic containers, and inspects native imports
for the host/guest boundary. `eng/test-native.ps1` retains arithmetic, control-flow, and failure cases.
Successful managed tests alone are not evidence for these native checks.

The managed native-integration tests run when `SOL_TEST_NATIVE_REQUIRED=1`; native CI sets that
flag and requires an installed backend, so a missing component fails. The frontend-only jobs
explicitly report those tests as skipped. A separate report-only Codecov job uploads managed
orchestration coverage from native CI; it is not C++ line coverage or a GC correctness proof.
