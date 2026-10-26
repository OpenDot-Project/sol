<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Array and cursor iteration

This page defines `for-in` iteration and separates the language contract from the subset currently
accepted by verified FIR and the native backend. The complete source contract is broader than the
first native implementation. A successful parse of an example does not prove that the compiler can
analyze, lower, or execute it.

The executable native subset in this checkout accepts a non-null dynamic array `[]T` when its
element type has a qualified scalar or `String` representation. It does not yet accept fixed arrays,
`Vector<T>`, or user-defined iterator/cursor protocols in verified FIR. Source analysis has a wider
but still bounded contract. See [verified FIR and native programs](fir-native.md) for the complete
native admission list.

## Forms and terms

The surface form is:

```text
for val item in expression {
    use(item)
}

for var item in expression {
    item = transform(item)
}
```

`val` and `var` choose the mutability of the loop's item binding. They do not choose a collection
strategy. The iterable expression determines which iteration contract applies.

Sol distinguishes three relevant storage forms:

| Written type | Storage | Length behavior | Intended iteration route |
|---|---|---|---|
| `[]T` | Managed array reference | Fixed after allocation | Intrinsic array traversal or a source-level cursor view, according to the selected overload/context |
| `[N]T` | Inline fixed-size value | Exactly `N` elements | Intrinsic fixed-array traversal; not in the first native FIR subset |
| `Vector<T>` | Growable managed collection | May change through collection operations | Resolved iterator protocol; not interchangeable with an array |

An implementation must not treat every iterable as an indexed array. A cursor can perform I/O,
throw, own a native handle, be ref-like, or require deterministic cleanup. Those are observable
operations and require their own resolved identities and control-flow edges.

## Dynamic array iteration

For `[]T`, the array expression is evaluated exactly once, before the first body test. The compiler
retains the resulting array reference for the entire traversal. It obtains the array's fixed length
once after evaluating that expression. The iteration sequence is then the indexes from zero through
`length - 1`, in ascending order. A zero-length array takes the loop exit without entering the body.

This is a snapshot of the array reference and its immutable length, not a copy of the array contents.
The same array object remains the receiver for every element load. If the body writes an element by
index, a later indexed read observes that write. Rebinding the source variable does not retarget the
active traversal because the source expression was already evaluated. Dynamic arrays have no
resizing operation in this contract, so the saved length cannot become stale due to mutation.

The loop item is a local value binding. Each successful element load copies the element value into
that binding before the body begins. For primitive values, assignment to `var item` changes only the
local item. It never writes the new value back to the array. For reference elements, the copied
reference still denotes the same referenced object; mutating that object's state may be observable
through any alias, while assigning a new reference to the item does not replace the array element.

```sol
module docs.arrayiteration.copy

func localCopy(values: []int): int {
    var total = 0
    for var item in values {
        item += 10
        total += item
    }
    total
}

func arrayWriteIsExplicit(values: []int): () {
    for val item in values {
        // `item` is read-only. Updating the source requires an indexed assignment such as
        // `values[index] = replacement`; this loop form does not expose an implicit index.
        consume(item)
    }
}
```

The body may execute zero or more times. Therefore an assignment made only in the body does not
establish definite assignment after the loop. This remains true when the source array is known at
runtime to be non-empty unless the language can prove non-emptiness on every path. The initial native
FIR loop has no non-empty proof and keeps its zero-trip edge.

## Evaluation and mutation order

The order is observable and normative:

1. Evaluate the iterable expression from left to right according to ordinary expression rules.
2. If expression evaluation completes normally, preserve its resulting array reference.
3. Read the fixed array length exactly once.
4. Test the initial index against that length.
5. For each successful test, load the element at the current index and assign the loop item.
6. Run the loop body.
7. After normal body completion or `continue`, advance the index and poll the guest runtime before
   the next test. `break`, `return`, and an exception leave the loop without the next advance.

If the iterable expression calls a function, property getter, or other effectful operation, that
operation runs once even when the resulting array is empty. The body does not run for an empty array.
If the body reassigns the variable used by the iterable expression, the original evaluated reference
continues to govern this loop. If the body mutates an element through that original reference, later
loads observe the modified element value.

```sol
module docs.arrayiteration.evaluation

func selectArray(): []int throws SelectionFailure {
    recordSelection()
    [4, 5, 6]
}

func evaluateSourceOnce(): int throws SelectionFailure {
    var count = 0
    for val item in selectArray() {
        count += item
    }
    count
}

func rebindDoesNotRetarget(first: []int, second: []int): int {
    var active = first
    var total = 0
    for val item in active {
        active = second
        total += item
    }
    total
}
```

