// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: joins include zero-trip, exhaustion, break, and exceptional continuations.
module spec.arrayiteration.assignmentjoins

func initializedBeforeLoop(values: []int): int {
    var value = 0
    for val item in values { value = item }
    value
}

func bodyOnlyWriteIsInsufficient(values: []int): int {
    var value: int
    for val item in values { value = item }
    value
}

func oneArmWriteIsInsufficient(values: []int): int {
    var value: int
    for val item in values {
        if item > 0 { value = item }
    }
    value
}

func bothBodyArmsWriteButZeroTripRemains(values: []int): int {
    var value: int
    for val item in values {
        if item > 0 { value = item } else { value = -item }
    }
    value
}

func breakArmWriteJoinsWithExhaustion(values: []int): int {
    var value = -1
    for val item in values {
        if item == 0 { value = 9; break }
        value = item
    }
    value
}

func continueArmSkipsWrite(values: []int): int {
    var value = -1
    for val item in values {
        if item < 0 { continue }
        value = item
    }
    value
}

func writeBeforeContinueIsVisibleOnThatExit(values: []int): int {
    var value = -1
    for val item in values {
        value = item
        if item < 0 { continue }
    }
    value
}

func innerLoopWriteDoesNotProveOuterBodyRan(rows: [][]int): int {
    var value: int
    for val row in rows {
        for val item in row { value = item }
    }
    value
}

func innerZeroTripLeavesOuterAssignmentMissing(rows: [2][]int): int {
    var value: int
    for val row in rows {
        for val item in row { value = item }
    }
    value
}

func innerBreakWriteSurvivesOuterNormalPath(rows: [2][]int): int {
    var value = -1
    for val row in rows {
        for val item in row { value = item; break }
    }
    value
}

func returnEdgeDoesNotJoinAfterLoop(values: []int): int {
    var value: int
    for val item in values {
        value = item
        return value
    }
    value
}

func throwEdgeCarriesItsOwnSnapshot(values: []int): int throws Failure {
    var value: int
    for val item in values {
        if item < 0 { throw Failure("negative") }
        value = item
    }
    value
}

func catchReceivesStateAtBodyFailure(values: []int): int {
    var value = 0
    try {
        for val item in values {
            value = item
            mayFail(item)
        }
    } catch (error: Failure) {
        return value
    }
    value
}

func sourceFailurePrecedesBodyAssignment(values: []int): int throws Failure {
    var value: int
    for val item in makeArrayOrFail(values) { value = item }
    value
}

func exceptionalOutCallDoesNotAssign(values: []int): int throws Failure {
    var value: int
    for val item in values { writeThenFail(out value, item) }
    value
}

func normalOutCallAssignsOnlyEnteredIteration(values: []int): int {
    var value: int
    for val item in values { write(out value, item) }
    value
}

func staticEmptyBodyWriteIsUnreachable(): int {
    var value: int
    val empty: []int = []
    for val item in empty { value = item }
    value
}

func staticNonEmptyLiteralHasBodyEntry(): int {
    var value: int
    for val item in [4] { value = item }
    value
}

func sourceCallMayStillBeEmpty(values: []int): int {
    var value: int
    for val item in identityArray(values) { value = item }
    value
}

func unreachableAfterAllNonEmptyLiteralPaths(): int {
    for val item in [4] { return item }
    0
}

func unreachableAfterBreakFromNonEmptyLiteral(): int {
    for val item in [4] { break }
    0
}

func assignmentAfterAlwaysEmptyLoopRemainsUnassigned(): int {
    var value: int
    val empty: []int = []
    for val item in empty { value = item }
    value
}

func mutableFactDoesNotSurviveUnknownBodyWrite(values: []int, maybe: bool): int {
    var result = 0
    for val item in values {
        if maybe { mutateCapturedState() }
        result += item
    }
    result
}

class Failure: Exception {}

// The normal post-loop state joins the zero-trip path, exhaustion path, and every reachable break.
// Returns and throws are modeled separately; an exceptional snapshot cannot borrow a later write.
