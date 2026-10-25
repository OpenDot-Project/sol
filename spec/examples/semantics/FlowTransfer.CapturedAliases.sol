// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative capture fixture. Closure invocation and alias invalidation are separate edges.
module spec.flow.capturedaliases

class Entry {
    val value: int = 1
}

func capturedValueSnapshot(entry: Entry?): int {
    val snapshot = entry
    val inspect = { snapshot?.value ?: 0 }
    if snapshot == null {
        return inspect()
    }
    // Expected: immutable local storage retains its value refinement across a closure that
    // only reads it. The referenced object itself may still have mutable fields.
    inspect() + snapshot.value
}

func mutableCaptureCanReplaceValue(entry: Entry?): int {
    var current = entry
    val clear = { current = null }
    if current == null {
        return 0
    }
    clear()
    // Expected: invocation of a writer closure invalidates the old local refinement.
    current.value
}

func mutableCaptureNotInvoked(entry: Entry?): int {
    var current = entry
    val clear = { current = null }
    if current == null {
        return 0
    }
    // Expected: a not-yet-invoked closure does not execute its body. Whether its capture
    // invalidates the fact at creation is governed by escape and capture analysis.
    current.value
}

func invokeOnlyOnOneBranch(entry: Entry?, flag: bool): int {
    var current = entry
    val clear = { current = null }
    if current == null {
        return 0
    }
    if flag {
        clear()
    }
    // Expected: the join includes a path where the callback wrote null.
    current.value
}

func invocationOnTerminatingBranch(entry: Entry?, flag: bool): int {
    var current = entry
    val clear = { current = null }
    if current == null {
        return 0
    }
    if flag {
        clear()
        return 0
    }
    // Expected: the only continuing edge skipped the callback.
    current.value
}

func twoAliasesAreDistinctLocals(entry: Entry?): int {
    var first = entry
    var second = first
    if first == null {
        return 0
    }
    second = null
    // Expected: assigning the second local does not overwrite the first local slot.
    first.value
}

func sharedObjectStateIsNotAValueCopy(entry: Entry?): int {
    var first = entry
    var second = first
    if first == null {
        return 0
    }
    second = null
    // first and second previously referred to the same object. Rebinding second does not
    // mutate fields in that object or change first's local reference.
    first.value
}

func refArgumentKillsMutableFact(entry: Entry?): int {
    var current = entry
    if current == null {
        return 0
    }
    replace(ref current)
    // Expected: exact ref storage can be changed by the callee.
    current.value
}

func replace(ref entry: Entry?): () {
    entry = null
}

func outArgumentEstablishesOnlyItsWrittenType(): int {
    var entry: Entry?
    create(out entry)
    // Expected: assigned but still nullable; exact declared type is retained.
    entry.value
}

func create(out entry: Entry?): () {
    entry = Entry()
}

func closureWithOutEffect(): () -> () {
    var value: int
    val assign = { value = 1 }
    assign()
    // Expected: closure analysis tracks the assignment to the captured cell.
}

func conditionalClosureAssignment(flag: bool): int {
    var value: int
    val assign = { value = 1 }
    if flag {
        assign()
    }
    // Expected: value remains unassigned on the false branch.
    value
}

func callbackArgumentMayEscape(entry: Entry?): int {
    var current = entry
    if current == null {
        return 0
    }
    storeCallback { current = null }
    // Expected: if the callback escapes, the mutable captured value cannot be certified stable.
    current.value
}

func storeCallback(action: () -> ()): () {}

// Captures use resolved storage identity and lifetime. Immutable value capture copies the
// value; capturing a reference copies the reference, not the referenced object's fields.
// Mutable captures share a cell. Ref/out, ref struct and stack-borrow values cannot escape
// through a closure. Unknown escape facts remain unverified.
