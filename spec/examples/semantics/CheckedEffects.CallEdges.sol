// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative effect fixture. Source parsing does not perform checked-effect verification.
module spec.effects.calledges

open class NetworkFailure: Exception {}
class TimeoutFailure: NetworkFailure {}
class FormatFailure: Exception {}
class BoundsFailure: RuntimeException {}

func connect(): int throws NetworkFailure { 1 }
func timeOut(): int throws TimeoutFailure { 2 }
func format(): int throws FormatFailure { 3 }

func catchesAllNetwork(): int {
    try {
        connect()
    } catch (failure: NetworkFailure) {
        0
    }
}

func subtypeBudgetCoversCall(): int throws Exception {
    connect()
}

func sameBudgetCoversCall(): int throws NetworkFailure {
    connect()
}

func unrelatedBudgetCannotCoverCall(): int throws FormatFailure {
    // Expected: uncovered checked NetworkFailure at this call.
    connect()
}

func uncheckedCallNeedsNoBudget(): int {
    // This value may still throw at runtime; its subtree is unchecked by the type hierarchy.
    BoundsFailure().hashCode()
}

func sequentialEffectsUnion(flag: bool): int throws NetworkFailure, FormatFailure {
    connect()
    if flag {
        format()
    }
    1
}

func catchRemovesOneEffect(flag: bool): int throws FormatFailure {
    try {
        connect()
        if flag {
            format()
        }
    } catch (failure: NetworkFailure) {
        0
    }
}

func catchDoesNotRemoveSibling(flag: bool): int throws FormatFailure {
    try {
        connect()
        if flag {
            format()
        }
    } catch (failure: NetworkFailure) {
        0
    }
}

func staticTypeDefinesEffect(error: Throwable): ! throws Throwable {
    throw error
}

func checkedEvenWhenRuntimeSubtypeIsUnchecked(error: Throwable): ! throws Throwable {
    // Expected: broad static type keeps the checked obligation until explicit narrowing.
    throw error
}

func explicitRuntimeNarrowing(error: Throwable): ! {
    if error is RuntimeException {
        throw error
    }
    throw BoundsFailure()
}

func effectDoesNotDependOnArgumentName(): int throws NetworkFailure {
    val result = connect()
    result
}

func noImplicitWrapping(): int throws NetworkFailure {
    // The call's failure is not converted to an error code or string.
    connect()
}

func mainBoundaryIsNotAUserExemption(args: []String): () throws NetworkFailure {
    connect()
}

// The process entry adapter may report an uncaught Throwable, but this function's ordinary
// call still has to satisfy its own checked budget.
