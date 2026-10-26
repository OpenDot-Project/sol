// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative mixed-transfer fixture. Normal, exceptional, and cleanup edges remain distinct.
module spec.flow.mixedtransfers

class Failure: RuntimeException {}

func choose(flag: bool): int {
    var value: int
    for {
        if flag {
            value = 1
            break
        }
        value = 2
        break
    }
    // Both reachable break states assign value.
    value
}

func oneBreakLeavesValueUnassigned(flag: bool): int {
    var value: int
    for {
        if flag {
            value = 1
            break
        }
        return 0
    }
    value
}

func continueDoesNotBecomeExit(flag: bool): int {
    var value: int
    for {
        if flag {
            continue
        }
        value = 1
        break
    }
    value
}

func throwAndBreakAreDifferent(flag: bool): int {
    for {
        if flag {
            throw Failure()
        }
        break
    }
    0
}

func switchCasesHaveNoFallthrough(flag: bool): int {
    for {
        switch flag {
            case true:
                return 1
            case false:
                return 2
        }
        // Boolean cases are exhaustive; each case returns, so this path is not reachable.
        break
    }
    0
}

func branchThenFinally(flag: bool, out result: int): () {
    try {
        if flag {
            result = 1
            return
        }
        throw Failure()
    } finally {
        result = 2
    }
}

func nestedReturnAndCleanup(flag: bool): int {
    defer { record(1) }
    if flag {
        defer { record(2) }
        return 1
    }
    return 0
}

func loopReturnAndCleanup(flag: bool): int {
    for {
        defer { record(1) }
        if flag {
            return 1
        }
        break
    }
    0
}

func continueRunsIterationCleanup(limit: int): int {
    var total = 0
    for var index: int = 0; index < limit; index += 1 {
        defer { record(index) }
        if index == 1 {
            continue
        }
        total += index
    }
    total
}

func exceptionAfterAssignment(flag: bool): int throws Failure {
    var value: int
    try {
        value = 1
        if flag {
            throw Failure()
        }
        value = 2
    } catch (failure: Failure) {
        // The exceptional edge observes the assignment made before the throw.
        return value
    }
    value
}

func exceptionBeforeAssignment(flag: bool): int throws Failure {
    var value: int
    try {
        if flag {
            throw Failure()
        }
        value = 2
    } catch (failure: Failure) {
        // Negative: on this throw edge, value is still unassigned.
        return value
    }
    value
}

func record(value: int): () {}

// Each transfer carries its own state to the nearest resolved target. Return/throw exits are
// not merged as loop breaks. Finally and defer run before the pending transfer is observed.
