// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: intrinsic traversal preserves the specified order of a guarded index loop.
module spec.arrayiteration.indexedcomparison

func foreachSum(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func indexedSum(values: []int): int {
    var total = 0
    var index = 0
    while index < values.length {
        total += values[index]
        index += 1
    }
    total
}

func bothUseAscendingOrder(values: []int): bool {
    sequenceFromForeach(values) == sequenceFromIndex(values)
}

func eachSourceExpressionRunsOnce(values: []int): int {
    var total = 0
    for val item in countedSource(values) { total += item }
    total
}

func manualIndexSourceExpressionRunsOnce(values: []int): int {
    val source = countedSource(values)
    var total = 0
    var index = 0
    while index < source.length { total += source[index]; index += 1 }
    total
}

func foreachItemIsCopy(values: []int): int {
    var total = 0
    for var item in values { item += 1; total += item }
    total
}

func indexedStoreIsExplicit(values: []int): int {
    var index = 0
    var total = 0
    while index < values.length {
        val item = values[index]
        values[index] = item + 1
        total += values[index]
        index += 1
    }
    total
}

func foreachDoesNotExposeImplicitIndex(values: []int): int {
    var seen = 0
    for val item in values { seen += item }
    seen
}

func indexLoopMustGuardBeforeRead(values: []int): int {
    var total = 0
    var index = 0
    while index < values.length {
        total += values[index]
        index += 1
    }
    total
}

func unguardedIndexReadCanFail(values: []int): int {
    values[values.length]
}

func breakEquivalentToIndexExit(values: []int): int {
    var total = 0
    for val item in values { if item == 0 { break }; total += item }
    total
}

func continueEquivalentToIndexIncrement(values: []int): int {
    var total = 0
    for val item in values { if item < 0 { continue }; total += item }
    total
}

func nestedForeachUsesNestedIndices(rows: [][]int): int {
    var total = 0
    for val row in rows { for val item in row { total += item } }
    total
}

func nestedIndexLoopsUseIndependentCounters(rows: [][]int): int {
    var total = 0
    var rowIndex = 0
    while rowIndex < rows.length {
        val row = rows[rowIndex]
        var column = 0
        while column < row.length { total += row[column]; column += 1 }
        rowIndex += 1
    }
    total
}

func sourceIsNotRecomputedAtEachIndex(values: []int): int {
    var total = 0
    for val item in statefulSource(values) { total += item }
    total
}

func lengthSnapshotNotElementSnapshot(values: []int): int {
    var total = 0
    for val item in values {
        if item < 0 && values.length > 0 { values[0] = item }
        total += item
    }
    total
}

func currentItemLoadOccursAtItsTurn(values: []int): int {
    var total = 0
    for val item in values { mutateLaterElement(values); total += item }
    total
}

func noEagerElementCopyBeforeBody(values: []Cell): int {
    var total = 0
    for val item in values { mutateLaterObject(values); total += item.value }
    total
}

func explicitIndexExpressionHasItsOwnEffect(values: []int): int {
    var total = 0
    for val item in values { total += values[nextIndex()] + item }
    total
}

func foreachStillUsesCompilerOwnedIndex(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

class Cell { var value: int }

// A lowering may use an internal index, but it does not expose that index as source state. An eager
// snapshot or repeated evaluation of the iterable is not equivalent to the manual guarded loop.
