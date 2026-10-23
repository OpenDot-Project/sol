<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Native diagnostic reference

This is the development contract for `GcDiagnostics.h` and the native profile reader.
The diagnostic snapshot has its own version 1.
That number does not promote the provisional object, descriptor or root ABI to version 1.
There is no corresponding source-level standard-library API yet.

## Reading snapshots

The C entry point is:

```cpp
bool sol_gc_diagnostics(GcDiagnosticSnapshot* snapshot, uint64_t bytes);
```

The caller provides storage and the exact native `sizeof(GcDiagnosticSnapshot)`.
A null pointer or different size returns false without modifying the supplied storage.
A successful read records version, size, epoch, phase, counters, distributions and ready-queue state.
The heap mutex serializes the read with collector-owned mutations.
The idle/sweep barrier counter is separately atomic and therefore observational.
Do not interpret all fields as a global transaction across concurrently running mutators.

The API copies only bounded native data.
It does not allocate an exporter, serialize JSON or invoke CLR code.
The current profile serializer belongs to the test harness.
It is not part of the production collector hot path.
Callers must not retain pointers into private collector state.

The internal phase values are:

| Number | Phase |
|---:|---|
| 0 | Idle |
| 1 | Start handshake |
| 2 | Concurrent mark |
| 3 | Termination handshake |
| 4 | Concurrent sweep |

A completed profile must be captured in Idle with no ready regions.
An external observer can see any valid phase while a collection is active.
Source thread registration and NativeSafe requirements still govern operations that touch roots.
Reading diagnostics does not grant permission to access guest object payloads.

## Counter meanings

Counters are cumulative from runtime diagnostic initialization until shutdown.
Snapshot reads do not reset them.
The ordinary collector counters use saturating addition.
The atomic idle-barrier count is an observational unsigned counter.
It is not used for liveness, allocation admission or termination.

| JSON name | Counted unit |
|---|---|
| `root_slots` | Registered frame slots visited in root snapshots, including null slots. |
| `root_frames` | Frame records visited in root snapshots. |
| `strong_roots` | Open strong-handle entries visited, including null targets. |
| `cache_slots_returned` | Unused cached slots returned specifically at cycle start. |
| `mark_objects` | Objects whose scans the worker completed. |
| `mark_reference_slots` | Descriptor reference loads performed by worker batches. |
| `mark_batches` | Worker claims of ready regions that performed a trace batch. |
| `assist_batches` | Trace batches performed by allocating mutators. |
| `assist_reference_slots` | Descriptor reference loads performed by assists. |
| `assist_objects` | Objects whose scans assists completed. |
| `shade_calls` | Calls to shade, including null references and literals. |
| `new_marks` | Previously white registered objects changed to marked and gray. |
| `already_marked` | Registered targets already marked in the current epoch. |
| `reclaimed_objects` | Allocation records reclaimed during sweep. |
| `reclaimed_bytes` | Aligned registered request bytes reclaimed. |
| `sweep_objects` | Allocation records examined by sweep, live or dead. |
| `sweep_batches` | Nonempty bounded sweep batches. |
| `collections` | Fully completed collection cycles. |
| `termination_retries` | Termination snapshots that returned to Mark with gray work. |
| `allocation_bytes` | Aligned requests registered through the public allocation path. |
| `allocation_calls` | Successful registered public allocations. |
| `pressure_waits` | Cache misses that waited at the registered-byte overrun boundary. |
| `refill_failures` | First failed refill attempts that requested collection before retry. |
| `phase_changes` | Start and Terminate handshake requests, including retries. |
| `mark_max_reference_slots` | Maximum reference-slot count in any worker or assist trace batch. |
| `mark_max_objects` | Maximum completed-object count in any worker or assist trace batch. |
| `assist_debt_paid` | Allocation-debt bytes actually repaid by tracing work. |
| `active_barriers` | Stores that entered the heap-mutex barrier path. |
| `idle_barriers` | Stores that used the Idle/Sweep path without the heap mutex. |

`phase_changes` counts handshake transitions only; it is not a count of every phase assignment.
`new_marks` excludes allocations born black during the current epoch.
`already_marked` excludes null targets and immutable literals outside the managed allocation index.
Those distinctions explain why `shade_calls` can exceed their sum.
An object with no reference fields still contributes to completed-object counts.

`cache_slots_returned` is not the aggregate cache-return counter.
Size changes, detach and shutdown have their own cache accounting.
Use the existing thread-allocation statistics for all returned unused reservations.
Neither counter is a number of reclaimed managed objects.

At a fully drained cycle, worker completions plus assist completions equal new marks.
That relationship need not hold halfway through an array scan.
Allocated objects can also remain black without ever contributing a new mark in that cycle.
Do not derive the live object count by subtracting those tracing counters.

## Timing distributions

Every distribution contains sample count, summed nanoseconds, actual maximum and 12 buckets.
Buckets hold disjoint counts, not cumulative counts.
Their upper bounds in nanoseconds are:

```text
1,000; 4,000; 16,000; 64,000; 256,000; 1,000,000;
4,000,000; 16,000,000; 64,000,000; 256,000,000; 1,000,000,000; UINT64_MAX
```

