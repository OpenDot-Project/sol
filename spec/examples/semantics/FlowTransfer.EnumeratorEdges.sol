// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative iteration fixture. Resolved cursor calls and cleanup form explicit flow edges.
module spec.flow.enumeratoredges

class IterationFailure: Exception {}
class CloseFailure: Exception {}

interface Cursor<T> {
    func moveNext(): bool throws IterationFailure
    func current(): T throws IterationFailure
    func close(): () throws CloseFailure
}

func sum(cursor: Cursor<int>): int throws IterationFailure, CloseFailure {
    var result = 0
    for val item in cursor {
        result += item
    }
    result
}

func breakClosesCursor(cursor: Cursor<int>): int throws IterationFailure, CloseFailure {
    for val item in cursor {
        if item == 0 {
            break
        }
        return item
    }
    0
}

func continueStillRunsIterationCleanup(cursor: Cursor<int>): int throws IterationFailure, CloseFailure {
    var total = 0
    for val item in cursor {
        if item < 0 {
            continue
        }
        total += item
    }
    total
}

func cursorEffectBudgetMissing(cursor: Cursor<int>): int {
    // Expected: moveNext/current may throw and the cursor cleanup may also throw.
    for val item in cursor {
        return item
    }
    0
}

func iteratorSourceMayThrow(cursor: Cursor<int>): int throws IterationFailure, CloseFailure {
    for val item in cursor {
        if item > 0 {
            return item
        }
    }
    0
}

func cleanupFailureOnIterationExit(cursor: Cursor<int>): int throws IterationFailure, CloseFailure {
    for val item in cursor {
        if item == 1 {
            break
        }
        if item == 2 {
            throw IterationFailure()
        }
    }
    0
}

func nestedCursorCleanup(outer: Cursor<int>, inner: Cursor<int>): int
    throws IterationFailure, CloseFailure {
    var result = 0
    for val first in outer {
        for val second in inner {
            result += first + second
            if result > 10 {
                break
            }
        }
    }
    result
}

func cursorValueIsAssignedOnlyInsideBody(cursor: Cursor<int>): int
    throws IterationFailure, CloseFailure {
    var value: int
    for val item in cursor {
        value = item
    }
    // Expected: empty iteration has no body assignment.
    value
}

func cursorBodyAndCloseHaveDifferentEffects(cursor: Cursor<int>): int
    throws IterationFailure, CloseFailure {
    defer { closeAnother(cursor) }
    for val item in cursor {
        return item
    }
    0
}

func closeAnother(cursor: Cursor<int>): () throws CloseFailure {
    cursor.close()
}

// Collection iteration resolves the cursor protocol before flow transfer. moveNext/current
// effects belong to the iteration edge; close runs on normal completion, break, return, and
// exceptional unwind. A ref-like cursor remains within its permitted scope and is not boxed.
