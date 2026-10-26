// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: finally transfers compose with each pending normal/exception edge.
module spec.flow.finallyexceptionalwrites

class ReadFailure: Exception {}
class CleanupFailure: Exception {}

func read(): int throws ReadFailure { 1 }

func assignInFinallyCompletesReturn(out result: int): () {
    try {
        return
    } finally {
        result = 1
    }
}

func assignmentBeforeThrowSurvivesFinally(out result: int): () throws ReadFailure {
    try {
        result = 2
        read()
    } finally {
        record(1)
    }
}

func finallyAssignmentAppliesToException(out result: int): () throws ReadFailure {
    try {
        read()
    } finally {
        result = 3
    }
}

func conditionalFinallyWriteIsNotDefinite(flag: bool, out result: int): () {
    try {
        return
    } finally {
        if flag {
            result = 4
        }
    }
    // Expected: flag=false leaves a pending normal return without an out assignment.
}

func catchWriteThenFinallyWrite(): int {
    var result: int
    try {
        read()
    } catch (failure: ReadFailure) {
        result = 5
    } finally {
        result += 1
    }
    // Expected: the catch assigns only when read throws; read can also return normally, leaving
    // result unassigned on that normal path despite the finally action.
    result
}

func catchFailureStillRunsFinally(): int throws ReadFailure {
    try {
        read()
    } catch (failure: ReadFailure) {
        throw failure
    } finally {
        record(2)
    }
}

func finallyMayIntroduceNewCheckedEffect(): int throws ReadFailure, CleanupFailure {
    try {
        read()
    } finally {
        cleanup()
    }
}

func finallyEffectCannotBeDropped(): int throws ReadFailure {
    try {
        read()
    } finally {
        // Expected: cleanup's independent checked failure needs coverage.
        cleanup()
    }
}

func cleanup(): () throws CleanupFailure { throw CleanupFailure() }
func record(value: int): () {}

func nestedFinallyAssignments(out result: int): () {
    try {
        try {
            return
        } finally {
            result = 6
        }
    } finally {
        // Expected: outer cleanup observes the inner cleanup's completed assignment.
        result += 1
    }
}

func finallyCannotErasePriorException(): int throws ReadFailure {
    try {
        read()
    } finally {
        // Expected: CleanupFailure is an additional outgoing effect; finally does not erase
        // ReadFailure or make this cleanup effect disappear.
        cleanup()
    }
}

// The transfer graph preserves whether normal completion or an exception is pending and which
// writes precede each cleanup. Implemented today: parser only. Planned: typed edge snapshots and
// cleanup composition must be proven before lowering.
