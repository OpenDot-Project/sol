# Yellow Tea

Yellow Tea is the planned native garbage collector for Sol programs on Windows x86-64. Its initial
algorithm is precise, non-moving, non-generational concurrent mark and sweep. Marking and sweeping
run alongside application threads. Cycle start and mark termination use coordinated thread handshakes.

**Implementation status:** this section specifies the intended collector. The repository currently
has no implemented Yellow Tea allocator, collector workers, generated root frames, or native GC test
harness. Solc's frontend tests and the tool's private .NET runtime do not validate this design.
There are no measured pause, throughput, memory-overhead, or production-readiness claims.

## Ownership

The compiler determines object layouts, reference locations, root liveness, safepoint placement, and
the insertion of barrier operations. The native runtime owns allocation, thread registration, phase
coordination, tracing work, strong and weak handles, and reclamation. These responsibilities meet at
the [compiler/runtime ABI](compiler-runtime-abi.md).

Yellow Tea manages the heap of compiled Sol applications. It does not collect the CLR heap used by
Solc or the other development tools. A native object address is not a CLR object reference. The
managed compiler and any LLVM bridge are outside the collector's application heap.

## Design boundaries

| Property | Initial contract |
|---|---|
| Root and object identification | Exact descriptors and registered root slots; no conservative scanning. |
| Object movement | No relocation or compaction. Addresses remain stable while objects remain alive. |
| Reachability algorithm | Concurrent tracing with an insertion/update barrier. |
| Generations | None; there is no nursery or generational card table. |
| Reclamation | Concurrent, span-based sweep, with exclusive sweep ownership. |
| Pauses | Start and termination handshakes; no production full-heap STW fallback. |
| Weak references | Epoch-aware promotion to strong roots; no resurrection after mark sealing. |
| Resource cleanup | Explicit resource ownership; no arbitrary source finalizers. |
| Excluded initial features | Moving collection, read barriers, concurrent stack scanning, ephemerons, resurrection. |

Non-moving does not imply immortal: a raw address becomes invalid after its last owning root or
handle disappears and a collection reclaims the object. Stable addresses also do not provide
synchronization between application threads.

## Latency goals and limits

The design avoids tracing the entire heap while application threads are stopped. It still has
pause sources: thread arrival at safepoints, publication of roots, initial root scanning, and bounded
termination work. More threads or roots can increase these costs even when heap size is unchanged.

Allocation assists and allocator throttling can delay application code while other threads continue.
Those delays must appear in allocation-latency measurements. A short handshake statistic alone does
not demonstrate low application latency. No zero-STW, hard-real-time, or workload-independent pause
bound is part of this contract.

## Reading order

1. [Heap and allocation](heap-and-allocation.md): spans, size classes, layouts, and publication.
2. [Roots and safepoints](roots-and-safepoints.md): liveness, thread states, native transitions.
3. [Collection cycle](collection-cycle.md): phase transitions and bounded termination attempts.
4. [Barriers and memory ordering](barriers-and-memory-ordering.md): preservation of reachable edges.
5. [Weak references and sweeping](weak-references-and-sweeping.md): promotion and reclamation races.
6. [Compiler/runtime ABI](compiler-runtime-abi.md): descriptors, runtime entry points, and unwinding.
7. [Pacing and diagnostics](pacing-and-diagnostics.md): budgets, assists, configuration, and telemetry.
8. [Validation and delivery](validation-and-delivery.md): model, stress, sanitizer, and performance gates.

The [repository status](../status.md) distinguishes existing frontend capabilities from planned native
execution. Documentation of an ABI or state machine does not mean that its implementation exists.
