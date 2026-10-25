// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: defer actions attach to lexical scope exits.
module spec.flow.deferregistration

func record(value: int): () {}

func registeredConditionally(flag: bool): () {
    if flag {
        defer { record(1) }
        record(2)
    }
    // Expected: the defer registered in the arm runs when that arm's scope exits.
}

func noRegistrationOnUnenteredArm(flag: bool): () {
    if flag {
        defer { record(1) }
    } else {
        record(2)
    }
    // Expected: the false path has no pending action from the true arm.
}

func deferOrderWithinScope(): () {
    defer { record(1) }
    defer { record(2) }
    record(3)
    // Expected execution order: body, record(2), then record(1).
}

func nestedScopesHaveIndependentStacks(flag: bool): () {
    defer { record(1) }
    if flag {
        defer { record(2) }
        record(3)
    }
    record(4)
    // Expected: inner cleanup runs before record(4); outer cleanup runs at function exit.
}

func loopBodyRegistersPerIteration(limit: int): () {
    for var index: int = 0; index < limit; index += 1 {
        defer { record(index) }
        record(index + 100)
    }
    // Expected: each body-scope action runs when that iteration exits, including continue.
}

func continueRunsIterationCleanup(limit: int): int {
    var total = 0
    for var index: int = 0; index < limit; index += 1 {
        defer { record(index) }
        if index == 1 {
            continue
        }
        total += index
    }
    // Expected: continue exits the body scope and runs its registered cleanup first.
    total
}

func breakRunsIterationCleanup(limit: int): int {
    var total = 0
    for var index: int = 0; index < limit; index += 1 {
        defer { record(index) }
        if index == 2 {
            break
        }
        total += index
    }
    // Expected: break runs the active iteration cleanup and skips the loop update expression.
    total
}

func returnRunsCleanup(): int {
    defer { record(1) }
    return 2
}

func cleanupAssignmentCompletesOut(out result: int): () {
    defer { result = 7 }
    return
}

func conditionalCleanupDoesNotAssignOnAllPaths(flag: bool, out result: int): () {
    if flag {
        defer { result = 7 }
        return
    }
    // Expected: the normal false path has no registered assignment to result.
}

func assignmentBeforeReturnSurvivesCleanup(out result: int): () {
    defer { record(1) }
    result = 4
    return
}

func innerReturnUnwindsBothScopes(flag: bool): () {
    defer { record(1) }
    if flag {
        defer { record(2) }
        return
    }
    record(3)
}

func deferCannotReturn(flag: bool): () {
    defer {
        if flag {
            // Expected: outward return from a cleanup action is rejected.
            return
        }
    }
}

// A defer becomes active only when execution reaches its declaration. Branch joins therefore
// retain conditional registration state. Each cleanup runs at its lexical scope exit in reverse
// order. Implemented today: syntax trees only. Planned: verified flow and FIR retain path-specific
// registrations, pending transfers, and cleanup order; the comments above are not test results.