The example does not prescribe how the compiler represents the index. It prescribes the observable
results. An implementation may use a loop index and a saved length, a verified equivalent, or a
future optimizer transformation proven to preserve evaluation order, item copies, bounds behavior,
GC visibility, and transfer edges.

## Empty arrays

An empty array literal needs an element type from context:

```sol
module docs.arrayiteration.empty

func demonstrateEmptyArray(): () {
    val noValues: []int = []
    for val item in noValues {
        unreachableBody()
    }
}
```

The loop expression is evaluated normally. The length test is false, the item slot is not written,
and no body instruction executes. A body effect or exception cannot escape solely from a path that
cannot execute. The body still participates in parsing and semantic validation: malformed syntax,
invalid names, invalid assignment, and invalid types are not made acceptable by an empty input.
Effect propagation follows reachability, so an effectful operation in an unreachable body is not an
escaping effect of the containing function.

An untyped `[]` with no expected element type is ambiguous. A compiler must not invent an element
type to make a `for-in` loop work. It reports the normal empty-array inference diagnostic or leaves
the construct Deferred where the relevant analysis is not yet implemented.

Only an expression proven to be an empty literal is a static zero-trip proof. A variable, parameter,
property, or function call that happens to produce an empty array at runtime remains a possible
zero-or-more loop. Its body effects must be included because the value may instead be non-empty.

## Control transfer

The loop owns its body, item binding, continue target, and break target. A transfer targets the
nearest enclosing loop unless an explicitly supported label selects another target. The current
language contract does not permit an unimplemented label feature to be silently approximated.

| Transfer from body | Required behavior |
|---|---|
| Normal completion | Advance to the next index and test again |
| `continue` | Skip the remaining body, perform active per-iteration cleanup, advance, then test again |
| `break` | Leave this loop; do not advance; continue after the loop |
| `return` | Leave the function after required lexical cleanup |
| `throw` or a checked call failure | Follow the exceptional edge and required lexical cleanup |
| Bottom-valued expression | Has no normal continuation; enclosing edges retain the applicable transfer/effect facts |

For nested loops, `break` and `continue` target the innermost active loop. An inner-loop `break`
does not become an outer-loop `break`. Continuing an inner loop advances only its index. Returning
from a nested loop exits all active loops in order. The array traversal itself does not introduce
resource cleanup because a managed array has no cursor handle.

The conceptual CFG for a dynamic array contains these operations:

```text
evaluate iterable once
save reference and length
              ┌──────────────────────────────┐
              v                              │
          index < length?                    │
             /       \                       │
            no        yes                     │
            │          │                      │
            v          v                      │
           exit    load element               │
                       │                      │
                       v                      │
                  assign item                 │
                       │                      │
                       v                      │
                      body ── continue ──┐    │
                       │                 │    │
                       └── normal ───────┴─ advance/poll
                              break ─────────> exit
```

This conceptual graph is not a second language IR. FIR retains a typed array-iteration operation;
its verifier constructs a flow graph side table for assignment safety. LLVM lowering constructs
native blocks through the LLVM C++ API bridge.

## Definite assignment and refinements

The flow state before the loop is a zero-trip predecessor of the state after the loop. Body-only
assignments cannot be used after the loop:

```sol
module docs.arrayiteration.definiteassignment

func assignedOnlyWhenNonEmpty(values: []int): int {
    var last: int
    for val item in values {
        last = item
    }
    // Negative: the array may be empty, leaving `last` unassigned.
    last
}
```

The item is assigned before each body entry. A read of the item in the body is safe, but a local
declared in the body still follows ordinary definite-assignment rules. A `continue` cannot establish
an assignment skipped on that path. A `break` edge joins the post-loop state with the zero-trip edge.
A `return` does not join the normal continuation. An exceptional edge carries the state at the point
where the exception can happen; it cannot borrow assignments from a later statement.

Mutable refinements that are invalidated by calls, aliases, or assignments remain invalidated. The
loop item is a new local identity on each iteration. A refinement of the source array variable does
not change its storage identity. Reassigning a mutable captured source variable does not change the
array reference already retained by the loop.

## Checked effects

There are two separate sources of effects for dynamic array iteration:

