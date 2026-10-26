// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative future contract: ref-like cursors and views remain within their permitted scopes.
module spec.arrayiteration.reflikelifetime

ref struct Cursor {
    var index: int
    val limit: int
    func moveNext(): bool = index < limit
    func current(): int = index
}

func refCursorCanBeConsumedInScope(limit: int): int {
    val cursor = Cursor(0, limit)
    var total = 0
    for val item in cursor { total += item }
    total
}

func refCursorCannotBeStoredInHeapField(limit: int): () {
    val cursor = Cursor(0, limit)
    val holder = CursorHolder(cursor)
    consume(holder)
}

func refCursorCannotBeCapturedByEscapingClosure(limit: int): () {
    val cursor = Cursor(0, limit)
    val callback = { cursor.moveNext() }
    save(callback)
}

func refCursorCannotBeReturned(limit: int): Cursor {
    val cursor = Cursor(0, limit)
    for val item in cursor { consume(item) }
    cursor
}

func refCursorCannotBeBoxedForInterface(limit: int): () {
    val cursor = Cursor(0, limit)
    val boxed: Iterator<int> = cursor
    consume(boxed)
}

func refCursorCannotCrossAwait(limit: int): () {
    val cursor = Cursor(0, limit)
    for val item in cursor {
        // A suspension point cannot be introduced while this ref-like cursor is live.
        workAsync(item)
    }
}

func refCursorCannotCrossYield(limit: int): () {
    val cursor = Cursor(0, limit)
    for val item in cursor {
        // A yield point cannot expose this ref-like cursor's scope to the caller.
        emit(item)
    }
}

func refCursorCannotBeCapturedByNestedLambda(limit: int): () {
    val cursor = Cursor(0, limit)
    for val item in cursor {
        useNow({ cursor.current() })
    }
}

func nonEscapingSynchronousBorrowIsAllowed(values: Span<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func readOnlyBorrowDoesNotPermitWrite(values: ReadOnlySpan<int>): () {
    var index = 0
    for val item in values {
        values[index] = item
        index += 1
    }
}

func mutableSpanWritesUseExplicitIndex(values: Span<int>): () {
    var index = 0
    for val item in values {
        values[index] = item + 1
        index += 1
    }
}

func spanItemIsStillAValue(values: Span<int>): int {
    var total = 0
    for var item in values {
        item = -item
        total += item
    }
    total
}

func spanOriginCannotBeForgottenOnReturn(values: Span<int>): Span<int> {
    for val item in values { consume(item) }
    values
}

func spanCannotBeCaptured(values: Span<int>): () {
    for val item in values {
        val callback = { values[0] }
        save(callback)
    }
}

func stackOnlyCursorMayHaveNoClose(): int {
    val cursor = Cursor(0, 3)
    var sum = 0
    for val item in cursor { sum += item }
    sum
}

func stackOnlyResourceCursorClosesInScope(cursor: StackResourceCursor): () {
    for val item in cursor { consume(item) }
}

func heapIteratorCanBeStoredWhenItsTypeAllows(cursor: Iterator<int>): () {
    val saved = cursor
    for val item in saved { consume(item) }
}

func refCursorDoesNotBecomeHeapIteratorByImplicitConversion(limit: int): () {
    val cursor = Cursor(0, limit)
    for val item in cursor { consume(item) }
}

func refCursorNestedScopeEndsBeforeOuterScope(limit: int): int {
    var result = 0
    {
        val cursor = Cursor(0, limit)
        for val item in cursor { result += item }
    }
    result
}

func refCursorNestedCleanupRunsBeforeOuterCleanup(limit: int): () {
    defer { closeOuter() }
    val cursor = ResourceCursor(limit)
    for val item in cursor { return }
}

func capturingItemNotCursorHasIndependentRule(values: []int): Vector<() -> int> {
    var callbacks: Vector<() -> int>
    for var item in values { callbacks.add({ item }) }
    callbacks
}

class CursorHolder(val value: Cursor)
class StackResourceCursor { func moveNext(): bool; func current(): int }

// Array rooting keeps a managed array alive. It does not authorize a ref-like cursor/span to escape,
// be boxed, or be captured. A borrow remains valid only through its scoped iteration use.
