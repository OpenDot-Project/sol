// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: the zero-trip edge participates in post-loop definite assignment.
module spec.arrayiteration.definiteassignment

func itemIsAssignedOnBodyEntry(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func bodyWriteDoesNotAssignAfterLoop(values: []int): int {
    var result: int
    for val item in values { result = item }
    // Expected definite-assignment error: the array may be empty.
    result
}

func bodyWriteAndBreakDoesNotAssignAfterLoop(values: []int): int {
    var result: int
    for val item in values {
        result = item
        break
    }
    // Expected error: no item was written on the zero-trip edge.
    result
}

func continueBeforeAssignmentDoesNotAssign(values: []int): int {
    var result: int
    for val item in values {
        if item < 0 { continue }
        result = item
    }
    // Expected error: empty input and negative-only input do not assign result.
    result
}

func assignmentOnAllBodyPathsStillDoesNotAssignAfterLoop(values: []int): int {
    var result: int
    for val item in values {
        if item < 0 { result = -item } else { result = item }
    }
    // Expected error: body exhaustiveness does not remove the zero-trip predecessor.
    result
}

func priorAssignmentSurvivesEmptyInput(values: []int): int {
    var result = 11
    for val item in values { result = item }
    result
}

func assignmentBeforeBreakSurvivesTheBreakPath(values: []int): int {
    var result = 11
    for val item in values {
        result = item
        break
    }
    result
}

func returnPathDoesNotJoinNormalExit(values: []int): int {
    var result: int
    for val item in values {
        result = item
        return result
    }
    // Expected error: the empty path falls through with result unassigned.
    result
}

func allBodyPathsReturnButEmptyStillFallsThrough(values: []int): int {
    var result: int
    for val item in values {
        if item < 0 { return item }
        result = item
        return result
    }
    // Expected error: the loop may have zero entries.
    result
}

func bottomBodyPathDoesNotAssignNormalExit(values: []int): int {
    var result: int
    for val item in values {
        result = item
        throw Failure("stop")
    }
    // Expected error: empty array bypasses body and throw.
    result
}

func bodyLocalStartsUnassignedEachEntry(values: []int): int {
    var total = 0
    for val item in values {
        var local: int
        if item > 0 { local = item }
        // Expected error on the false branch; previous iterations do not initialize this local.
        total += local
    }
    total
}

func bodyLocalAssignmentDoesNotFlowAcrossIterations(values: []int): int {
    var total = 0
    for val item in values {
        var local: int
        if item == 0 { local = 1 }
        // Expected error: a previous iteration's local storage is not this iteration's proof.
        total += local
    }
    total
}

func outCallAssignsOnNormalBodyPath(values: []int): int {
    var total = 0
    for val item in values {
        var local: int
        fill(out local, item)
        total += local
    }
    total
}

func outCallDoesNotAssignPastZeroTrip(values: []int): int {
    var local: int
    for val item in values { fill(out local, item) }
    // Expected error: out assignment happens only if an iteration occurs.
    local
}

func outCallThrowUsesPreCallAssignment(values: []int): int throws Failure {
    var local: int
    for val item in values {
        fillThenThrow(out local, item)
    }
    // Expected error: only normal out completion establishes assignment.
    local
}

func breakStateJoinsZeroTripAndBody(values: []int): int {
    var local = 2
    for val item in values {
        if item > 0 { local = item; break }
        local += 1
    }
    local
}

func continueStateDoesNotJoinAsACompletedIteration(values: []int): int {
    var local = 0
    for val item in values {
        if item < 0 { continue }
        local = item
    }
    local
}

func nestedInnerAssignmentDoesNotProveOuterEntry(outer: [][]int): int {
    var result: int
    for val row in outer {
        for val item in row { result = item }
    }
    // Expected error: either loop may be empty.
    result
}

func innerBreakDoesNotBreakOuterAssignmentProof(outer: [][]int): int {
    var result = 0
    for val row in outer {
        for val item in row {
            result = item
            break
        }
    }
    result
}

func outerContinueSkipsLaterAssignment(outer: [][]int): int {
    var result: int
    for val row in outer {
        if row.length == 0 { continue }
        result = row[0]
    }
    // Expected error: empty outer input skips all assignments.
    result
}

func constantEmptyArrayHasNoBodyAssignment(): int {
    var result: int
    val empty: []int = []
    for val item in empty { result = item }
    // Expected error: the body cannot execute.
    result
}

func constantEmptyArrayAllowsAfterLoopReadOnlyIfPreviouslyAssigned(): int {
    var result = 7
    val empty: []int = []
    for val item in empty { result = item }
    result
}

func sourceCallMayReturnEmpty(values: []int): int {
    var result: int
    for val item in maybeEmpty(values) { result = item }
    // Expected error: source call result may have length zero.
    result
}

func knownLiteralHasBodyAssignmentWhenNonEmpty(): int {
    var result: int
    for val item in [42] { result = item }
    result
}

func nonEmptyLiteralBreaksOnFirstEntry(): int {
    var result: int
    for val item in [42] {
        result = item
        break
    }
    result
}

func oneElementLiteralCanReturnEarly(): int {
    for val item in [42] { return item }
    0
}

func oneElementLiteralCanContinueToExhaustion(): int {
    var count = 0
    for val item in [42] { count += 1; continue }
    count
}

func checkedFailureDoesNotAssignAfterLoop(values: []int): int throws Failure {
    var result: int
    for val item in values {
        if item == 0 { throw Failure("stop") }
        result = item
    }
    // Expected error on normal zero-trip/exhaustion paths without a write.
    result
}

class Failure: Exception {}

// The join after a possibly-empty iteration intersects facts from all reachable normal predecessors.
// Body effects, returns, throws, and cleanup exits remain attached to their specific edges.
