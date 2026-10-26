// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: nested loops have distinct item slots, bounds, and transfer owners.
module spec.arrayiteration.nestedscopes

func rectangularSum(rows: [][]int): int {
    var total = 0
    for val row in rows {
        for val item in row { total += item }
    }
    total
}

func raggedSum(rows: [][]int): int {
    var total = 0
    for val row in rows {
        for val item in row { total += item }
    }
    total
}

func innerIterableIsReevaluatedForEachOuterElement(rows: []int): int {
    var total = 0
    for val outer in rows {
        for val inner in makeInnerArray(outer) { total += outer + inner }
    }
    total
}

func outerIterableIsEvaluatedOnce(rows: [][]int): int {
    var total = 0
    for val row in traceRows(rows) {
        for val item in row { total += item }
    }
    total
}

func innerIterableEvaluatesAfterOuterItem(rows: [][]int): int {
    var total = 0
    for val row in rows {
        for val item in selectInner(row) { total += item }
    }
    total
}

func innerEmptyArrayDoesNotSkipOuterBody(rows: [][]int): int {
    val empty: []int = []
    var outerCount = 0
    for val row in rows {
        outerCount += 1
        for val item in empty { outerCount += item }
    }
    outerCount
}

func outerEmptyArraySkipsInnerSourceEvaluation(rows: [][]int): int {
    var innerSources = 0
    for val row in emptyRows() {
        innerSources += 1
        for val item in traceInner(row) { innerSources += item }
    }
    innerSources
}

func nestedBindingShadowsByScope(rows: [][]int): int {
    var total = 0
    for val item in rows {
        for val item in item { total += item }
    }
    total
}

func siblingLoopBindingsAreIndependent(first: []int, second: []int): int {
    var total = 0
    for val item in first { total += item }
    for val item in second { total -= item }
    total
}

func nestedMutableBindingsAreIndependent(rows: [][]int): int {
    var total = 0
    for var row in rows {
        for var item in row {
            item += 1
            total += item
        }
        row = []
    }
    total
}

func innerBreakKeepsOuterTail(rows: [][]int): int {
    var count = 0
    for val row in rows {
        for val item in row {
            if item < 0 { break }
            count += 1
        }
        count += 100
    }
    count
}

func outerBreakLeavesInnerLoop(rows: [][]int): int {
    var count = 0
    for val row in rows {
        for val item in row { count += item }
        if count > 100 { break }
    }
    count
}

func innerContinueLeavesOuterLoopTransferAlone(rows: [][]int): int {
    var total = 0
    for val row in rows {
        for val item in row {
            if item == 0 { continue }
            total += item
        }
        total += 1
    }
    total
}

func outerContinueSkipsLaterInnerSource(rows: [][]int): int {
    var total = 0
    for val row in rows {
        if row.length == 0 { continue }
        for val item in traceInner(row) { total += item }
    }
    total
}

func nestedReturnRunsAllRequiredExits(rows: [][]int): int {
    defer { releaseOuterState() }
    for val row in rows {
        defer { releaseRow(row) }
        for val item in row {
            defer { releaseItem(item) }
            if item > 0 { return item }
        }
    }
    0
}

func nestedThrowLeavesInnerThenOuterScopes(rows: [][]int): () throws Failure {
    for val row in rows {
        defer { releaseRow(row) }
        for val item in row {
            defer { releaseItem(item) }
            if item < 0 { throw Failure("negative") }
        }
    }
}

func rowMutationDoesNotChangeSavedInnerLength(rows: [][]int): int {
    var total = 0
    for val row in rows {
        row = []
        for val item in row { total += item }
    }
    total
}

func innerSourceMutationDoesNotChangeActiveInnerArray(rows: [][]int): int {
    var total = 0
    for val row in rows {
        var active = row
        for val item in active {
            active = []
            total += item
        }
    }
    total
}

func loopLocalOutlivesOnlyItsLexicalScope(rows: [][]int): int {
    var total = 0
    for val row in rows {
        var count = 0
        for val item in row { count += item }
        total += count
    }
    total
}

func innerSlotIsNotVisibleAfterInnerLoop(rows: [][]int): int {
    var total = 0
    for val row in rows {
        for val item in row { total += item }
        // Expected name-resolution error: inner item scope ended.
        total += item
    }
    total
}

func outerSlotRemainsVisibleInInnerLoop(rows: [][]int): int {
    var total = 0
    for val row in rows {
        for val item in row { total += row.length + item }
    }
    total
}

func nestedZeroTripAssignment(rows: [][]int): int {
    var result: int
    for val row in rows {
        for val item in row { result = item }
    }
    // Expected error: either the outer or inner array can be empty.
    result
}

func nonEmptyOuterDoesNotProveInnerNonEmpty(rows: [2][]int): int {
    var result: int
    for val row in rows {
        for val item in row { result = item }
    }
    // Expected error: an inner dynamic array may be empty.
    result
}

func innerBreakDoesNotProveOuterEntry(rows: [][]int): int {
    var result = 0
    for val row in rows {
        for val item in row { result = item; break }
    }
    result
}

class Failure: Exception {}

// Each active loop contributes its own source snapshot, item slot, test, update, and exit. Nested
// lexical cleanup follows the dynamic nesting order; ordinary break and continue do not cross loops.
