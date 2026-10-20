# Heap and allocation

This page defines the initial heap design. No allocator or layout described here has been qualified
by a native implementation. Layout numbers are design inputs for the first runtime ABI, not a
promise of binary compatibility with an existing Sol release.

## Allocation units

Managed allocations have 16-byte alignment. An allocation whose total object header and body size
is at most 32,768 bytes uses a 64 KiB span. Larger allocations receive dedicated native virtual-memory
regions. Requested object size, rounded size-class consumption, committed memory, and reserved virtual
address space are separate accounting quantities.

The initial 44 small-object size classes, in bytes, are:

```text
16 32 48 64 80 96 112 128 144 160 176 192 208 224 240 256
320 384 448 512 640 768 896 1024
1280 1536 1792 2048 2560 3072 3584 4096
5120 6144 7168 8192 10240 12288 14336 16384
20480 24576 28672 32768
```

A class is selected from the complete allocation size, including headers. The difference between
that size and the selected class is allocator slack. Slack is not observable through source-level
layout queries and must be included in fragmentation measurements.

Span metadata lives outside user objects. Before a new span can serve allocations, its work-publication
metadata must exist; failure to provision it fails allocation before any object is published.

## Object and array layouts

| Object header field | Bytes | Purpose |
|---|---:|---|
| Type descriptor pointer | 8 | Immutable layout, tracing, type, and dispatch information. |
| Object flags | 4 | Runtime state; not the collector mark bit. |
| Runtime reserved field | 4 | Reserved by the runtime ABI. |

The fixed object header is 16 bytes. Identity hash storage is a lazy side-table responsibility rather
than a user-visible address calculation. Per-object mark state lives in span metadata.

The planned managed-array header is 32 bytes: the base header, a signed 32-bit element count,
an unsigned 32-bit element stride, and an 8-byte element descriptor pointer. Checked arithmetic
computes `header + length * stride` and alignment rounding. Negative lengths, overflow, and allocation
failure remain distinct failures; overflow must never turn into an undersized allocation.

Reference arrays trace each element. Arrays of reference-containing structs repeat the struct's exact
scan shape at the element stride. Scalar arrays contain no tracing edges. Inline `[N]T` arrays are
value storage whose containing descriptor or frame records their reference fields.

String storage contains immutable UTF-8 bytes. Length and any scalar-count cache are scalar fields,
not roots. A possible internal trailing NUL does not change the source-visible byte length or replace
explicit length handling at native boundaries.

## Span metadata

Each span records its size class, slot count, free count, allocation bitmap, two epoch mark bitmaps,
gray bitmap, sweep epoch, state, and allocator-list membership. The gray and ready metadata form a
resident work-publication path that does not need allocation in the write barrier.

The detailed state progression is:

```text
Free -> SweptAvailable -> Allocating -> SealedUnswept -> Sweeping -> SweptAvailable
```

These are ownership states, not permission for arbitrary concurrent mutation. A worker atomically
claims a sealed span before sweeping. Allocation cannot use an unswept span. Free-slot publication
occurs only after sweep has completed the necessary mark, weak-reader, and reuse checks.

Only one collection cycle is active. The next mark epoch starts after the previous sweep completes;
double-buffered mark parity must not clear the next epoch's bits while finishing the preceding one.

## Fast paths and initialization

Thread-local caches allocate from swept slots under thread ownership. Shared allocator lists are used
for refill and growth. Refill may incur [bounded assistance](pacing-and-diagnostics.md); application
allocation latency includes that work.

Before publication, reference slots are zeroed and the descriptor is installed. The compiler's
initialization verifier then establishes source-level validity. Zeroed storage alone is not a valid
non-null String field, initialized struct, or completed class instance.

During marking, new objects enter the current epoch as live/black allocations. Reference initialization
stores still use the insertion barrier: marking the new container does not automatically mark its
referents. Constructor arguments and temporary values remain roots until ownership transfers.

Objects are not returned to an allocation path before their metadata and storage are consistent.
Debug poisoning and quarantine may expose stale references, but they do not replace the publication
and reclamation protocol.

## Large objects and fragmentation

The large-object table tracks dedicated regions and their descriptors, allocation epochs, mark state,
and reclaim state. Release and reuse obey the same root, handle, and weak-reader constraints as small
objects. An address cannot be reused while a reader is still entitled to promote the previous object.

Non-moving collection permits external fragmentation. Measurements must separate span slack, free
slots in partially occupied spans, large-region retention, and reserved address space. Compaction is
outside the first algorithm; it cannot silently appear as a pressure fallback.
