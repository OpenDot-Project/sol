// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative exceptional-edge fixture. Each catch reads the state at its throwing operation.
module spec.flow.exceptionalsnapshots

class ReadFailure: Exception {}
class WriteFailure: Exception {}

func read(): int throws ReadFailure { 1 }
func write(): () throws WriteFailure {}

func firstCallCanThrowBeforeAssignment(): int throws ReadFailure {
    var result: int
    try {
        val value = read()
        result = value
    } catch (failure: ReadFailure) {
        // Expected: result was not assigned before read threw.
        return result
    }
    result
}

func earlierAssignmentReachesLaterCallEdge(): int throws ReadFailure {
    var result: int
    result = 1
    try {
        read()
        result = 2
    } catch (failure: ReadFailure) {
        // Expected: result is assigned to 1 on the call's exceptional edge.
        return result
    }
    result
}

func assignmentsAfterEachEffectEdge(): int throws ReadFailure, WriteFailure {
    var result: int
    try {
        read()
        result = 1
        write()
        result = 2
    } catch (readFailure: ReadFailure) {
        // The read edge occurs before either assignment.
        return 0
    } catch (writeFailure: WriteFailure) {
        // The write edge occurs after result = 1 and before result = 2.
        return result
    }
    result
}

func nestedTryPreservesEarlierState(): int throws ReadFailure {
    var result: int
    result = 0
    try {
        try {
            read()
        } catch (failure: ReadFailure) {
            result = 1
        }
        read()
    } catch (failure: ReadFailure) {
        // Expected: both the inner handled edge and the second call are represented. The
        // second call can throw after the first handler assigned result = 1.
        return result
    }
    result
}

func handlerWriteDoesNotReachSiblingHandler(): int throws ReadFailure, WriteFailure {
    var result: int
    try {
        read()
    } catch (failure: ReadFailure) {
        result = 1
        write()
    } catch (failure: WriteFailure) {
        // Expected: this sibling handler catches the protected try body, not a throw from
        // the first handler. The first handler's WriteFailure escapes to an outer scope.
        return result
    }
    result
}

func handlerThrowReachesOuterCatch(): int {
    try {
        try {
            read()
        } catch (failure: ReadFailure) {
            write()
        }
    } catch (failure: WriteFailure) {
        return 1
    }
    0
}

func finallyRunsAfterEachPendingReturn(flag: bool, out value: int): () {
    try {
        if flag {
            return
        }
        return
    } finally {
        value = 1
    }
}

func finallyMutatesThrowingPath(flag: bool, out value: int): () throws ReadFailure {
    try {
        if flag {
            throw ReadFailure()
        }
        return
    } finally {
        value = 1
    }
}

func finallyEffectAfterPrimaryThrow(): ! throws ReadFailure, WriteFailure {
    try {
        throw ReadFailure()
    } finally {
        write()
    }
}

func finallyEffectWithoutPrimary(): () throws WriteFailure {
    try {
        val value = 1
    } finally {
        write()
    }
}

func nestedFinallyOrder(): () throws ReadFailure, WriteFailure {
    try {
        try {
            read()
        } finally {
            write()
        }
    } finally {
        write()
    }
}

// A handler catches exceptions from the protected body, not its sibling handlers. Exceptions
// from a handler propagate outward. Finally is attached to every exit from its try, including
// exits that have already passed through a catch. A compiler must preserve a separate state for
// each exceptional edge until cleanup has run.
