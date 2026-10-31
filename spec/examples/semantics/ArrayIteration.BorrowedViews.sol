// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: view traversal carries the origin and access mode through each item load.
module spec.arrayiteration.borrowedviews

func dynamicArrayOwnsElements(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func spanBorrowsAnExistingRegion(values: Span<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func readonlySpanBorrowsReadOnlyRegion(values: ReadOnlySpan<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func sliceViewHasItsOwnLength(values: Span<int>, offset: int, count: int): int {
    val slice = values.slice(offset, count)
    var total = 0
    for val item in slice { total += item }
    total
}

func nestedSliceKeepsNestedOrigin(values: Span<int>, first: int, length: int): int {
    val outer = values.slice(first, length)
    val inner = outer.slice(1, length - 1)
    var total = 0
    for val item in inner { total += item }
    total
}

func expiredOwnerCannotYieldView(values: Span<int>): Span<int> {
    val copied = values
    for val item in copied { consume(item) }
    copied
}

func heapArrayCanOutliveSourceLocal(values: []int): []int {
    val copy = values
    for val item in copy { consume(item) }
    copy
}

func viewCannotBeStoredInOrdinaryObject(values: Span<int>): () {
    val holder = OrdinaryHolder(values)
    consume(holder)
}

func refStructCanStoreViewWithinScope(values: Span<int>): () {
    val holder = ViewHolder(values)
    for val item in holder.values { consume(item) }
}

func viewCannotBeCapturedInEscapingLambda(values: ReadOnlySpan<int>): () {
    val callback = { values[0] }
    save(callback)
}

func viewCanBeUsedByImmediateLambda(values: ReadOnlySpan<int>): int {
    useNow({ values[0] })
}

func refReturnCannotOutliveView(values: Span<int>): ref int {
    for val item in values { return ref item }
    return ref values[0]
}

func viewCannotCrossProcessBoundary(values: Span<int>): () {
    spawnWorker(values)
}

func viewCannotBePinnedByManagedRoot(values: Span<int>): () {
    for val item in values { collectGuestHeap() }
}

func arrayRootDoesNotExtendStackOrigin(values: Span<int>): () {
    for val item in values { collectGuestHeap() }
}

func viewIterationDoesNotAllocateOwner(values: Span<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func emptyViewHasNoBody(values: Span<int>): int {
    val empty = values.slice(0, 0)
    var count = 0
    for val item in empty { count += 1 }
    count
}

func viewBoundsAreValidatedAtCreation(values: Span<int>, offset: int, count: int): int {
    val slice = values.slice(offset, count)
    var total = 0
    for val item in slice { total += item }
    total
}

func viewLengthCannotExceedOrigin(values: Span<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

ref struct ViewHolder { val values: Span<int> }
class OrdinaryHolder(val values: Span<int>)

// A view can be traversed without allocating an iterator, but its byref-like origin and access mode
// remain part of its type and lifetime proof. Managed-array roots do not replace borrow tracking.