Durations equal to a bound belong in that bucket.
Zero-duration observations belong in the first bucket.
An empty distribution reports zero for every percentile bound.
Nearest-rank selection uses ceiling division without overflowing `samples * percentile`.
Bucket upper bounds are deliberately distinguished from actual maxima.

| Distribution | Start/end of one sample |
|---|---|
| `safepoint_wait` | Handshake phase publication through all mutators becoming parked/native-safe. |
| `root_snapshot` | Active frame and open-strong-root enumeration and shading. |
| `mark_batch` | One claimed region's bounded worker scan and Finish/recheck. |
| `assist_batch` | The same scan protocol performed by an allocating mutator. |
| `sweep_batch` | Examination/retirement of one bounded group of allocation records. |

Root snapshot time excludes prior thread-arrival wait.
It also excludes the start-handshake cache-flush loop.
Safepoint wait starts after the worker already acquired the heap mutex.
It does not include time the worker spent waiting to acquire that mutex.
Together these distributions are useful components, not a complete decomposition of pause time.

Trace samples include descriptor loads, allocation-index lookups and queue Finish work.
They exclude the worker's yield and subsequent heap-mutex reacquisition between batches.
Sweep can temporarily release the mutex for Windows region retirement;
that elapsed time remains inside the current sweep sample.
None of the distributions is a thread CPU-time counter.

## Ready-queue counters

`regions` is current queue membership, excluding a claimed region.
`peak_regions` is the maximum queue membership observed since reset.
`claims` counts successful removals from the ready queue.
`publications` counts actual queue appends, including requeues after partial scans.
`coalesced_publications` counts publish requests skipped because the region was queued or claimed.

One wide array may cause many claims of its one-slot region.
One dense small span may serve many objects in a single claim.
Queue counts alone do not establish memory locality or mark throughput.
At Idle after a completed run, every publication must have a corresponding claim.
Shutdown can cancel pending work; compare completed workloads before shutdown instead.

## Event-ring opt-in

`SOL_GC_DIAGNOSTICS` accepts exactly `0` or `1`; an unset value means disabled.
Other values fail startup with a static configuration diagnostic.
The runtime does not print the process environment or the rejected value.
Settings are read when the first thread attaches to a runtime without attached threads.
Set the variable before process startup for reproducible profiles.

The ring has 4,096 resident records, each 48 bytes.
Its storage exists even when recording is disabled.
Appending an event performs no dynamic allocation.
The numeric fields are sequence, monotonic timestamp, epoch, kind, work and duration.
There are no object addresses, source names, type names, strings or application payloads.
An opt-in exporter can consume records outside the mutator path in future work.

| Kind | `work` interpretation |
|---|---|
| `start_requested` | Attached thread count at Start request. |
| `roots_scanned` | Frame slots plus open strong-handle entries examined. |
| `mark_started` | Initial ready-region count after Start roots. |
| `mark_batch` | Worker reference slots examined. |
| `assist_batch` | Assist reference slots examined. |
| `termination_requested` | Attached thread count at Terminate request. |
| `sweep_started` | Allocation records present at sweep start. |
| `sweep_batch` | Allocation records examined in that batch. |
| `cycle_completed` | Registered live bytes observed by sweep. |
| `pressure_wait` | Registered bytes when an allocation chose to wait. |
| `refill_failure` | Aligned requested allocation size that failed refill. |

Duration is populated for roots, trace batches and sweep batches.
Other event kinds currently report zero duration.
The event list is not a complete per-allocation trace.
The marker's correctness never depends on an event being retained.

## Pagination and loss

The C reader accepts the last sequence already consumed:

```cpp
uint32_t sol_gc_read_events(uint64_t after, GcEvent* events, uint32_t capacity);
```

A zero capacity returns zero without requiring output storage.
A nonzero capacity requires valid caller-owned storage.
The return value is the number of records copied, never greater than capacity.
Reading beyond the newest sequence returns zero.
Reading from an overwritten sequence begins at the oldest retained record.
Consumers must compare returned sequences with their cursor and report any gap.

Sequence identities start at 1 and never wrap.
At ordinary ring overwrite, both the first retained sequence and dropped count advance.
At sequence exhaustion, append stops and dropped count saturates rather than reusing identities.
The profile validator checks the ordinary non-exhausted complete retained window.
It does not treat a truncated list as a complete event trace.

Snapshots and page reads acquire the heap mutex independently.
A concurrent producer may overwrite records between the two calls.
Long-running consumers must handle that race using sequence gaps.
The profile harness reads after its workload has drained and stopped mutating the guest heap.
Its retained window therefore has stable accounting without freezing the application for export.

## Limits of the evidence

These diagnostics do not measure hardware cache misses, per-thread arrival times,
root liveness quality, native metadata RSS, scheduler delay or a whole-application pause histogram.
Native event recording is separate from OpenTelemetry integration.
No managed exporter is called by a barrier or collector worker.
See [native profiling](native-profiling.md) for reproducible workloads and report validation,
and [resident tracing](resident-tracing.md) for the implementation protocol behind the counters.
