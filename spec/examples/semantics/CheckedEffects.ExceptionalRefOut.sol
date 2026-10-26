// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: ref/out effects differ on normal and exceptional exits.
module spec.effects.exceptionalrefout

class ReadFailure: Exception {}
class WriteFailure: Exception {}

func assignThenFail(out value: int): () throws WriteFailure {
    value = 1
    throw WriteFailure()
}

func failBeforeAssign(flag: bool, out value: int): () throws ReadFailure {
    if flag {
        throw ReadFailure()
    }
    value = 2
}

func catchAfterKnownOutWrite(): int {
    var value: int
    try {
        assignThenFail(out value)
    } catch (failure: WriteFailure) {
        // Expected: the callee's exceptional edge occurs after its definite assignment.
        return value
    }
}

func catchMayObserveUnassignedOut(flag: bool): int {
    var value: int
    try {
        failBeforeAssign(flag, out value)
    } catch (failure: ReadFailure) {
        // Expected: when flag is true, failure precedes the out write.
        return value
    }
    value
}

func callerWriteBeforeThrowIsVisible(): int {
    var value: int
    try {
        value = 3
        fail()
    } catch (failure: ReadFailure) {
        // Expected: the assignment completed before the throwing call.
        return value
    }
}

func callerWriteAfterThrowDoesNotRun(): int {
    var value: int
    try {
        fail()
        value = 3
    } catch (failure: ReadFailure) {
        // Expected: the assignment after fail is absent from this exceptional snapshot.
        return value
    }
}

func mutateThenThrow(ref value: int): () throws WriteFailure {
    value = 5
    throw WriteFailure()
}

func refMutationSurvivesExceptionalExit(): int {
    var value = 0
    try {
        mutateThenThrow(ref value)
    } catch (failure: WriteFailure) {
        // Expected: a verified callee summary records the completed ref write before throw.
        return value
    }
}

func exceptionalRefStateWithoutSummary(ref value: int): int throws WriteFailure {
    mutateThenThrow(ref value)
    // At an unavailable-body boundary, prior value facts about ref storage are conservatively
    // invalidated on both outcomes unless a checked exceptional summary restores a fact.
    value
}

func fail(): ! throws ReadFailure {
    throw ReadFailure()
}

func catchDoesNotChangeCheckedBudget(): int {
    try {
        assignThenFail(out var value)
    } catch (failure: WriteFailure) {
        return 0
    }
}

func outCallerRequiresNormalReturn(): int {
    var result: int
    try {
        assignThenFail(out result)
    } catch (failure: WriteFailure) {
        // This path's assignment is valid only because the callee wrote before its throw.
        return result
    }
    result
}

// The callee must assign out on every normal exit, not on every throwing exit. Exceptional
// definite-assignment facts require an ordered body summary. Implemented today: syntax trees;
// planned: verified exceptional summaries and caller flow consume these edge-specific writes.
