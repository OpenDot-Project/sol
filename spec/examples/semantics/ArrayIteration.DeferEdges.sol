// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: loop-body cleanup registration is per entered iteration and transfer-specific.
module spec.arrayiteration.deferedges

class CleanupFailure: Exception {}
class BodyFailure: Exception {}

func closeItem(item: int): () throws CleanupFailure {}
func cleanupOuter(): () throws CleanupFailure {}

func deferRunsOnNormalCompletion(values: []int): () throws CleanupFailure {
    for val item in values {
        defer { closeItem(item) }
        consume(item)
    }
}

func deferRunsBeforeNextIteration(values: []int): () throws CleanupFailure {
    var expected = 0
    for val item in values {
        defer { verifyClosedBefore(expected) }
        expected += 1
        consume(item)
    }
}

func deferRunsOnContinue(values: []int): () throws CleanupFailure {
    for val item in values {
        defer { closeItem(item) }
        if item < 0 { continue }
        consume(item)
    }
}

func deferRunsOnBreak(values: []int): () throws CleanupFailure {
    for val item in values {
        defer { closeItem(item) }
        if item == 0 { break }
    }
}

func deferRunsOnReturn(values: []int): int throws CleanupFailure {
    for val item in values {
        defer { closeItem(item) }
        return item
    }
    0
}

func deferRunsOnThrow(values: []int): () throws CleanupFailure, BodyFailure {
    for val item in values {
        defer { closeItem(item) }
        throw BodyFailure("body")
    }
}

func deferFailureOnNormalPath(values: []int): () {
    // Expected checked-effect error: any entered iteration may fail during cleanup.
    for val item in values {
        defer { closeItem(item) }
        consume(item)
    }
}

func deferFailureOnContinuePath(values: []int): () {
    for val item in values {
        defer { closeItem(item) }
        if item < 0 { continue }
    }
}

func deferFailureOnBreakPath(values: []int): () {
    for val item in values {
        defer { closeItem(item) }
        break
    }
}

func deferFailureOnReturnPath(values: []int): int {
    for val item in values {
        defer { closeItem(item) }
        return item
    }
    0
}

func deferFailureDuringThrowPreservesPrimary(values: []int): () throws BodyFailure, CleanupFailure {
    for val item in values {
        defer { closeItem(item) }
        throw BodyFailure("primary")
    }
}

func registrationAfterConditionalIsPathSensitive(values: []int, shouldRegister: bool): ()
    throws CleanupFailure {
    for val item in values {
        if shouldRegister { defer { closeItem(item) } }
        consume(item)
    }
}

func skippedBranchDoesNotRegisterCleanup(values: []int): () {
    for val item in values {
        if false { defer { closeItem(item) } }
        consume(item)
    }
}

func registrationReachedAfterContinueDoesNotExist(values: []int): () {
    for val item in values {
        if item < 0 { continue }
        defer { closeItem(item) }
        consume(item)
    }
}

func registrationBeforeContinueRuns(values: []int): () throws CleanupFailure {
    for val item in values {
        defer { closeItem(item) }
        if item < 0 { continue }
    }
}

func twoRegistrationsRunInReverseOrder(values: []int): () throws CleanupFailure {
    for val item in values {
        defer { releaseFirst(item) }
        defer { releaseSecond(item) }
        consume(item)
    }
}

func nestedIterationCleanupsAreInsideOut(rows: [][]int): () throws CleanupFailure {
    defer { cleanupOuter() }
    for val row in rows {
        defer { closeRow(row) }
        for val item in row {
            defer { closeItem(item) }
            consume(item)
        }
    }
}

func innerContinueCleansOnlyInnerIteration(rows: [][]int): () throws CleanupFailure {
    for val row in rows {
        defer { closeRow(row) }
        for val item in row {
            defer { closeItem(item) }
            if item < 0 { continue }
            consume(item)
        }
    }
}

func innerBreakCleansInnerThenContinuesOuter(rows: [][]int): () throws CleanupFailure {
    for val row in rows {
        defer { closeRow(row) }
        for val item in row {
            defer { closeItem(item) }
            if item == 0 { break }
        }
    }
}

func outerBreakCleansActiveNestedScopes(rows: [][]int): () throws CleanupFailure {
    for val row in rows {
        defer { closeRow(row) }
        for val item in row {
            defer { closeItem(item) }
            if item < 0 { break }
            if item == 0 { break }
        }
        if row.length == 0 { break }
    }
}

func returnCleansAllEnteredIterations(rows: [][]int): int throws CleanupFailure {
    defer { cleanupOuter() }
    for val row in rows {
        defer { closeRow(row) }
        for val item in row {
            defer { closeItem(item) }
            return item
        }
    }
    0
}

func unreachableCleanupIsNotRegistered(): () {
    val empty: []int = []
    for val item in empty {
        defer { closeItem(item) }
        consume(item)
    }
}

func emptyArrayDoesNotPropagateCleanupFailure(): () {
    val empty: []int = []
    for val item in empty { defer { closeItem(item) } }
}

func bodyFailureAndCleanupFailureStaySeparate(values: []int): ()
    throws BodyFailure, CleanupFailure {
    for val item in values {
        defer { closeItem(item) }
        if item < 0 { throw BodyFailure("body") }
    }
}

func cleanupWriteAppliesToTheExitingState(values: []int): int {
    var last = 0
    for val item in values {
        defer { last = item }
        if item > 0 { continue }
    }
    last
}

func cleanupWriteDoesNotRepairEarlierExceptionalSnapshot(values: []int): int throws BodyFailure {
    var last: int
    for val item in values {
        defer { last = item }
        if item < 0 { throw BodyFailure("body") }
    }
    0
}

// Defer activation is tied to reaching its declaration in the current body entry. Each iteration
// has its own active cleanup stack. A static empty literal creates no body entry and registers none.
