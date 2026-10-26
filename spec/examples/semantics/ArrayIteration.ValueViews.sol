// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: spans traverse bounded views without silently boxing their origin.
module spec.arrayiteration.valueviews

func spanReadsOnlyItsWindow(values: Span<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func spanLengthIsViewLength(values: Span<int>): int {
    var count = 0
    for val item in values { count += 1 }
    count
}

func readonlySpanReadsOnlyItsWindow(values: ReadOnlySpan<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func spanWindowStartIsNotZeroInOrigin(values: Span<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func nestedSpanViewsKeepSeparateBounds(rows: []Span<int>): int {
    var total = 0
    for val row in rows {
        for val item in row { total += item }
    }
    total
}

func spanViewCanObserveWritesThroughSameOrigin(values: Span<int>): int {
    var index = 0
    var total = 0
    for val item in values {
        values[index] = item + 1
        total += item
        index += 1
    }
    total
}

func readonlySpanHasNoWriteRoute(values: ReadOnlySpan<int>): () {
    var index = 0
    for val item in values {
        // Expected mutability error at the span store.
        values[index] = item
        index += 1
    }
}

func spanItemRemainsAValueCopy(values: Span<int>): int {
    var total = 0
    for var item in values {
        item += 1
        total += item
    }
    total
}

func spanOwnerMustOutliveTraversal(owner: SpanOwner): int {
    var total = 0
    for val item in owner.values { total += item }
    total
}

func returnedSpanCannotOutliveOwner(owner: SpanOwner): Span<int> {
    for val item in owner.values { consume(item) }
    owner.values
}

func closureCannotCaptureStackSpan(values: Span<int>): () {
    for val item in values { save({ values[0] }) }
}

func copyingSpanHeaderDoesNotCopyElements(values: Span<int>): int {
    val copy = values
    var total = 0
    for val item in copy { total += item }
    total
}

func spanDerivedFromArrayRetainsOwner(values: []String): int {
    val view = values.asSpan()
    var total = 0
    for val item in view { total += item.length }
    total
}

func spanDerivedFromStackCannotBecomeHeapArray(values: Span<int>): []int {
    var result: []int = []
    for val item in values { result.add(item) }
    result
}

func readonlyToWritableSpanNeedsExplicitProof(values: ReadOnlySpan<int>): Span<int> {
    // Expected conversion error; read-only storage does not become writable through iteration.
    values
}

func defaultEmptySpanHasZeroTrip(): int {
    val values = Span<int>()
    var count = 0
    for val item in values { count += 1 }
    count
}

func emptySpanDoesNotRunBodyEffect(): () {
    val values = Span<int>()
    for val item in values { effect(item) }
}

func spanCursorDoesNotBox(values: Span<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func spanAndDynamicArrayAreDifferentViews(values: []int, view: Span<int>): int {
    var total = 0
    for val item in values { total += item }
    for val item in view { total += item }
    total
}

ref struct SpanOwner { val values: Span<int> }

// A span is a bounded view whose origin/lifetime remains explicit. Iteration cannot discard its
// starting offset, bounds, read-only status, or escape rules to reuse the dynamic-array ABI.
