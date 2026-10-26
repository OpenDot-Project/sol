// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative future contract: state snapshots attach to acquisition, advance, current, body, and close.
module spec.arrayiteration.cursortransferstate

class Failure: Exception {}
class CloseFailure: Exception {}

interface Cursor<T> {
    func moveNext(): bool throws Failure
    func current(): T throws Failure
    func close(): () throws CloseFailure
}

func sourceMutationBeforeAcquisition(values: []int): int throws Failure, CloseFailure {
    var state = 0
    for val item in acquireAfterWrite(out state, values) { consume(item) }
    state
}

func acquisitionExceptionalSnapshot(values: []int): int throws Failure {
    var state = 0
    try {
        for val item in acquireAfterWriteThenFail(out state, values) { consume(item) }
    } catch (error: Failure) { return state }
    state
}

func advanceExceptionalSnapshot(cursor: Cursor<int>): int throws Failure, CloseFailure {
    var state = 0
    try {
        for val item in cursor { state = item }
    } catch (error: Failure) { return state }
    state
}

func currentExceptionalSnapshot(cursor: Cursor<int>): int throws Failure, CloseFailure {
    var state = 0
    try {
        for val item in cursor { state = item }
    } catch (error: Failure) { return state }
    state
}

func bodyExceptionalSnapshot(cursor: Cursor<int>): int throws Failure, CloseFailure {
    var state = 0
    try {
        for val item in cursor { state = item; fail(item) }
    } catch (error: Failure) { return state }
    state
}

func cleanupExceptionalSnapshot(cursor: Cursor<int>): int throws Failure, CloseFailure {
    var state = 0
    try {
        for val item in cursor { state = item }
    } catch (error: CloseFailure) { return state }
    state
}

func normalExhaustionState(cursor: Cursor<int>): int throws Failure, CloseFailure {
    var state = 0
    for val item in cursor { state = item }
    state
}

func breakState(cursor: Cursor<int>): int throws Failure, CloseFailure {
    var state = 0
    for val item in cursor { state = item; break }
    state
}

func returnState(cursor: Cursor<int>): int throws Failure, CloseFailure {
    var state = 0
    for val item in cursor { state = item; return state }
    state
}

func continueState(cursor: Cursor<int>): int throws Failure, CloseFailure {
    var state = 0
    for val item in cursor {
        state += 1
        if item < 0 { continue }
        state += item
    }
    state
}

func cursorCloseSeesFinalBodyState(cursor: Cursor<int>): int throws Failure, CloseFailure {
    var state = 0
    for val item in cursor { state = item }
    readClosedState()
}

func breakCleanupSeesCurrentItem(cursor: Cursor<int>): int throws Failure, CloseFailure {
    var state = 0
    for val item in cursor {
        defer { observe(state, item) }
        state = item
        break
    }
    state
}

func continueCleanupSeesCurrentItem(cursor: Cursor<int>): int throws Failure, CloseFailure {
    var state = 0
    for val item in cursor {
        defer { observe(state, item) }
        state = item
        continue
    }
    state
}

func currentSlotDoesNotLeakAfterLoop(cursor: Cursor<int>): int throws Failure, CloseFailure {
    for val item in cursor { consume(item) }
    // Expected name-resolution error: item is loop-scoped.
    item
}

func cursorCreationStateIsNotBodyState(cursor: Cursor<int>): int throws Failure, CloseFailure {
    var state = 1
    val source = recordStateAndReturnCursor(out state, cursor)
    for val item in source { state += item }
    state
}

func nestedCursorStateHasIndependentSnapshots(outer: Cursor<int>, inner: Cursor<int>): int
    throws Failure, CloseFailure {
    var state = 0
    for val first in outer {
        state += first
        for val second in inner { state += second }
    }
    state
}

func innerCloseFailureStateFlowsToOuterCatch(outer: Cursor<int>, inner: Cursor<int>): int
    throws Failure, CloseFailure {
    var state = 0
    try {
        for val first in outer {
            for val second in inner { state = first + second }
        }
    } catch (error: CloseFailure) { return state }
    state
}

func cleanupWriteDoesNotAlterSavedPrimarySnapshot(cursor: Cursor<int>): int
    throws Failure, CloseFailure {
    var state = 0
    try {
        for val item in cursor {
            defer { state = 99 }
            fail(item)
        }
    } catch (error: Failure) { return state }
    state
}

func closeWriteOccursOnlyAfterLoopTransfer(cursor: Cursor<int>): int throws Failure, CloseFailure {
    var state = 0
    for val item in cursor { state = item }
    state
}

func zeroTripHasNoCurrentWrite(cursor: Cursor<int>): int throws Failure, CloseFailure {
    var state = 0
    for val item in cursor { state = item }
    state
}

// Each operation has a source origin and input-state snapshot. Exceptional control-flow must not
// collapse these stages into one synthetic call with a single post-call state.
