<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Collection cycle

This is the planned phase protocol. A collector implementation must pass the state-machine tests
before these transitions can be described as working behavior.

## Phase responsibilities

| Phase | Application threads | Collector work | Exit condition |
|---|---|---|---|
| Idle | Run and allocate. | Maintain accounting. | Trigger requests a cycle. |
| Start requested | Arrive at safepoints or remain NativeSafe. | Advance epoch and establish barrier/registry agreement. | Every registered thread is safe. |
| Initial root snapshot | Parked or NativeSafe. | Shade published roots. | Root snapshot is protected. |
| Concurrent mark | Run with the insertion barrier. | Drain gray work and scan exact descriptors. | Candidate quiescence. |
| Termination requested | Park and flush publication state. | Verify quiescence and finish bounded residual work. | Seal mark or resume concurrent marking. |
| Concurrent sweep | Run and allocate from swept storage. | Reclaim unreachable slots under exclusive span ownership. | Sweep completes. |
| Accounting | Run. | Update live bytes and the next trigger. | Return to idle. |

Only the phase coordinator changes the active epoch. Start and termination synchronize thread state,
barrier publication, roots, and worker queues. They are not opportunities to scan the entire heap
while application execution is stopped.

## Starting a cycle

The coordinator takes phase ownership, advances the epoch, and publishes the active-barrier state.
Every registered managed thread acknowledges the request after publishing its frame state. NativeSafe
threads have already published roots and cannot mutate managed edges until they reenter the handshake.

Once all threads are safe, root values are shaded into resident tracing work. The coordinator then
releases application threads and workers trace concurrently. New allocations use current-epoch black
allocation, with barrier-protected reference initialization.

A thread that attaches during the transition must join the phase agreement before managed execution.
Registration and phase state cannot be two unrelated checks with a race window between them.

## Concurrent tracing and locality

The initial correctness path can use object queues as an optimization over resident gray work.
Later locality work groups targets by span and scans nearby gray objects together. Work stealing is
at span-batch granularity where practical. No mandatory SIMD or AVX-512 dependency is part of the
x86-64 baseline.

Batching must preserve eventual scanning of every gray object. A span already being processed can
receive additional work. Its scanner cannot clear a producer's later ready bit merely because the
span was previously queued. See [work publication](barriers-and-memory-ordering.md).

## Quiescence and termination

Candidate quiescence requires all of the following, with an epoch-consistent observation:

- The global ready bitmap has no pending work.
- Worker-local caches contain no unpublished gray work.
- No shade/publication operation remains in flight.
- Root/frame publications belong to the current epoch.

The coordinator then requests termination and parks application threads again. Threads finish
barrier critical sections and flush their publication state. Final root checks may reveal more work;
an earlier empty queue is not proof that marking has completed.

The initial residual-work target is at most a 64 KiB reference-scan batch or 200 microseconds of
active collector work before returning to concurrent marking when closure is not yet reached.
These are pacing targets. Thread scheduling and safepoint arrival can exceed them, so they do not
constitute a maximum pause guarantee.

If closure is not reached within the work budget, threads resume under the active barrier, workers
and assists continue, and termination is retried. The design does not conceal an unbounded closure
drain in a "short" termination phase.

## Sealing and sweeping

Successful termination seals the marked set. Weak resolution switches to the sealed-epoch rule:
unmarked targets cannot be promoted. Registry cleanup itself runs concurrently; sealing does not
require a stop-the-world traversal of every weak registration.

Application threads resume while sweep claims spans and large regions. Unswept storage is unavailable
for allocation. A new cycle cannot reuse epoch metadata while the prior sweep is still active.

## Progress under pressure

Repeated termination retries can starve reclamation if the application creates work faster than
workers drain it. The response is earlier triggering, bounded assists, and allocator pacing.
Exhausted reserve follows the controlled allocation-failure path. A hidden full-heap STW mark or
sweep is outside this design.

Validation must measure retry count and cause, residual work, thread-arrival time, active handshake
work, and time until reclaimable memory becomes available. A short individual retry with no eventual
progress is not a successful latency result.
