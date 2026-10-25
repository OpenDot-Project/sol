// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: loop transfer analysis includes zero, backedge, and exit paths.
module spec.flow.loopbackedgewrites

func assignmentMayBeSkipped(limit: int): int {
    var value: int
    for var index: int = 0; index < limit; index += 1 {
        value = index
    }
    // Expected: the initial condition may be false, so value may remain unassigned.
    value
}

func initializationBeforeLoopIsDefinite(limit: int): int {
    var value = 0
    for var index: int = 0; index < limit; index += 1 {
        value = index
    }
    // Expected: assignment in the loop is optional but the entry value is already assigned.
    value
}

func everyNormalBreakAssigns(flag: bool): int {
    var result: int
    for {
        if flag {
            result = 1
            break
        }
        result = 2
        break
    }
    // Expected: both reachable break edges assign result.
    result
}

func breakAndConditionExitJoin(limit: int): int {
    var result: int
    for var index: int = 0; index < limit; index += 1 {
        if index == 3 {
            result = index
            break
        }
    }
    // Expected: condition-false exit can occur before the assignment.
    result
}

func continueFlowsThroughUpdate(limit: int): int {
    var updates = 0
    for var index: int = 0; index < limit; updates += 1 {
        if index < 0 {
            continue
        }
        index += 1
    }
    // Expected: a continue edge evaluates the update expression before the next condition.
    updates
}

func continueDoesNotSkipCleanup(limit: int): int {
    var visits = 0
    for var index: int = 0; index < limit; index += 1 {
        defer { visits += 1 }
        if index == 0 {
            continue
        }
        visits += 10
    }
    // Expected: each continue unwinds body cleanup before the loop update.
    visits
}

func nestedBreakTargetsInnermost(flag: bool): int {
    var outer = 0
    for {
        for {
            if flag {
                break
            }
            break
        }
        outer += 1
        if flag {
            break
        }
    }
    outer
}

func backedgeCanInvalidateNullableFact(flag: bool, entry: Entry?): int {
    var current = entry
    if current == null {
        return 0
    }
    for flag {
        current = null
        continue
    }
    // Expected: a path through the loop backedge writes null before the exit.
    current.code
}

func stableValueAcrossEmptyLoop(flag: bool, entry: Entry?): int {
    val snapshot = entry
    if snapshot == null {
        return 0
    }
    for flag {
        record(snapshot.code)
    }
    // Expected: loop body reads do not mutate the immutable snapshot local.
    snapshot.code
}

class Entry(val code: int) {}

func record(value: int): () {}

// Fixed-point iteration joins entry with backedges and keeps abrupt exits separate. Resource
// exhaustion or an incomplete transfer is deferred/reported; it cannot be treated as a proof
// that the loop body ran at least once. Implemented today: syntax only.
