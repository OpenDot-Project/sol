// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative future cursor contract: close is registered only after successful acquisition.
module spec.arrayiteration.cursorcleanup

class FactoryFailure: Exception {}
class AdvanceFailure: Exception {}
class ReadFailure: Exception {}
class CloseFailure: Exception {}

interface ResourceCursor<T> {
    func moveNext(): bool throws AdvanceFailure
    func current(): T throws ReadFailure
    func close(): () throws CloseFailure
}

interface ResourceSource<T> {
    func iterator(): ResourceCursor<T> throws FactoryFailure
}

func closeAfterNormalExhaustion(source: ResourceSource<int>): ()
    throws FactoryFailure, AdvanceFailure, ReadFailure, CloseFailure {
    for val item in source { consume(item) }
}

func closeAfterBreak(cursor: ResourceCursor<int>): ()
    throws AdvanceFailure, ReadFailure, CloseFailure {
    for val item in cursor {
        if item == 0 { break }
    }
}

func closeAfterReturn(cursor: ResourceCursor<int>): int
    throws AdvanceFailure, ReadFailure, CloseFailure {
    for val item in cursor { return item }
    0
}

func closeAfterBodyThrow(cursor: ResourceCursor<int>): ()
    throws AdvanceFailure, ReadFailure, CloseFailure, Failure {
    for val item in cursor { throw Failure("body") }
}

func closeAfterAdvanceThrow(cursor: ResourceCursor<int>): ()
    throws AdvanceFailure, CloseFailure {
    for val item in cursor { consume(item) }
}

func closeAfterCurrentThrow(cursor: ResourceCursor<int>): ()
    throws AdvanceFailure, ReadFailure, CloseFailure {
    for val item in cursor { consume(item) }
}

func closeIsNotCalledWhenFactoryFails(source: ResourceSource<int>): () throws FactoryFailure {
    for val item in source { consume(item) }
}

func closeEffectIsRequired(cursor: ResourceCursor<int>): ()
    throws AdvanceFailure, ReadFailure {
    // Expected checked-effect error: closing may fail on loop exhaustion or another exit.
    for val item in cursor { consume(item) }
}

func closeRunsOnceAfterManyItems(cursor: ResourceCursor<int>): int
    throws AdvanceFailure, ReadFailure, CloseFailure {
    var count = 0
    for val item in cursor { count += 1 }
    count
}

func closeRunsOnceAfterImmediateFalse(cursor: ResourceCursor<int>): int
    throws AdvanceFailure, CloseFailure {
    var count = 0
    for val item in cursor { count += 1 }
    count
}

func breakClosesWithoutExtraAdvance(cursor: ResourceCursor<int>): int
    throws AdvanceFailure, ReadFailure, CloseFailure {
    for val item in cursor {
        if item == 0 { break }
        consume(item)
    }
    0
}

func continueDoesNotCloseCursor(cursor: ResourceCursor<int>): int
    throws AdvanceFailure, ReadFailure, CloseFailure {
    var total = 0
    for val item in cursor {
        if item < 0 { continue }
        total += item
    }
    total
}

func continueRunsBodyCleanupThenAdvances(cursor: ResourceCursor<int>): ()
    throws AdvanceFailure, ReadFailure, CloseFailure, CleanupFailure {
    for val item in cursor {
        defer { closeIteration(item) }
        if item < 0 { continue }
        consume(item)
    }
}

func closeAfterBreakAndBodyCleanup(cursor: ResourceCursor<int>): ()
    throws AdvanceFailure, ReadFailure, CloseFailure, CleanupFailure {
    for val item in cursor {
        defer { closeIteration(item) }
        if item == 0 { break }
    }
}

func closeAfterReturnAndBodyCleanup(cursor: ResourceCursor<int>): int
    throws AdvanceFailure, ReadFailure, CloseFailure, CleanupFailure {
    for val item in cursor {
        defer { closeIteration(item) }
        return item
    }
    0
}

func outerCleanupRunsAfterInnerCursorClose(source: ResourceSource<int>): ()
    throws FactoryFailure, AdvanceFailure, ReadFailure, CloseFailure, CleanupFailure {
    defer { cleanupOuter() }
    for val item in source {
        if item > 0 { return }
    }
}

func nestedCursorClosesInsideOut(outer: ResourceCursor<int>, inner: ResourceCursor<int>): ()
    throws AdvanceFailure, ReadFailure, CloseFailure {
    for val first in outer {
        for val second in inner {
            if first == second { break }
        }
    }
}

func innerCloseFailureStillClosesOuter(
    outer: ResourceCursor<int>,
    inner: ResourceCursor<int>,
): () throws AdvanceFailure, ReadFailure, CloseFailure {
    for val first in outer {
        for val second in inner { consume(first + second) }
    }
}

func closeFailureOnNormalExhaustionChangesNormalExit(cursor: ResourceCursor<int>): ()
    throws AdvanceFailure, ReadFailure, CloseFailure {
    for val item in cursor { consume(item) }
    recordNormalCompletion()
}

func closeFailureOnBreakChangesBreakExit(cursor: ResourceCursor<int>): ()
    throws AdvanceFailure, ReadFailure, CloseFailure {
    for val item in cursor { break }
    recordAfterLoop()
}

func closeFailureOnReturnReplacesReturnWithExceptionalExit(cursor: ResourceCursor<int>): int
    throws AdvanceFailure, ReadFailure, CloseFailure {
    for val item in cursor { return item }
    0
}

func bodyAndCloseFailuresKeepPrimaryIdentity(cursor: ResourceCursor<int>): ()
    throws AdvanceFailure, ReadFailure, CloseFailure, Failure {
    for val item in cursor { throw Failure("primary") }
}

func closeDoesNotRunBeforeFirstAcquisition(cursor: ResourceCursor<int>): ()
    throws AdvanceFailure, ReadFailure, CloseFailure {
    for val item in maybeAcquire(cursor) { consume(item) }
}

func noCloseMethodMeansNoCursorCloseEdge(cursor: OrdinaryCursor<int>): ()
    throws AdvanceFailure, ReadFailure {
    for val item in cursor { consume(item) }
}

func closeMethodMustBeAccessible(cursor: HiddenCloseCursor<int>): () {
    // Expected protocol-resolution error if the resolved cleanup is inaccessible.
    for val item in cursor { consume(item) }
}

class Failure: Exception {}
class CleanupFailure: Exception {}

// Cursor cleanup and body-scope cleanup are different resources. Continue closes the iteration's
// lexical body scope, then requests another item; it does not close the cursor. Cursor close occurs
// once when traversal as a whole leaves through exhaustion, break, return, or exceptional unwind.
