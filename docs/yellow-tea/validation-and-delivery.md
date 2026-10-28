<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Validation and delivery

The runtime already has an experimental concurrent collector. This page defines the evidence needed to
extend and qualify it; it is not a claim that each future gate has passed. See the [current implementation
inventory](current-implementation.md) for what is present in Sol 0.1.0.

## Implementation progress and remaining gates

| Area | Current state | Remaining qualification |
|---|---|---|
| Descriptors, roots and barriers | Exact descriptors for the admitted runtime subset; generated root frames, strong/weak handles, insertion barriers and handshake participation. | General value layouts, closures, interface/object graphs and complete exceptional root ownership. |
| Allocation and reclamation | 44 fixed size classes, reusable span slots, dedicated large regions, committed/reserved accounting and epoch-sealed sweep publication. | Broader concurrent refill/return races, allocator pressure and workload qualification. |
| Concurrent collection | Background mark/sweep worker, resident mark/gray bits, bounded tracing and allocation assists, epoch-aware weak promotion. | Full language integration, scheduler diversity and complete pacing/locality measurements. |
| Native execution | Restricted AOT and process-isolated ORC paths share SolRT; dynamic and explicit static linkage are exercised. | General source exception unwinding, full object construction and broad ABI coverage. |
| Validation | ASan harnesses, deterministic state model, reachability comparisons, concurrency stress and compiler/runtime integration fixtures exist. | Expand coverage to every admitted layout and adversarial failure path; measure latency and throughput on stated workloads. |

The collector is experimental. A passing fixture proves only the paths and states it exercises. Later
performance results cannot replace correctness, ownership, or memory-ordering gates.

## Small-state model

The starting model contains up to eight objects, two application mutators, and one marker. It explores
mark-bit publication, gray/ready bits, store publication, root transfers, phase start/termination,
and weak promotion. Small finite exploration is evidence for the modeled protocol; it is not a
proof of all native implementation code or all possible heap sizes.

Required schedules include a producer paused after setting a mark bit, queue saturation, a scanner
finishing while a producer adds gray work, root replacement before an old edge disappears, and a
termination request during an in-flight barrier. The model must distinguish safe duplicate work
from unsafe lost work.

## Reachability oracle

A deterministic reference graph receives the same logical mutations as the runtime harness. At a
controlled cycle boundary, its reachable set is compared with the collector's retained set. Any
reachable object reclaimed by the implementation is a correctness failure.

The test oracle may stop test mutators and compute whole-graph reachability. That is a testing
technique, not a production full-heap STW fallback. Floating garbage retained until a later cycle
must be evaluated under the chosen barrier semantics rather than incorrectly reported as a lost root.

## Stress matrix

| Area | Required cases |
|---|---|
| Graphs | Cycles, deep chains, broad graphs, root-only transfers, rapidly replaced references. |
| Layout | Reference arrays, scalar arrays, nested structs, inline arrays, active enum payloads, nullable values. |
| Closures | Immutable captures, shared mutable cells, delegates, callback environments. |
| Mutation | Reference bulk copies with overlap, concurrent writes, full optional queues. |
| Allocation | All size classes, large objects, refill pressure, metadata failure, reserve exhaustion. |
| Threads | 1–64 threads, attachment/detachment races, delayed safepoints, long NativeSafe operations. |
| Exceptions | Nested catches/finally/defer, pending Throwable roots, rethrow, cleanup failure. |
| Native ownership | Handle create/resolve/release, callback shutdown, I/O completion and cancellation. |
| Weak references | Promotion before/after seal, reader retirement, target reuse, dead weak-owner entries. |
| Epochs | Double sweep attempts, unswept allocation rejection, bitmap parity, next-cycle admission. |

Random schedules must record a reproducible seed and minimize a failing execution. Stress success
does not excuse an unexplained model counterexample.

## Compiler and ABI checks

Generated-code tests inspect root slots, frame ownership, barriers on all reference-store categories,
poll placement, and normal/exceptional liveness. Optimization levels must preserve those facts.
ABI companion tests verify layout, alignment, argument passing, and unwind registration.

Both AOT and ORC must retain a pending Throwable during collection, preserve an interior base root,
and execute cleanup without double-popping frames. ORC code cannot run before its unwind tables are
registered. Native transitions must keep handles alive through actual completion, not merely through
an API's initiation call.

## Sanitizers, security, and workflow boundaries

Native harnesses use applicable sanitizer configurations to find bounds errors, use-after-free, and
other memory faults. Sanitizer success does not prove logical reachability or replace the memory-order
model. C# mutation tests remain frontend tests and must not be reported as native sanitizer coverage.

Repository automation uses PowerShell and Windows 2025 CI. Native build integration remains MSBuild.
Separate checks should expose native correctness, model tests, stress, and performance evidence rather
than burying failures in a monolithic job. Dependency and workflow scanning continue alongside these
tests; they do not validate collector semantics.

## Release evidence

Before a release claims an implemented collector, it needs an exact compiler/runtime/LLVM identity,
layout and entry-point qualification, reproducible model and stress results, and documented limitations.
Before it claims low latency, it also needs [latency and allocation measurements](pacing-and-diagnostics.md)
that include assists and safepoint waiting. There are currently no such release claims.
