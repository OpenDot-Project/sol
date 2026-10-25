// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative generic-effect fixture. The checked-effect variable is constrained and substituted.
module spec.effects.genericpatterns

class Failure: Exception {}
class ReadFailure: Exception {}
class WriteFailure: Exception {}

func <E: Throwable> raise(error: E): ! throws E {
    throw error
}

func <E: Throwable> pass(value: E): E throws E {
    raise(value)
}

func concreteFailure(): ! throws Failure {
    raise(Failure())
}

func concreteSubtypeBudget(): ! throws Exception {
    raise(Failure())
}

func missingConcreteBudget(): ! {
    // Expected: specialization substitutes Failure for E.
    raise(Failure())
}

func <E> unconstrainedThrow(value: E): ! throws E {
    // Expected: E is not known to satisfy Throwable.
    throw value
}

func <E: Throwable> genericCallback(action: () -> int throws E): int throws E {
    action()
}

func useGenericCallback(): int throws ReadFailure {
    genericCallback({ read() })
}

func read(): int throws ReadFailure { 1 }

func genericCallbackWrongBudget(): int throws WriteFailure {
    genericCallback({ read() })
}

func <E: Throwable> genericOverrideBudget(action: () -> int throws E): int throws E {
    action()
}

func multipleClosedEffects(
    first: () -> int throws ReadFailure,
    second: () -> int throws WriteFailure
): int throws ReadFailure, WriteFailure {
    first() + second()
}

func <E: Throwable> openAndClosedEffectSet(
    generic: () -> int throws E,
    fixed: () -> int throws ReadFailure
): int throws E, ReadFailure {
    generic() + fixed()
}

func <E: Throwable> repeatedVariableOccurrences(
    first: () -> int throws E,
    second: () -> int throws E
): int throws E {
    first() + second()
}

func <E: Throwable> inconsistentEffectOrigins(
    first: () -> int throws E,
    second: () -> int throws ReadFailure
): int throws E {
    // Expected: inference cannot bind E to an unrelated effect and discard ReadFailure.
    first() + second()
}

func <E: Throwable> nullableEffectValue(error: E?): ! throws E {
    // Expected: nullable Throwable cannot be thrown until a non-null check refines it.
    throw error
}

// Generic effects are resolved by nominal type identity and substituted into the callable and
// function type. A solver with only a bounded single-open-pattern capability must report
// Deferred for an unresolved multi-open set; it may not select the first matching permutation.
