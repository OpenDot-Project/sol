<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Weak references and sweeping

Weak promotion and reclamation share one epoch-aware protocol. Merely making a weak slot atomic is
not enough to prevent a load from racing with object reuse. An experimental handle and concurrent
sweeper baseline now exists with allocation-identity validation and epoch-sealed slot reuse;
span mark bitmaps, parallel sweep claims and reusable generation handle tickets remain pending.
See the [current implementation](current-implementation.md) before treating this design as implemented.

## Strong and weak ownership

A strong runtime handle participates in the root graph until released. A weak handle records a
target without keeping it alive. Source WeakRef objects are managed objects; their weak target slots
do not become ordinary strong fields in their tracing descriptor.

There is no user finalization, resurrection, ephemeron, or WeakMap fixed-point algorithm in the initial
collector. Native resources use explicit ownership and closing. Any future no-managed-allocation
native cleanup queue would require its own contract and would not invoke arbitrary source finalizers.

## Promotion during marking

While mark is active, weak resolution enters a coordinated critical section, loads the target,
shades it when necessary, and publishes a strong root for the returned value. The operation remains
in flight until that root is established. Mark termination cannot overlook the newly promoted edge.

A successfully returned reference must be alive and rooted before the resolver releases protection.
Returning a raw pointer first and asking generated code to root it afterward leaves an invalid race
window.

## Promotion after sealing

Once mark is sealed, a resolver may return only a target marked in the sealed epoch. An unmarked
target resolves as null even if its storage has not yet been reclaimed. The resolver cannot bring a
white object back into the live graph after the collector has decided it is unreachable.

Weak-registry cleanup runs concurrently. A physically uncleared slot may still represent a logically
dead target; the resolver checks epoch/mark state under protection. This avoids a full weak-registry
scan during termination.

## Reclamation and address reuse

Weak slot loads, clearing, reader epochs, and reclaim ownership use one synchronization protocol.
The sweeper waits until readers entitled to inspect or promote a target are no longer active before
reusing or releasing its storage. A later object occupying the same address must not be mistaken
for the old weak target.

The current native implementation serializes resolution with allocation-index retirement under the
heap mutex. Each collectible weak handle stores the allocation's monotonic identity as well as its
address. Resolution checks index membership, identity equality, and sealed mark state before reading
object memory or publishing the root. Index removal precedes storage reuse or release, so a later
allocation at the same address fails the identity check. Physical weak-record clearing is lazy on
resolution; the sweeper does not traverse the handle table for each dead object. A missing or replaced
target is cleared once and subsequent resolutions publish null. Process-lifetime immutable literals
are separately tagged and do not require a collectible allocation record. Allocation-identity
exhaustion fails rather than wrapping, and runtime shutdown does not reset that sequence.

Open strong handles have a separate intrusive root list. Closing removes an entry under the same
mutex, while a closed ticket remains in the validity table until shutdown. This bounds handshake
enumeration by active strong owners, but does not bound historical ticket memory. The opaque pointer
API still has no reusable generation ticket scheme. Handle creation requires a currently valid rooted
reference (or a process-lifetime literal); an arbitrary stale raw pointer is not a recoverable input.

The weak registry must also retire entries for unreachable WeakRef owners before the entries or
their owner storage are reused. Protecting targets while freeing registry entries under active
readers would be a separate use-after-free bug.

## Span sweep protocol

The sweeper compares the closed mark epoch with a stable allocation bitmap. A successful atomic
claim grants one worker sweep ownership of a span. It then identifies unreachable allocated slots,
performs the necessary weak-reader/reuse coordination, and publishes free slots for the allocator.

Unswept spans cannot accept new allocations. This prevents a new object from being erased by cleanup
for an earlier epoch. The allocator sees reclaimed capacity only after the span's swept state and
free-slot metadata are consistently published.

Large regions obey equivalent ownership and reader rules before native memory release. Dedicated
allocation does not exempt an object from handle or pending-exception lifetime protection.

## Epoch reuse

The initial design does not overlap two collection cycles. Sweep finishes before the next mark
begins. Mark-bitmap parity and clearing must follow span state; clearing a stale bitmap cannot race
with writes belonging to the next live epoch.

Required tests cover weak resolve before and after sealing, resolve versus target reuse, weak owner
reclamation, handle release versus sweeping, double sweep claims, epoch parity, and large-region release.
Debug poison/quarantine helps expose failures but does not replace the synchronization proof.