| Operation | When it can run | Effect owner |
|---|---|---|
| Iterable expression | Once before the loop, even if the resulting array is empty | Enclosing expression/call |
| Array length read | Once after successful expression evaluation | Runtime operation; current array length read is non-throwing |
| Element address/load | Only after an in-range index test | Runtime bounds contract; verified loop guard must establish the range |
| Body operation | Zero or more times | Enclosing function if a reachable body path can execute |
| Per-iteration lexical cleanup | On the iteration exits covered by its registration | Cleanup transfer rules |
| Cursor `iterator` / `moveNext` / `current` | Cursor path only | Each selected operation's declared callable effects |
| Cursor `close` | Cursor path on defined close edges | Cleanup effect budget and suppression rules |

For an unknown dynamic array, the body may execute, so checked effects in the body remain obligations
of the caller. They do not disappear because the array could be empty. For a statically empty typed
literal, body paths are unreachable and do not contribute escaping effects, while the iterable
expression retains its ordinary effects. This distinction is based on a proven literal shape, not a
runtime guess.

The array runtime accessors used by verified native lowering do not create a user checked effect.
Index range comes from the loop invariant and saved non-negative length. A corrupted or invalid
runtime object is outside the source-language value model and belongs to runtime validation, not to
user exception inference.

## Cursor and resource iteration contract

The full source language also permits resolved `Iterable<T>` / `Iterator<T>` implementations and a
structural method pattern. A method-pattern source resolves an `iterator()` result, then resolves
`moveNext(): bool` and `current(): T` on that cursor. An optional `close(): ()` supplies deterministic
cursor cleanup. Resolution occurs before flow transfer and records the exact declaration, receiver,
substitution, and effects of every operation. Name-based guesses at runtime are not permitted.

Cursor calls are source operations, unlike intrinsic array indexing:

1. Evaluate the iterable expression once.
2. Call the selected iterator acquisition operation once, if the protocol requires one.
3. On each iteration edge, call `moveNext()` once.
4. Only when `moveNext()` returns true, call `current()` once.
5. Assign the returned value to the body-local item, then execute the body.
6. After normal completion or continue, perform iteration-scope cleanup before the next advance.
7. Close a resource cursor on exhaustion, break, return, and exceptional unwind as specified by its
   resolved cleanup contract.

The checked effects of `moveNext()` and `current()` belong to the iteration edge. `close()` has its
own effect identity and can fail while another exception is already active. A cleanup failure during
normal completion or break changes that transfer into an exceptional transfer. During an existing
exceptional unwind, cleanup failures follow the language's primary/suppressed failure rule; they do
not erase the original failure. Cleanup is LIFO across nested cursors and outer lexical scopes.

A plain `Iterator<T>` interface without `close()` does not acquire a synthetic cleanup effect.
Conversely, a `close()` requirement cannot be dropped because the statically visible interface omits
an effect annotation. The exact selected callable signature is preserved. Generic substitution
applies before effect comparison; base-interface effects cannot be widened at the call site.

Byref-like or ref-like cursors remain in scoped storage. The compiler cannot box them to satisfy an
interface, store them in a heap field, capture them in an escaping closure, or return them outside
their permitted lifetime. A failed lifetime proof is an error or Deferred result, never permission
to silently copy the cursor to the heap.

This cursor contract is normative language behavior, but it is not implemented by the current
verified FIR/native gate. The first native `FirArrayForEach` node represents a dynamic array only; it
contains no iterator calls, cleanup owner, exceptional edge, suppression state, or cursor lifetime
metadata. User cursor iteration must remain outside that verified subset until those obligations are
represented and checked.

## Garbage collection and bounded progress

The native backend retains the array reference as a strong root across the entire traversal. The
current Yellow Tea implementation is non-moving; the retained pointer remains a valid address across
polls. Every reference-typed item store uses the runtime's traced store path. The array's backing
storage owns reference elements and participates in array descriptor tracing. A temporary expression
result must be published as a root before a later operation can reach a collection poll.

The loop performs a guest collection poll on its backedge, after a completed iteration or a
`continue`. It does not poll on `break`, `return`, or a body path that throws before the backedge. The
poll keeps progress bounded by the work between loop backedges under the runtime's current
coordination model; it is not a hard real-time latency promise. A long-running individual body
operation can exceed that interval.

The array length is read once and the array is held strongly. An implementation must not retain an
unrooted raw element pointer across an allocating call or collection poll. It must not hand the GC an
interior pointer unless the runtime explicitly supports and roots that interior location. The current
backend computes an element address only for the immediate load or store.

