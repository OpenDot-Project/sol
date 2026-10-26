// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative future contract: current-value validity is bounded by successful advance operations.
module spec.arrayiteration.cursorstate

class InvalidCursorState: RuntimeException {}
class AdvanceFailure: Exception {}
class CurrentFailure: Exception {}

interface Cursor<T> {
    func moveNext(): bool throws AdvanceFailure
    func current(): T throws CurrentFailure, InvalidCursorState
}

func currentAfterSuccessfulAdvance(cursor: Cursor<int>): int
    throws AdvanceFailure, CurrentFailure, InvalidCursorState {
    var result = 0
    for val item in cursor { result = item }
    result
}

func currentBeforeFirstAdvanceIsInvalid(cursor: Cursor<int>): int
    throws CurrentFailure, InvalidCursorState {
    cursor.current()
}

func currentAfterFalseAdvanceIsInvalid(cursor: Cursor<int>): int
    throws AdvanceFailure, CurrentFailure, InvalidCursorState {
    if cursor.moveNext() { cursor.current() } else { cursor.current() }
}

func currentAfterEndRemainsInvalid(cursor: Cursor<int>): int
    throws AdvanceFailure, CurrentFailure, InvalidCursorState {
    var result = 0
    for val item in cursor { result = item }
    cursor.current()
}

func repeatedCurrentDoesNotAdvance(cursor: Cursor<int>): int
    throws AdvanceFailure, CurrentFailure, InvalidCursorState {
    if cursor.moveNext() { cursor.current() + cursor.current() } else { 0 }
}

func repeatedMoveNextAdvancesEachCall(cursor: Cursor<int>): bool throws AdvanceFailure {
    cursor.moveNext() && cursor.moveNext()
}

func cursorExhaustionIsStickyByProtocol(cursor: FinishedCursor<int>): bool
    throws AdvanceFailure {
    !cursor.moveNext() && !cursor.moveNext()
}

func collectionMaySpecifyDifferentExhaustionRule(cursor: CustomCursor<int>): bool
    throws AdvanceFailure {
    cursor.moveNext()
}

func loopDoesNotCallCurrentAfterFalse(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, InvalidCursorState {
    for val item in cursor { consume(item) }
}

func continueRequestsNextValueAfterCleanup(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, InvalidCursorState, CleanupFailure {
    for val item in cursor {
        defer { release(item) }
        continue
    }
}

func breakDoesNotRequestNextValue(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, InvalidCursorState {
    for val item in cursor { break }
}

func returnDoesNotRequestNextValue(cursor: Cursor<int>): int
    throws AdvanceFailure, CurrentFailure, InvalidCursorState {
    for val item in cursor { return item }
    0
}

func currentResultIsReadOncePerBodyEntry(cursor: Cursor<int>): int
    throws AdvanceFailure, CurrentFailure, InvalidCursorState {
    var total = 0
    for val item in cursor { total += item }
    total
}

func secondCurrentCallInBodyIsExplicit(cursor: Cursor<int>): int
    throws AdvanceFailure, CurrentFailure, InvalidCursorState {
    var total = 0
    for val item in cursor { total += item + cursor.current() }
    total
}

func currentFailureIsNotTreatedAsEnd(cursor: Cursor<int>): ()
    throws AdvanceFailure, CurrentFailure, InvalidCursorState {
    for val item in cursor { consume(item) }
}

func advanceFailureIsNotTreatedAsFalse(cursor: Cursor<int>): () throws AdvanceFailure {
    for val item in cursor { consume(item) }
}

func boolFalseIsTheOnlyNormalEndSignal(cursor: Cursor<int>): () throws AdvanceFailure {
    for val item in cursor { consume(item) }
}

func nullCurrentIsAValueOnlyForNullableElement(cursor: NullableCursor<String>): ()
    throws AdvanceFailure, CurrentFailure {
    for val item in cursor { consumeNullable(item) }
}

func cursorValueSnapshotDoesNotChangeOnLaterAdvance(cursor: Cursor<String>): int
    throws AdvanceFailure, CurrentFailure, InvalidCursorState {
    var firstLength = 0
    for val item in cursor {
        if firstLength == 0 { firstLength = item.length }
        consume(item)
    }
    firstLength
}

func nestedCursorStateIsIndependent(outer: Cursor<int>, inner: Cursor<int>): int
    throws AdvanceFailure, CurrentFailure, InvalidCursorState {
    var total = 0
    for val first in outer {
        for val second in inner { total += first + second }
    }
    total
}

// The standard Cursor protocol's state validity is explicit. A failed advance is an exception, not
// ordinary exhaustion. `current()` does not advance, and the loop copies its returned value once
// before body entry.
