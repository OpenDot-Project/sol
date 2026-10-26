// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative cleanup fixture. The source contract describes ordering, not runtime execution.
module spec.effects.cleanupordering

class PrimaryFailure: Exception {}
class CleanupFailure: Exception {}

func operation(): int throws PrimaryFailure { throw PrimaryFailure() }
func releaseFirst(): () throws CleanupFailure {}
func releaseSecond(): () throws CleanupFailure {}
func record(value: int): () {}

func primaryFailureSurvives(): int throws PrimaryFailure, CleanupFailure {
    defer { releaseFirst() }
    defer { releaseSecond() }
    operation()
}

func cleanupFailureAloneEscapes(): () throws CleanupFailure {
    defer { releaseFirst() }
    record(1)
}

func cleanupBudgetRequired(): () {
    defer { releaseFirst() }
    record(2)
}

func finallyAndDeferStack(): () throws CleanupFailure {
    defer { releaseFirst() }
    try {
        record(3)
    } finally {
        releaseSecond()
    }
}

func nestedScopeOrder(flag: bool): () {
    defer { record(1) }
    if flag {
        defer { record(2) }
        record(3)
    }
    record(4)
}

func loopIterationOrder(limit: int): () {
    defer { record(1) }
    for var index: int = 0; index < limit; index += 1 {
        defer { record(index) }
        if index == 2 {
            continue
        }
        if index == 3 {
            break
        }
    }
}

func cleanupCannotReturn(): int {
    defer {
        // Expected: a cleanup body cannot replace the pending function result.
        return
    }
    1
}

func cleanupCannotThrowOutward(): () {
    defer {
        // Expected by the initial source contract: cleanup transfer is prohibited.
        throw CleanupFailure()
    }
}

func suppressedFailureIsNotDropped(): int throws PrimaryFailure, CleanupFailure {
    defer { releaseFirst() }
    // If operation fails and releaseFirst also fails, PrimaryFailure remains primary and the
    // CleanupFailure is attached through the bounded suppressed-failure mechanism.
    operation()
}

// Cleanup failure composition is deterministic and does not enumerate hash sets to pick a
// primary. Suppressed recording has a bounded allocation-free reserve path. Runtime exhaustion
// or heap corruption may use a controlled abort policy; this source example is not evidence of
// native unwind integration.