## FIR and LLVM qualification

The first verified representation is `FirArrayForEach(ItemSlot, Array, Body, Origin)`. It retains the
typed source expression, the exact body item slot, and the body. The item slot is tied back to the
single source binding and to the element type of the dynamic array. The verifier also checks that the
item is a local value slot, not a parameter alias or a forged unrelated storage location.

The FIR flow graph models one iterable-expression evaluation, a zero-trip exit edge, an item write
before body entry, continue-to-step behavior, backedges, and break-to-exit behavior. A literal with a
known zero length keeps only the zero-trip path. The source body remains structurally validated even
when its flow path is unreachable.

LLVM lowering evaluates the expression once, calls the native length accessor once, keeps the array
root alive, and emits a guarded index traversal. The index and saved length use function-entry local
storage so a foreach nested in another loop does not allocate unbounded stack storage on each
re-entry. Break exits directly. Continue runs the step, stores the next index, polls the guest runtime,
and returns to the test block. Each element is loaded with the verified element width and stored to
the verified item slot.

The supported element storage kinds are `bool`, `sbyte`, `byte`, `short`, `int`, `uint`, `long`,
`ulong`, `single`, `double`, and `String`. A newly parsed or analyzed type does not become native
supported automatically. `half`, `quad`, nested arrays, nullable arrays, fixed arrays, generic body
specialization, and user-defined cursor protocols remain gated unless the verifier and runtime
layout explicitly qualify them.

FIR JSON includes the new statement shape. Its output schema is version 3 because consumers must
distinguish the added closed-set node kind. The schema version is not a stable library ABI identifier.
The flow graph remains verification metadata and is not serialized as executable IR.

## Diagnostics and Deferred results

The source analyzer reports ordinary type, binding, definite-assignment, nullability, and checked
effect failures at their source origins. It must not invent an item type when the iterable shape is
unresolved. A source type without an intrinsic array type or fully resolved cursor protocol is not
assumed to be indexable.

The verified FIR builder rejects unsupported array element layouts, non-single item bindings,
missing source identity, fixed-array iteration, and cursor-shaped `for-in` with `SOL2400` or a more
specific supported diagnostic. It must not emit an empty body in place of an unsupported loop. FIR
verification independently checks node identity and exact types before LLVM sees the program.

Resource limits, cancellation, or incomplete member/effect resolution may yield Deferred analysis.
Deferred is not success, and callers must not lower it. A successful parse only establishes syntax.
A successful body analysis only establishes the facts represented by that analysis. A successful FIR
verification qualifies only the closed subset checked by the verifier. Native execution evidence is
required before a runtime behavior is called implemented.

## Semantic evidence matrix

Each source rule has a designated owner and evidence source. The semantic fixtures under
`spec/examples/semantics/ArrayIteration.*.sol` are individually named to isolate these dimensions:

| Dimension | Required evidence |
|---|---|
| Dynamic, fixed, and growable collection distinction | Shape/type fixtures |
| One evaluation of the source expression | Evaluation-order fixtures |
| Saved reference after source-local rebinding | Reference-identity fixtures |
| Saved length and ascending order | Sequence fixtures |
| Primitive item copy and val/var enforcement | Binding fixtures |
| Reference element aliasing and explicit indexed writeback | Reference-element fixtures |
| Empty typed literal and untyped empty inference | Empty-array fixtures |
| Zero-trip assignment join | Definite-assignment fixtures |
| Body checked effects and empty-literal reachability | Checked-effect fixtures |
| Break, continue, return, throw, and nested loops | Transfer fixtures |
| Per-iteration `defer` edge ordering | Cleanup fixtures |
| Iterator acquisition/advance/current/close order | Cursor fixtures |
| Cursor close failure during normal and exceptional exits | Cursor-unwind fixtures |
| Ref-like cursor escape and no-boxing requirements | Cursor-lifetime fixtures |
| Array roots, item roots, poll sites, and interior pointers | Runtime-root fixtures |
| FIR source binding, slot type, and body-origin validation | Native-qualification fixtures |
| Unsupported layouts and nodes | Negative qualification fixtures |

The fixture index states the intended purpose of each file. A fixture can document a rule not yet
implemented; the compiler version status and CI results determine implementation coverage. See
[flow and checked effects](flow-and-effects.md) for the shared exceptional-state and cleanup model.

## Scope summary

