<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Roots and safepoints

Yellow Tea's tracing contract is precise. The compiler and runtime must enumerate every live managed
reference using typed metadata; an integer that resembles an address is not a root. Generated roots,
barriers, entry/backedge polls, and native-safe transitions now exist for the restricted native subset.
The complete inventory below extends beyond the [current implementation](current-implementation.md).

## Root inventory

| Root owner | Required live values |
|---|---|
| Registered managed thread | Active shadow frames, live reference temporaries, and reference-containing values. |
| Module initialization | Lazy `val`/`var` state, completed values, partially initialized storage, cached failure causes. |
| Singleton initialization | Object/companion instances and initialization failure records. |
| Exception machinery | Pending Throwable, native exception carrier ownership, catch locals, cause and suppressed values. |
| Closures and delegation | Closure environment, shared mutable capture cells, stored delegates and their managed payloads. |
| Native boundary | Strong handles, registered callbacks, retained managed I/O buffers. |
| Runtime services | Queued task/channel/timer payloads and other explicitly registered managed owners. |
| Entry adapter | `Main`, initialization state, and the argument `[]String`. |

Weak handles do not enter this strong root inventory. Resolving one may create a strong root through
the separate [weak-promotion protocol](weak-references-and-sweeping.md).

## Shadow frames

A root frame contains a previous-frame pointer, an immutable frame descriptor, and root slots.
The descriptor supplies root count and exact scan information, with source/debug liveness data.
Frames are registered before an operation can expose their references to collection.

Registers may hold copies between safepoints. Before a thread acknowledges a collection request,
the compiler must spill every live reference needed by its frame descriptor into published root
storage. Optimizers cannot delete, reorder beyond the handshake, or prematurely end these stores.

Root slots use the same insertion protection as heap fields while marking is active. Reading an
old heap edge and deleting it is safe only when the copied value remains protected as a temporary,
root, or barrier-published edge throughout that transition.

Normal return and exceptional cleanup pop each frame exactly once. A `defer` block, `finally`, or
exception carrier can extend liveness beyond the last ordinary source read. Frame metadata must
cover that extension rather than relying on accidental stack contents.

## Interior locations and stack-only values

An interior reference is represented by an owning base reference and an offset. The base is the
root. A raw pointer into an array or object is not sufficient lifetime information, even though
the collector does not move that object.

`ref`/`out` parameters, borrowed spans, guards, and ref structs must retain their owning roots for
their entire proven lifetime, including exception edges. Stack-only storage cannot escape into a
heap object or an escaping closure. Compile-time escape analysis and runtime root tracing solve
different problems; neither substitutes for the other.

## Thread states

```text
Attaching -> ManagedRunning -> ParkRequested -> Parked -> ManagedRunning
                    |                                ^
                    +---------> NativeSafe -----------+
Registered thread -> Detaching -> removed from the registry
```

Attach completes TLS, frame, and registry initialization before source execution. Detach removes a
thread only after releasing its frame and callback responsibilities. The phase coordinator cannot
lose a running thread by observing an incomplete registration or removal.

On a park request, the thread completes its current barrier critical section, publishes roots with
release ordering, and acknowledges the requested epoch. Parked threads resume only under the
coordinator's phase transition. A stale cached phase flag cannot permit an unprotected reference store.

## Safepoint placement

Planned polling points include function entry, allocating calls, loop backedges, and native transitions.
Straight-line nonallocating code receives a poll when the estimated instruction budget would exceed
1,024 instructions. This is a compiler work heuristic, not a wall-clock bound.

Root scanning cost depends on root count and thread count. Time waiting for the slowest thread is
reported separately from active collector work. A long native operation should enter NativeSafe
rather than hold the entire collection waiting for managed execution that is not occurring.

## NativeSafe and callbacks

Entering NativeSafe publishes the thread's managed roots first. Native code in this state cannot
arbitrarily inspect or mutate the managed reference graph. It may retain an approved byte/scalar
buffer under a strong runtime handle; reference-containing buffers require a registered, barrier-aware
adapter or an explicit copy.

Callback entry attaches an unregistered thread or performs the NativeSafe-to-managed handshake before
accessing Sol objects. A raw object address obtained earlier does not authorize access from an
unregistered thread. Shutdown and callback lifetime checks must reject an invalid callback before
calling a released code pointer or accessing detached runtime state.
