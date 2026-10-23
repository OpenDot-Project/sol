<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Resident tracing implementation

This chapter describes the experimental native marker in the development source.
It complements the [collection cycle](collection-cycle.md) and records implementation choices,
including costs that have not been removed. It is not a released runtime compatibility contract.

## Ownership and representation

Every small-allocation span owns one `MarkRegion` in native metadata.
Each dedicated large object owns a one-slot region through its allocation record.
Managed payloads contain neither queue links nor a list of gray children.
The 16-byte object prefix and descriptor layout remain unchanged.

A region describes a base address, slot count and stride.
Small-span slot addresses are computed from the base and class stride.
A dedicated region has one slot and does not use stride arithmetic.
The existing allocation index validates the computed address before a descriptor is read.
There is no additional array of allocation-record pointers for every possible slot.

Metadata words each cover at most 64 slots and contain:

| Field | Meaning |
|---|---|
| Epoch | Collection identity for the two bitsets in this word. |
| Marked | Objects discovered or allocated black in that epoch. |
| Gray | Discovered objects whose descriptor scan is incomplete. |

A separate 64-bit summary identifies words containing gray slots.
At the smallest 16-byte class, 4,096 slots require 64 words.
The summary therefore covers the entire 64 KiB span without a second summary level.
Partially used final words never expose slot indices outside the region.
The scan chooses the first gray word and then its lowest gray slot.

This groups work by span and selects the lowest currently gray slot within that span.
Discovering a new lower-address child can return the scan to an earlier slot; address order
is not globally monotonic over a claim or a complete collection.
It does not move objects, compact holes, group objects by graph parent or promise cache residency.
The allocation index lookup remains part of tracing each selected object.
An object-to-record array could remove that lookup at a different metadata cost;
the current implementation does not pay for that array.

## Epochs without a heap-wide bitmap clear

Collection start increments the collector epoch.
Words from another epoch are logically unmarked and contain no current gray work.
Their physical bit patterns do not need to be cleared during the start handshake.
The first write in the new epoch initializes that word's epoch and bitsets.
The gray-word summary also has an epoch and is initialized lazily.

Allocation explicitly marks its slot black in the current epoch.
That operation does not put the object on the ready queue.
Reference initialization after allocation must therefore use the insertion barrier.
A newly allocated black object cannot rely on a future descriptor scan to find a white child.

Collections do not overlap.
The worker finishes or cancels all ready work before region metadata can be destroyed.
The epoch mechanism is not a scheme for concurrent collectors to overwrite each other's words.
Counter exhaustion and process-lifetime qualification remain separate concerns.

## Ready span queue

`ReadyWork` is an intrusive FIFO of regions.
Its link, queued flag and claimed flag live in resident region metadata.
Publishing gray work does not allocate a queue entry or resize a container.
Repeated publication coalesces when a region is already queued or currently claimed.
The gray bitmap retains the actual work; queue membership is only the scheduling mechanism.

The queue protocol has four operations:

1. Shade sets the marked and gray bits for a newly discovered object.
2. Publish appends a region only when it has work and is neither queued nor claimed.
3. Claim removes the front region, clears queued and sets claimed.
4. Finish clears claimed, rechecks gray bits and republishes remaining work.

Finish must recheck even if the consumer believes it completed the last selected object.
Tracing that object can discover a different white object in the same region.
Its publication coalesces against the claimed flag.
Without the final recheck, the bitmap can retain gray bits while no queue entry refers to them.

The implementation currently serializes these operations with the heap mutex.
The bitmap words and queue links are not a lock-free multi-producer data structure.
The finite model deliberately splits publication and consumer steps more finely to test the
lost-work invariant and an intentionally broken version without the final recheck.
Passing that model does not prove every native schedule or a future atomic implementation.

The required invariant outside a running claim is:

```text
current-epoch gray work => queued or claimed
```

Duplicate queue nodes are forbidden.
A region cannot be simultaneously linked twice or destroyed while queued or claimed.
Shutdown cancels remaining queue membership before releasing storage and allocation metadata.
Optional event-ring overflow cannot remove tracing work.

## Trace budgets and resumable arrays

A marker batch has two independent limits:

| Limit | Worker | Normal assist | Pressure assist |
|---|---:|---:|---:|
| Reference slots examined | 64 | 16 | 64 |
| Objects completed | 32 | 8 | 32 |

The object limit bounds batches of scalar objects with no references.
The reference limit bounds wide objects and reference arrays.
Neither limit is a wall-clock deadline.
A page fault, allocation-index lookup, lock wait or CPU scheduling delay can exceed a desired time.

Each allocation record retains a descriptor cursor.
An incomplete array keeps its gray bit set when a batch exhausts its reference budget.
The next claim resumes at that cursor, including in the middle of a composite element.
Only completion of the entire scan clears the gray bit.
The cursor is reset when an object is first shaded in a new epoch.