| Feature | Language contract | Source analysis | Verified FIR/native backend |
|---|---|---|---|
| Non-null dynamic `[]T` | Defined | Bounded source flow | First element subset qualified |
| Empty typed dynamic array | Zero body runs | Static literal path is unreachable for effects | Runtime guard exits before body |
| Fixed `[N]T` | Defined as inline value iteration | Element binding recognized in bounded analysis | Not qualified by this FIR node |
| `Vector<T>` | Uses collection iterator contract | Deferred until exact protocol resolves | Not qualified |
| User `Iterable` / `Iterator` | Exact method and effect contract | Deferred until exact protocol resolves | Not qualified |
| Resource cursor with `close()` | Close on defined exit/unwind edges | Future analysis obligation | Not qualified |
| Ref-like cursor | Scoped, non-boxed lifetime | Future analysis obligation | Not qualified |

The table is deliberately versioned with implementation status. When another collection family
becomes verified, its exact lowering and lifetime behavior must be documented and tested at the
appropriate layer rather than inferred from the array case.

## Cost model and implementation obligations

Intrinsic dynamic-array traversal is O(n) in the number of elements visited. It does not allocate an
iterator object and does not copy the full array. Its loop-specific auxiliary storage is one saved
reference, one saved length, one signed index, and the body item's local slot. The array itself and
any objects referenced by its elements remain governed by normal guest-heap allocation and tracing.

| Work item | Required frequency | Asymptotic contribution |
|---|---:|---:|
| Evaluate iterable expression | Once | Same as the expression |
| Read array length | Once | O(1) |
| Compare current index with saved bound | Once before each attempted iteration, including exit | O(n + 1) |
| Compute element address and load | Once for each entered iteration | O(n) |
| Store item binding | Once for each entered iteration | O(n) |
| Advance index and poll | Once after each normally completed or continued iteration | O(n) |
| Copy array contents | Never for intrinsic traversal | O(1) auxiliary traversal work |

The result is not specified as a particular assembly instruction sequence. Optimization may remove a
redundant local store or combine operations only when the observable source expression count, item
copy, bounds safety, assignment facts, exception effects, root visibility, and poll-progress rules
remain equivalent. The compiler may not eagerly copy every element just to make a later alias check
easier; doing so changes allocation and memory behavior and can amplify memory pressure.

Array lengths and indexes use the language's signed `int` domain in the first implementation. A valid
array length is non-negative and no greater than `int.maxValue`. If the array has length
`int.maxValue`, its final valid index is `int.maxValue - 1`; after that body completes, the next index
is exactly `int.maxValue`, which fails the `< length` test without wrapping. A source value whose
allocation length cannot be represented is rejected before traversal. A forged object header is a
runtime validation failure, not permission to use unchecked pointer arithmetic.

The native loop's root lifetime is lexical and bounded. The saved array reference must be in a strong
guest root before a body call or GC poll can happen. Each reference item copied into a local root uses
the runtime's traced-store operation. A scalar item does not need an object-reference slot. Nested
loops each retain their own source reference and saved bound; an inner-loop poll cannot invalidate an
outer source root. At loop exit, return, or break, ordinary root-frame cleanup makes loop-temporary
references eligible for collection once no other root remains.

| Runtime property | Qualification rule |
|---|---|
| Header and descriptor | Descriptor is validated and declares the element kind and byte stride used by the address operation. |
| Alignment | Element addresses satisfy the alignment required by the qualified kind; a wider type cannot be accessed using a byte stride. |
| Reference tracing | The array descriptor marks reference slots; backing storage is not treated as an opaque scalar block. |
| Store barrier | Replacing a reference element publishes the new value through the runtime barrier. |
| Interior pointers | An element address is consumed immediately; it is not retained across an allocation, call, loop backedge, or poll. |
| Pinning and movement | The current Yellow Tea heap is non-moving. If compaction or movement is later added, saved references must be updated or pinned under an explicit contract. |
| Host and guest | Sol objects are not passed to the compiler's CLR heap. The executable guest owns the Yellow Tea heap in its own process. |

The loop poll happens on the backedge after iteration cleanup and before testing the next index. This
allows an active concurrent cycle to make progress across long traversals without inserting a
full-heap stop-the-world scan on every element. Poll frequency is not a fixed wall-clock bound: one
body call can block or perform substantial work, and Windows scheduling affects responsiveness.
This contract is a cooperation boundary, not a real-time guarantee.

## Source and backend conformance checklist

The language contract and the current implementation gate are reviewed separately. Use the following
checks when extending iteration support:

