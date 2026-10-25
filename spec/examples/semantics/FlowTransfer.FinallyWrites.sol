// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative finally-assignment fixture. Cleanup executes before each pending transfer escapes.
module spec.flow.finallywrites

class Failure: RuntimeException {}

func assignBeforeReturn(flag: bool, out value: int): () {
    try {
        if flag {
            return
        }
        return
    } finally {
        value = 1
    }
}

func assignBeforeThrow(flag: bool, out value: int): () throws Failure {
    try {
        if flag {
            throw Failure()
        }
        return
    } finally {
        value = 2
    }
}

func finallyWriteOnNormalExit(): int {
    var value: int
    try {
        value = 1
    } finally {
        value = 2
    }
    value
}

func finallyWriteAfterEarlyReturn(flag: bool): int {
    var value: int
    try {
        if flag {
            return 1
        }
        value = 2
    } finally {
        value = 3
    }
    value
}

func cleanupWriteCanCompleteOut(out value: int): () {
    try {
        throw Failure()
    } catch (failure: Failure) {
        return
    } finally {
        value = 4
    }
}

func finallyReadBeforeWrite(out value: int): () {
    try {
        return
    } finally {
        // Expected: finally cannot read an out parameter until a prior write assigns it.
        val previous = value
        value = previous + 1
    }
}

func throwFromFinallyDoesNotReturn(out value: int): () throws Failure {
    try {
        return
    } finally {
        value = 1
        throw Failure()
    }
}

func finallyWritesDoNotCrossSiblingTry(flag: bool): int {
    var value: int
    try {
        if flag {
            return 1
        }
    } finally {
        value = 2
    }
    // Expected: normal completion passed through finally, so the write is visible here.
    value
}

func cleanupAssignmentsArePathSpecific(flag: bool, out value: int): () {
    try {
        if flag {
            return
        }
        throw Failure()
    } catch (failure: Failure) {
        return
    } finally {
        value = 5
    }
}

// Finally transfer applies to every pending edge. An assignment performed there is visible
// before a normal return is observed, but cannot repair a read that already occurred in finally.
// If finally itself throws, normal completion does not occur and caller-side out assignment
// promises are not inferred from a later unreachable statement.