For an array with `N` elements and `R` reference offsets per element, scan position `i` selects:

```text
element = i / R
field   = i % R
offset  = array_prefix + element * stride + reference_offsets[field]
```

The runtime checks the array prefix size before reading count and stride.
It rejects a negative count, zero stride or payload that exceeds the registered allocation.
Each reference field must fit within one element and within the whole allocation.
Fixed-object reference fields must also fit within the descriptor's declared size;
extra aligned allocation bytes do not extend a fixed object's scan layout.
Reference loads must be pointer-aligned and must not point inside the object prefix.
Nonempty scan shapes require an offset table.
Malformed metadata follows the invariant failure path before the invalid reference load.

Padding bytes, scalar fields and pointer-looking scalar values are not traced.
An empty reference array completes without reading a reference slot.
A shape with no reference offsets is a scalar scan for collector purposes.
The compiler still owns the correctness and lifetime of immutable descriptors.
These checks are not protection against arbitrary hostile native memory corruption.

## Barrier phase fast path

Reference stores first require an attached ManagedRunning thread and aligned destination.
They load the published collector phase with acquire ordering.
In Idle or Sweep they count the fast path and publish the slot with a release store.
Those two phases do not acquire the heap mutex in the store barrier.

Start, Mark and Terminate take the heap mutex.
The barrier then checks the current phase, shades the target when required, and stores the edge.
An active store still pays for lookup, bitmap publication and heap-mutex serialization.
An increment of an atomic diagnostics counter is also present on the idle/sweep fast path.
This is not a zero-cost barrier.

The fast path relies on a specific mutator protocol:

- A barrier cannot park or enter NativeSafe between loading the phase and publishing its store.
- Start cannot finish while that mutator remains ManagedRunning.
- A mutator that resumes after a handshake synchronizes through the heap mutex.
- Sweep begins only after termination has sealed the reachable graph.
- Raw references to sealed white objects cannot be resurrected during Sweep.

An Idle read that races a Start request is safe because the store completes before that thread
can acknowledge Start; the root scan and later tracing see the published edge.
Retaining a cached Idle decision across a park would break this argument.
The phase model injects that broken transition and requires a counterexample.

The native harness also holds the heap mutex in another thread and requires Idle stores to
complete before it is released. A separate fixture holds a mutator running during Start,
performs a real active store, then allows the worker to scan and finish.

## Allocation assists

Each attached thread owns allocation debt for one collector epoch.
Registering an allocation adds its aligned request size, capped at 1 MiB of debt.
Changing epoch resets the debt before the next charge or assist decision.
An ordinary assist becomes eligible at 4 KiB of debt.
Physical or registered-byte pressure can make an assist eligible before that amount.

Assists run only during Mark and only if ready work exists.
They use the same tracer, resident bitmaps and ready queue as the worker.
There is no second marking algorithm and no fabricated debt repayment without work.
Completed scalar bytes or examined reference slots produce credit, bounded by outstanding debt.
Reference credit and completed-object credit use their maximum rather than counting both twice.

Assist is checked on a cache miss before obtaining more small slots.
Up to seven previously reserved cache hits can precede the next assist opportunity.
The physical commitment budget includes all reserved slots regardless of assist timing.
Index publication, polling, active barriers and assists still serialize on the heap mutex.

The collector currently treats either of these conditions as assist pressure:

- Registered aligned bytes have reached the current trigger.
- Committed bytes plus pending commitments have reached 75% of the physical heap limit.

The next registered-byte trigger is `live * 1.5 + 8 MiB`, with saturating arithmetic.
It is at least 16 MiB and is capped at 87.5% of the heap limit, subject to that minimum.
This is a starting policy, not a throughput-adaptive controller.
Crossing the assist-pressure watermark alone does not currently request a cycle;
registered-byte triggers and allocation-refill failure request collection.

If registered bytes exceed the trigger by at least 8 MiB, a cache miss waits for collection.
Failed physical refill collects and retries before the controlled out-of-memory path.
The collector does not replace this with a full-heap STW fallback.
Live data, class slack, fragmentation and concurrent pending refills can still prevent allocation.

## Remaining qualification

The current change establishes work locality and bounded scan units.
It does not establish a throughput win against the previous marker or another collector.
Cache-miss and LLC measurements, fragmentation curves, sustained allocation pressure,
long-lived graphs, native metadata overhead and tail latency require controlled experiments.

Root slots are still scanned in coordinated Start and Terminate handshakes.
There is no concurrent stack scanning or elimination of root snapshot work.
Fine-grained generated root liveness and long straight-line safepoint placement remain open.
Use the [native profiling guide](native-profiling.md) to collect evidence without confusing
coarse histogram bounds, allocation latency and global pause duration.