1. **Source category:** identify dynamic array, fixed array, standard collection, interface cursor,
   structural method-pattern cursor, or ref-like view from resolved types. Do not infer from the
   member name `length` or from runtime object shape.
2. **Source evaluation:** prove one evaluation at the specified point. Include getter, function-call,
   receiver, indexer, coalescing, and conditional expressions in the ordering review.
3. **Item contract:** resolve one item type and mutability. Verify whether the item is a value copy,
   object reference copy, ref-like borrow, or explicitly writable reference. Never let a mutable local
   item imply array writeback.
4. **Zero-trip state:** preserve the initial exit edge. A type-level fixed length or literal shape may
   prove a narrower case; runtime inputs and arbitrary calls may not.
5. **Backedges:** give normal completion and continue the same step target, after lexical iteration
   cleanup. Give break a direct exit target. Associate nested transfers with the innermost loop.
6. **Effects:** attach iterable, factory, advance, current, body, cleanup, and close effects to their
   own operations. Join only reachable escaping edges after catch subtraction.
7. **Exceptional state:** snapshot assigned locals, required fields, refinements, aliases, and active
   cleanups at the failing operation. Do not use a later item write to repair an earlier edge.
8. **Rooting:** inventory source-expression and item references, ensure root stores precede polls, and
   avoid retaining an untracked interior address.
9. **Mutation:** distinguish fixed array element stores from collection structural mutation. Apply the
   collection's documented invalidation/version contract only to the selected collection cursor.
10. **Serialization:** version FIR JSON when adding a closed node kind; keep type and slot IDs local to
    a compilation and do not call them stable package ABI identifiers.
11. **Diagnostics:** anchor a failure to the source expression, binding, body statement, or unsupported
    lowering node that owns the obligation. Do not replace unresolved work with an empty loop.
12. **Implementation status:** publish separate evidence for parse, source analysis, verified FIR,
    LLVM construction, AOT/ORC execution, GC stress, and ABI qualification. An example alone is not
    evidence that every layer supports its behavior.

### Review questions for a dynamic-array change

| Review question | Expected answer for the qualified path |
|---|---|
| Can the iterable call run more than once? | No; its source expression is emitted once. |
| Can the body start when the saved length is zero? | No. |
| Can a body-only assignment establish a value after a possibly-empty loop? | No. |
| Can assignment to `var item` change the array slot? | No; explicit indexed store is required. |
| Can assignment through an alias change a later array load? | Yes; the same object is retained and element loads occur at their turn. |
| Does `continue` close a dynamic array? | No resource close exists; it runs lexical cleanup and reaches the step/poll edge. |
| Does break perform the step/poll? | No; it exits directly after applicable cleanup. |
| Does a nested loop reuse and overwrite the outer bound? | No; each loop owns distinct function-entry local slots. |
| Can a `String` item be unrooted while the body allocates? | No; the item and source array must remain visible to guest tracing. |
| Can a generic cursor use this node? | No; the node represents a dynamic array and contains no cursor operation metadata. |

### Extension checklist for fixed arrays and collections

Fixed arrays need layout rules for inline aggregate values, element copy behavior, and compile-time
length proofs. Their source is not a managed-array pointer, so the current `sol_rt_array_length` and
`sol_rt_array_address` calls cannot be reused blindly. A fixed-array node must preserve inline address
ownership and any byref-like aggregate restrictions.

`Vector<T>` and other standard collections need an exact cursor contract. In addition to acquisition,
advance, and current-value operations, the implementation must state whether mutation is fail-fast,
snapshot-based, or otherwise defined. It must identify which operations increment structural
versioning, when a cursor checks the version, and which checked or unchecked error results. Hash
table resize, tree rebalancing, ring-buffer wrap, queue enqueue, and priority-queue ordering are not
all the same mutation.

Resource iteration needs cleanup edges on normal exhaustion, break, return, and exceptional unwind.
The cursor is registered for cleanup only after successful acquisition. A failure in `moveNext` or
`current` still closes an acquired cursor. A cleanup failure while another exception is active must
preserve primary identity and the language's suppression ordering. Nested cursor close is inside-out
and composes with lexical `defer` and `finally`.

Span and ref-like cursor iteration additionally need a lifetime proof. The compiler cannot box the
cursor to satisfy an interface or retain a pointer past its permitted origin. Byref-like calls need
receiver passing and ABI rules that preserve stack origin. An implementation should make an
unsupported lifetime explicit rather than silently heap-allocate a copy.
