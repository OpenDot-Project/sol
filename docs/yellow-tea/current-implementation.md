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
- Strong handles preserve objects; weak resolution publishes directly into a root slot. Sealed white
  targets resolve as null, and dead weak slots are cleared before reclaiming their targets. Closed
  handle identities remain tombstones until shutdown; a generation-based reusable table is pending.
- `SOL_GC_HEAP_LIMIT` validates bytes or `KiB`/`MiB`/`GiB`, with a 16 MiB minimum. Budget reservations
  serialize competing allocators; exhaustion attempts a concurrent cycle before controlled failure.
  Allocators more than 8 MiB above the current collection threshold wait for a concurrent cycle
  with their roots published, preventing sustained allocation from indefinitely outrunning sweep.
  Blocking output waits use native-safe transitions. Fail-stop invariant/OOM paths skip static
  destruction so reporting while holding a runtime lock cannot deadlock shutdown.
- Large unpublished allocation/zeroing and rooted String byte copies use native-safe regions.
  UTF-8/scalar/hash loops poll periodically. A temporary native frame protects new copy results
  before transitioning; a raw result pointer alone would not survive a collection in that region.
- The native Throwable carrier owns a shared strong handle across copies, catches, and rethrows.
  Message/cause/suppressed references are traced. Suppression is bounded and rejects cycles.

## Deliberate limits

The allocator currently uses individual aligned allocations and a synchronized allocation index.
The planned 64 KiB spans, size-class caches, mark/gray bitmaps, allocator assists, and event ring are
not implemented. Lock contention, tombstone overhead, large root inventories, and sweep/weak cleanup
costs require measurement and further work. This baseline is not the completed span allocator.

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
