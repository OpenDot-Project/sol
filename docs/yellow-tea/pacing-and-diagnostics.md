# Pacing and diagnostics

The following settings and counters define the initial collector design. They are not implemented
runtime options in Sol 0.0.1. Numeric work targets require qualification on real workloads before
they can become release performance claims.

## Trigger and capacity accounting

The initial target is `live_bytes * 1.75 + 8 MiB reserve`, with a minimum managed heap of 16 MiB.
Committed capacity and estimated mark throughput clamp the next trigger. The collector tracks
allocated bytes, live bytes, reserved address space, committed storage, and fragmentation separately.

Allocation rate and tracing throughput influence how early marking starts. Waiting until the heap
limit is nearly exhausted can leave too little headroom for concurrent collection to make progress.
The trigger must leave room for live growth and for sweep to return reusable storage.

The starting worker policy is `max(1, min(4, available_cpu_count - 1))`, with an explicit caller cap.
The runtime must define how unavailable or restricted CPU counts are normalized. Using every CPU
by default is not an assumption of this design.

## Assists and throttling

Allocation debt is converted into descriptor/reference-scan work. The starting assist target is
100 microseconds, with scan batches of at most 64 KiB. An assist reaches a protocol-safe boundary
between batches; it cannot abandon half-published work just to satisfy a timer.

If allocation outruns marking, the runtime starts cycles earlier, increases bounded assist debt,
and rate-limits growth paths. It does not insert an unreported synchronous whole-heap collection.
Assists, refill waits, and throttling belong in allocation-latency measurements even though they
are not global pauses.

## Managed heap limit

The planned `SOL_GC_HEAP_LIMIT` setting accepts a positive byte count or a `KiB`, `MiB`, or `GiB`
suffix. Checked parsing rejects overflow, unknown suffixes, and values below 16 MiB at startup.
The setting limits managed committed heap bytes. It does not cap total process memory, native
library allocations, code pages, thread stacks, or reserved virtual address space.

Diagnostics should identify the invalid setting without dumping the process environment. There is
no need to expose unrelated environment values or application contents in configuration errors.

## Allocation failure

Reserve exhaustion first uses a preallocated `OutOfMemoryError` path in the RuntimeException family
when the runtime can safely carry and unwind that error. Not every exhausted or corrupted state is
recoverable. Failure to maintain collector invariants or safely publish mandatory metadata follows
controlled abort rather than continuing with an incomplete heap graph.

The reserve is not a promise that all OS commitment failures can become catchable exceptions.
Failure injection must cover object allocation, span metadata, optional queue caches, handle storage,
and exception reporting separately.

## Metrics

| Group | Measurements |
|---|---|
| Heap | Allocations, allocated/live/committed/reserved bytes, size-class slack, fragmentation. |
| Tracing | Mark CPU, scan throughput, ready depth, gray work, resident-bitmap fallback count. |
| Reclamation | Sweep CPU, claimed/completed spans, reclaimed bytes, large-region retention. |
| Mutation | Barrier slow paths, reference-copy work, in-flight publications. |
| Pacing | Assist count/work/time, debt, refill waits, allocator throttling. |
| Coordination | Handshake requests, per-thread arrival, active work, termination retries. |
| Native ownership | Strong handle count, callback roots, retained I/O buffers, NativeSafe duration. |
| Weak ownership | Resolve failures, promotion attempts, reader-epoch retirement delays. |

The planned native event ring holds 4,096 bounded records. Overflow increments a dropped-records
counter. Losing telemetry cannot affect collection correctness, and lost events cannot disappear
silently from reports.

An optional OpenTelemetry consumer is outside the native mutator fast path. Native collection does
not call a managed exporter from a reference-store barrier. Export remains opt-in; object contents,
strings, source paths, and unbounded type identities are not default metric dimensions.

## Reporting latency

Reports separate safepoint wait, root scanning, active start/termination work, concurrent mark/sweep,
assists, and allocator waits. Record p50, p95, p99, p99.9 where sample size supports it, and maximum,
along with CPU, build configuration, thread count, heap budget, workload, and observation duration.
An average pause or a short synthetic run cannot establish a tail-latency guarantee.
