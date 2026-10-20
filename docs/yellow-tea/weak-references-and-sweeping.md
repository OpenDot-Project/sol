# Weak references and sweeping

Weak promotion and reclamation share one epoch-aware protocol. Merely making a weak slot atomic is
not enough to prevent a load from racing with object reuse. This page defines planned behavior;
neither weak handles nor the sweeper are implemented yet.

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
