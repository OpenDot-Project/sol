// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: cleanup effects apply to every exit and retain primary/suppressed order.
module spec.effects.cleanupcomposition

class BodyFailure: Exception {}
class FirstCleanupFailure: Exception {}
class SecondCleanupFailure: Exception {}

func body(): int throws BodyFailure { throw BodyFailure() }
func firstCleanup(): () throws FirstCleanupFailure { throw FirstCleanupFailure() }
func secondCleanup(): () throws SecondCleanupFailure { throw SecondCleanupFailure() }

func bodyAndFinallyBudgets(): int
    throws BodyFailure, FirstCleanupFailure {
    try {
        body()
    } finally {
        firstCleanup()
    }
}

func finallyBudgetCannotBeOmitted(): int throws BodyFailure {
    try {
        body()
    } finally {
        // Expected: the cleanup effect is reachable even if body() throws.
        firstCleanup()
    }
}

func deferEffectOnNormalExit(): int throws FirstCleanupFailure {
    defer { firstCleanup() }
    1
}

func deferEffectOnReturn(): int throws FirstCleanupFailure {
    defer { firstCleanup() }
    return 2
}

func deferEffectOnThrow(): ! throws BodyFailure, FirstCleanupFailure {
    defer { firstCleanup() }
    throw BodyFailure()
}

func nestedCleanupBudgets(): int
    throws BodyFailure, FirstCleanupFailure, SecondCleanupFailure {
    defer { firstCleanup() }
    try {
        body()
    } finally {
        secondCleanup()
    }
}

func nestedCleanupMissingDeferredBudget(): int throws BodyFailure, SecondCleanupFailure {
    defer { firstCleanup() }
    try {
        body()
    } finally {
        // Expected: FirstCleanupFailure from the deferred action is not covered by this list.
        secondCleanup()
    }
}

func matchingCatchDoesNotCatchCleanup(bodyOnly: bool): int throws FirstCleanupFailure {
    try {
        if bodyOnly {
            body()
        }
    } catch (failure: BodyFailure) {
        return 0
    } finally {
        firstCleanup()
    }
}

func cleanupFailureNotHandledBySiblingCatch(): int throws FirstCleanupFailure {
    try {
        body()
    } catch (failure: BodyFailure) {
        return 0
    } catch (failure: FirstCleanupFailure) {
        // Expected: handler selection occurs before finally runs; this sibling cannot catch it.
        return 1
    } finally {
        firstCleanup()
    }
}

func caughtBodyThenCleanupFailure(): int throws FirstCleanupFailure {
    try {
        body()
    } catch (failure: BodyFailure) {
        0
    } finally {
        firstCleanup()
    }
}

func twoDeferredFailuresAreBothInTheContract(): ()
    throws FirstCleanupFailure, SecondCleanupFailure {
    defer { firstCleanup() }
    defer { secondCleanup() }
}

// All statically reachable cleanup effects contribute regardless of which pending exit reaches
// the cleanup. Runtime primary/suppressed ordering is distinct from the callable's set-like
// checked budget. Implemented today: syntax only; verified cleanup FIR is planned.
