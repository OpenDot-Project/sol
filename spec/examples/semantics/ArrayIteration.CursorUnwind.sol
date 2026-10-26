// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative future contract: cursor cleanup failures preserve exit and exception identity.
module spec.arrayiteration.cursorunwind

class FactoryFailure: Exception {}
class AdvanceFailure: Exception {}
class CurrentFailure: Exception {}
class BodyFailure: Exception {}
class CloseFailure: Exception {}
class OuterCleanupFailure: Exception {}

interface Cursor<T> {
    func moveNext(): bool throws AdvanceFailure
    func current(): T throws CurrentFailure
    func close(): () throws CloseFailure
}

func normalCompletionCanBecomeCloseFailure(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val item in cursor { consume(item) }
    markCompleted()
}

func breakCanBecomeCloseFailure(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val item in cursor { break }
    markCompleted()
}

func returnCanBecomeCloseFailure(cursor: Cursor<int>): int
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val item in cursor { return item }
    0
}

func advanceFailureIsPrimaryWhenCloseFails(cursor: Cursor<int>): ()
    throws AdvanceFailure, CloseFailure {
    for val item in cursor { consume(item) }
}

func currentFailureIsPrimaryWhenCloseFails(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val item in cursor { consume(item) }
}

func bodyFailureIsPrimaryWhenCloseFails(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, BodyFailure, CloseFailure {
    for val item in cursor { throw BodyFailure("body") }
}

func acquisitionFailureDoesNotHaveCloseFailure(cursor: Cursor<int>): ()
    throws FactoryFailure {
    for val item in sourceWhoseFactoryFails(cursor) { consume(item) }
}

func failureBeforeAcquisitionHasNoCursorCleanup(source: CursorSource<int>): ()
    throws FactoryFailure {
    for val item in source { consume(item) }
}

func itemReadFailureHasCursorCleanup(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val item in cursor { consume(item) }
}

func bodyCheckedFailureRunsClose(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, BodyFailure, CloseFailure {
    for val item in cursor { mayFail(item) }
}

func closeFailureIsNotDiscardedByCatchOfBodyType(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    try {
        for val item in cursor { throw BodyFailure("body") }
    } catch (error: BodyFailure) {
        recordBodyFailure(error)
    }
}

func closeFailureMayBeCaughtSeparately(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, BodyFailure {
    try {
        for val item in cursor { throw BodyFailure("body") }
    } catch (error: CloseFailure) {
        recordCloseFailure(error)
    } catch (error: BodyFailure) {
        recordBodyFailure(error)
    }
}

func originalFailureRemainsPrimary(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, BodyFailure, CloseFailure {
    try {
        for val item in cursor { throw BodyFailure("primary") }
    } catch (error: BodyFailure) {
        // The body failure is the primary exception; cleanup failure is available through its
        // suppressed-failure collection and cannot replace this static identity.
        recordSuppressed(error)
    }
}

func twoCleanupFailuresPreserveLifoOrder(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, BodyFailure, CloseFailure, OuterCleanupFailure {
    defer { failOuterCleanup() }
    for val item in cursor {
        defer { failIterationCleanup() }
        throw BodyFailure("primary")
    }
}

func nestedCursorFailuresPreserveInnerPrimary(outer: Cursor<int>, inner: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, BodyFailure, CloseFailure {
    for val first in outer {
        for val second in inner {
            if first == second { throw BodyFailure("inner") }
        }
    }
}

func innerCloseFailureDoesNotSkipOuterCleanup(outer: Cursor<int>, inner: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val first in outer {
        for val second in inner { consume(first + second) }
    }
}

func closeFailureDuringNormalExitHasNoSuppressedPrimary(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val item in cursor { consume(item) }
}

func closeFailureDuringBreakHasNoBodyException(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val item in cursor { break }
}

func closeFailureDuringReturnHasPendingReturnValue(cursor: Cursor<int>): int
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val item in cursor { return item }
    0
}

func closeFailureDuringContinueDoesNotClose(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val item in cursor { continue }
}

func continueBodyCleanupFailureIsNotCursorClose(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure, OuterCleanupFailure {
    for val item in cursor {
        defer { failOuterCleanup() }
        continue
    }
}

func finallyRunsAfterCursorCloseFailure(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure, OuterCleanupFailure {
    try {
        for val item in cursor { consume(item) }
    } finally {
        failOuterCleanup()
    }
}

func closeFailureParticipatesInOuterCatch(cursor: Cursor<int>): () {
    try {
        for val item in cursor { consume(item) }
    } catch (error: CloseFailure) {
        handleClose(error)
    }
}

func outerCatchDoesNotReceivePreCloseState(cursor: Cursor<int>): int {
    var completed = false
    try {
        for val item in cursor { completed = true }
    } catch (error: CloseFailure) {
        // The handler receives the state at the close edge, including completed when traversal ran.
        return if completed { 1 } else { 0 }
    }
    2
}

func failedCloseIsNotRetriedImplicitly(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    for val item in cursor { consume(item) }
}

func explicitSecondCloseIsASeparateCall(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, CloseFailure {
    val saved = cursor
    for val item in cursor { consume(item) }
    saved.close()
}

// The semantic contract distinguishes normal completion, pending return, break, and active
// exceptions. Cleanup failures have deterministic ordering and cannot be erased by a broad
// catch-path merge that forgets the edge on which they occurred.
