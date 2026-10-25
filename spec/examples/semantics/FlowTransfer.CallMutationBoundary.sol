// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: local slot facts and heap facts have separate identities.
module spec.flow.callmutationboundary

class Entry(val code: int) {}

class Holder {
    var current: Entry?
}

func replace(holder: Holder): () {
    holder.current = null
}

func preserveImmutableSnapshot(holder: Holder): int {
    val snapshot = holder.current
    if snapshot == null {
        return 0
    }
    replace(holder)
    // Expected: snapshot is a stable local reference even when the source field is replaced.
    snapshot.code
}

func reloadMutableFieldAfterCall(holder: Holder): int {
    if holder.current == null {
        return 0
    }
    replace(holder)
    // Expected: the field test does not refine a later getter/field read across a mutating call.
    holder.current.code
}

func preserveUnrelatedLocal(holder: Holder, fallback: Entry?): int {
    val saved = fallback
    replace(holder)
    if saved == null {
        return 0
    }
    // Expected: a call with no alias to saved cannot invalidate its immutable local value.
    saved.code
}

func distinctSlotsCanHoldSameObject(holder: Holder): int {
    var first = holder.current
    var second = first
    if first == null {
        return 0
    }
    second = null
    // Expected: reassigning second leaves the first local slot and its object reference intact.
    first.code
}

func refWriteInvalidatesExactSlot(holder: Holder): int {
    var local = holder.current
    if local == null {
        return 0
    }
    clear(ref local)
    // Expected: clear can replace this exact local storage with null.
    local.code
}

func clear(ref entry: Entry?): () {
    entry = null
}

func outWriteDoesNotProveNonNull(): int {
    var entry: Entry?
    assignNull(out entry)
    // Expected: the out call definitely assigns entry, but its nullable type is unchanged.
    entry.code
}

func assignNull(out entry: Entry?): () {
    entry = null
}

func callbackWritesCapturedCell(holder: Holder): int {
    var local = holder.current
    if local == null {
        return 0
    }
    val clear = { local = null }
    clear()
    // Expected: a known invocation writes the mutable captured slot before this read.
    local.code
}

func callbackConstructionIsNotInvocation(holder: Holder): int {
    var local = holder.current
    if local == null {
        return 0
    }
    val clear = { local = null }
    // Expected: closure construction does not execute the assignment in its body.
    local.code
}

func unknownEscapingCallback(holder: Holder): int {
    var local = holder.current
    if local == null {
        return 0
    }
    retain { local = null }
    // Expected: an escaping mutable capture is not assumed to remain unchanged.
    local.code
}

func retain(action: () -> ()): () {}

func separateLocalAndFieldRefinements(holder: Holder): int {
    val snapshot = holder.current
    if snapshot == null {
        return 0
    }
    replace(holder)
    // Expected: the local snapshot and mutable field have different stability facts.
    snapshot.code
}

// Implemented today: source parsing and tree construction only. Planned: binding resolves slot,
// field, capture, ref, and out identities; flow preserves facts only while those identities are
// stable. A CST/AST success does not validate any Expected outcome in this fixture.
