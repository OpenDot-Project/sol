// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative handler-effect fixture. Catch and handler effects compose at different depths.
module spec.effects.handlereffectsets

class ReadFailure: Exception {}
class WriteFailure: Exception {}
class RuntimeFailure: RuntimeException {}

func read(): int throws ReadFailure { 1 }
func write(): () throws WriteFailure {}
func unchecked(): () throws RuntimeFailure {}

func onlyReadCaught(): int throws WriteFailure {
    try {
        read()
        write()
    } catch (failure: ReadFailure) {
        0
    }
}

func runtimeCatchDoesNotRemoveRead(): int throws ReadFailure {
    try {
        read()
        unchecked()
    } catch (failure: RuntimeFailure) {
        0
    }
}

func handlerFailureSurvivesSameTry(): int throws WriteFailure {
    try {
        read()
    } catch (failure: ReadFailure) {
        write()
    }
}

func catchOuterHandlerFailure(): int {
    try {
        try {
            read()
        } catch (failure: ReadFailure) {
            write()
        }
    } catch (failure: WriteFailure) {
        0
    }
}

func finallyRunsAfterCaughtFailure(): int throws WriteFailure {
    try {
        read()
    } catch (failure: ReadFailure) {
        1
    } finally {
        write()
    }
}

func cleanupFailureDoesNotErasePrimary(): ! throws ReadFailure, WriteFailure {
    try {
        throw ReadFailure()
    } finally {
        write()
    }
}

func repeatedCatchDoesNotDuplicateEffect(): int {
    try {
        read()
        read()
    } catch (failure: ReadFailure) {
        0
    }
}

func unrelatedEffectAfterCatchRemains(): int throws WriteFailure {
    try {
        read()
    } catch (failure: ReadFailure) {
        write()
    }
}

func typeTestNarrowsRethrow(error: Throwable): ! throws ReadFailure {
    if error is ReadFailure {
        throw error
    }
    throw RuntimeFailure()
}

func broadRethrowNeedsBroadBudget(error: Throwable): ! throws Throwable {
    try {
        throw error
    } catch (failure: ReadFailure) {
        throw failure
    }
}

func missingBroadRethrowBudget(error: Throwable): ! throws ReadFailure {
    try {
        throw error
    } catch (failure: ReadFailure) {
        // Expected: the original Throwable may not be ReadFailure.
        throw error
    }
}

// Catch subtraction is local to effects reaching the protected region. It does not filter
// handler, finally, or outer cleanup effects. A surrounding try can catch a handler failure
// after it leaves the inner handler. Suppressed failures retain their own types for diagnostics
// and metadata even when a primary exception remains active.
