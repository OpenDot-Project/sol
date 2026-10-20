# Barriers and memory ordering

Yellow Tea's initial design uses an insertion/update barrier. It does not combine an insertion
invariant with an unrelated snapshot-at-the-beginning deletion protocol. The ordering described here
is a required implementation contract, not evidence of a tested native memory model.

## Reachability invariant

Every object reachable from the initial roots, or through a subsequently published managed edge,
must remain protected by a current-epoch mark bit and any necessary pending scan work. Publishing a
reference to a white target requires shading that target first.

The first implementation does not test the parent object's color to decide whether to protect a
store. This makes the barrier easier to audit at the cost of additional checks. Optimizing away a
barrier requires a separate compiler/runtime proof; it is not justified by a benchmark alone.

## Reference-store sequence

While marking is active, a managed reference store follows this sequence:

1. Enter the epoch/publication guard and establish the in-flight operation.
2. For a non-null white target, atomically test and set its current mark bit.
3. Publish the target's gray bit with release ordering, then publish its span's resident ready bit.
4. Publish the destination as an aligned atomic reference store.
5. Leave the in-flight guard.

The visible destination edge cannot precede protection of the target. A termination handshake cannot
seal the mark set while this publication sequence is incomplete. Marker consumption must use the
matching acquire protocol before relying on published work or object metadata.

A marked object whose scan obligation was lost is still a collector bug: retaining that object while
freeing one of its reachable children violates the invariant. Mark-bit and work publication must be
treated as one correctness protocol.

## Allocation-free work publication

Each admitted span has resident mark, gray, and ready metadata. A barrier does not allocate a queue
node to make its reference store safe. Thread-local queues can cache work, but queue exhaustion must
fall back to the resident bitmaps without dropping a scan obligation.

A marker atomically claims ready work and exchanges gray words for scanning. Producers can add new
gray bits while it scans. A producer publishes readiness for those later bits; scanner completion
must not blindly clear that publication. Duplicate scanning is a performance problem, while lost
scanning is a correctness failure.

The model must exercise the interval between setting the mark bit and publishing gray/ready work.
An in-flight operation in that interval prevents sealing. An allocation failure in an optional queue
cannot remove the resident fallback's obligation.

## Barrier coverage

| Store location | Required treatment |
|---|---|
| Root slot or live temporary spill | Protect the new target and publish it under the root protocol. |
| Class/object field | Use the managed reference-store barrier. |
| Reference array element | Protect each new reference edge. |
| Embedded struct or tuple | Trace and copy only the descriptor's reference fields as reference edges. |
| Closure environment or shared capture cell | Apply the same reference-store contract. |
| Interface value or delegated storage | Protect its managed object owner/environment. |
| Overlapping bulk copy | Preserve source `memmove` semantics and barrier coverage for reference slots. |

Scalar byte copies do not become safe reference copies merely because an address is aligned.
Reference-containing buffers need descriptor-aware operations. Scalar fields are not read by the
marker as speculative pointers.

## New objects and removed edges

New objects are current-epoch live/black allocations. Their reference initialization stores still
shade their targets. Publishing a black object with an unprotected white child would otherwise
violate the same edge invariant as updating an older object.

The design omits a deletion barrier because references loaded from old edges must remain protected
as valid temporaries, roots, or subsequently published managed stores. A compiler optimization that
drops this protection invalidates the algorithm. Root-liveness and optimizer tests are therefore
part of collector correctness, not just code-generation quality.

## Concurrent reads and phase changes

Marker reference loads follow Sol's managed memory model and atomic slot requirements. A marker may
observe either side of a concurrent reference update; insertion protection must make either observation
safe. Reading ordinary scalar fields as if they were atomically published references is not allowed.

Phase changes occur through the [thread handshake](roots-and-safepoints.md). A thread cannot keep an
idle-phase flag indefinitely and bypass marking after the coordinator has activated the barrier.
Native callbacks and root publication participate in the same epoch agreement.

Required adversarial schedules include mark-bit-before-gray publication, simultaneous gray production
and scanner completion, queue-full fallback, root-edge transfer followed by deletion, bulk overlap,
and phase transition while a store is in flight.
