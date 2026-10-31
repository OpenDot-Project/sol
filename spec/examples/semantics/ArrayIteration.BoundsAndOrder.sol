// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: array traversal is ascending, bounded, and does not overflow its index.
module spec.arrayiteration.bounds

func ascendingOrder(values: []int): []int {
    var result: []int = []
    for val item in values { result.add(item) }
    result
}

func oneVisitPerElement(values: []int): int {
    var visits = 0
    for val item in values { visits += 1 }
    visits
}

func zeroLengthHasNoIndex(values: []int): int {
    var visits = 0
    for val item in values { visits += 1 }
    visits
}

func firstIndexIsZero(values: []int): int {
    var first = -1
    for val item in values { first = item; break }
    first
}

func lastIndexIsLengthMinusOne(values: []int): int {
    var last = -1
    for val item in values { last = item }
    last
}

func lengthIsNotUsedAsAnElementIndex(values: []int): int {
    var last = -1
    for val item in values { last = item }
    last
}

func boundsGuardPrecedesElementAddress(values: []int): int {
    var result = 0
    for val item in values { result += item }
    result
}

func bodyNeverReadsAtLength(values: []int): int {
    var result = 0
    for val item in values {
        result += item
        if values.length == 0 { result += values[values.length] }
    }
    result
}

func explicitOutOfRangeStillFails(values: []int): int {
    // Expected runtime bounds failure unless the caller proves this index is in range.
    values[values.length]
}

func explicitNegativeIndexStillFails(values: []int): int {
    values[-1]
}

func bodyMayChangeArrayElementsNotLength(values: []int): int {
    var index = 0
    var total = 0
    for val item in values {
        values[index] = item + 1
        total += item
        index += 1
    }
    total
}

func bodyCannotResizeArray(values: []int): () {
    for val item in values {
        // Expected member-resolution error: dynamic arrays expose no add/remove resizing API.
        values.add(item)
    }
}

func rebindingSourceDoesNotChangeBound(values: []int, other: []int): int {
    var active = values
    var count = 0
    for val item in active {
        active = other
        count += 1
    }
    count
}

func emptyBodyDoesNotIncrement(values: []int): int {
    var increments = 0
    for val item in values {
        increments += 1
        break
    }
    increments
}

func continueIncrementsExactlyOncePerIteration(values: []int): int {
    var visits = 0
    for val item in values {
        if item < 0 { continue }
        visits += 1
    }
    visits
}

func breakDoesNotIncrementAgain(values: []int): int {
    var visits = 0
    for val item in values {
        visits += 1
        break
    }
    visits
}

func maximumLengthIndexDoesNotWrap(values: []int): int {
    // For length Int32.MaxValue, the last valid index is MaxValue - 1. The post-body increment
    // reaches MaxValue and the next signed comparison exits; it never wraps to a negative index.
    var visits = 0
    for val item in values { visits += 1 }
    visits
}

func oversizedAllocationFailsBeforeIteration(): int {
    // The allocation/runtime contract rejects a length that cannot be represented by the
    // iteration index before a loop begins.
    val values = allocateArrayWithLength(int.maxValue + 1)
    var total = 0
    for val item in values { total += item }
    total
}

func boundsFailureDoesNotBecomeCursorEffect(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func runtimeHeaderCorruptionIsNotSourceValue(values: []int): int {
    // Invalid object headers are rejected by runtime validation, outside source-level array values.
    var total = 0
    for val item in values { total += item }
    total
}

func nestedBoundsAreIndependent(rows: [][]int): int {
    var total = 0
    for val row in rows {
        for val item in row { total += item }
    }
    total
}

func outerBreakDoesNotAddressNextOuterElement(rows: [][]int): int {
    var count = 0
    for val row in rows { count += 1; break }
    count
}

func innerBreakDoesNotAdvanceInnerAgain(rows: [][]int): int {
    var total = 0
    for val row in rows {
        for val item in row { total += item; break }
    }
    total
}

func innerContinueUsesInnerBound(rows: [][]int): int {
    var total = 0
    for val row in rows {
        for val item in row { if item < 0 { continue }; total += item }
    }
    total
}

func sourceLengthReadIsSeparateFromBodyLengthReads(values: []int): int {
    var total = 0
    for val item in values { total += values.length + item }
    total
}

// The implicit traversal bound is one length snapshot. An explicit `.length` expression in the
// body is a separate source operation and does not replace or mutate that snapshot.
