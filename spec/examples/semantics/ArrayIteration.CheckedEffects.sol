// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: source-expression and body effects belong to distinct reachable edges.
module spec.arrayiteration.checkedeffects

class Failure: Exception {}
class SourceFailure: Exception {}

func harmless(value: int): int = value
func mayFail(value: int): int throws Failure = value

func bodyEffectIncluded(values: []int): int throws Failure {
    var total = 0
    for val item in values { total += mayFail(item) }
    total
}

func bodyEffectMissing(values: []int): int {
    // Expected unhandled checked effect: the array may contain an element.
    for val item in values { mayFail(item) }
    0
}

func sourceEffectIncluded(): int throws SourceFailure {
    var total = 0
    for val item in sourceMayFail() { total += item }
    total
}

func sourceEffectMissing(): int {
    // Expected unhandled effect from evaluating the iterable expression.
    for val item in sourceMayFail() { consume(item) }
    0
}

func sourceAndBodyEffectsAreUnioned(values: []int): int throws SourceFailure, Failure {
    var total = 0
    for val item in sourceWithBody(values) { total += mayFail(item) }
    total
}

func sourceEffectRemainsForEmptyLiteral(): () throws SourceFailure {
    for val item in sourceMayReturnEmpty() { consume(item) }
}

func emptyLiteralRemovesBodyEffect(): () {
    val empty: []int = []
    for val item in empty { mayFail(item) }
}

func emptyLiteralDoesNotRemoveSourceEffect(): () throws SourceFailure {
    val empty = emptyArrayFactory()
    for val item in empty { harmless(item) }
}

func emptySourceTypeKnownAtDeclaration(): () {
    val empty: []int = []
    for val item in empty { harmless(item) }
}

func maybeEmptyParameterKeepsEffect(values: []int): () throws Failure {
    for val item in values { mayFail(item) }
}

func bodyEffectAfterConditionalContinue(values: []int): () throws Failure {
    for val item in values {
        if item < 0 { continue }
        mayFail(item)
    }
}

func bodyEffectBeforeBreak(values: []int): () throws Failure {
    for val item in values {
        mayFail(item)
        break
    }
}

func bodyEffectAfterBreakIsUnreachable(values: []int): () {
    for val item in values {
        break
        // Expected unreachable statement; this operation contributes no execution effect.
        mayFail(item)
    }
}

func effectOnReturnPath(values: []int): int throws Failure {
    for val item in values {
        return mayFail(item)
    }
    0
}

func effectOnThrowPath(values: []int): () throws Failure {
    for val item in values {
        mayFail(item)
        throw Failure("body")
    }
}

func catchesBodyEffect(values: []int): int {
    var total = 0
    for val item in values {
        try {
            total += mayFail(item)
        } catch (error: Failure) {
            total -= 1
        }
    }
    total
}

func catchesSourceEffect(): int {
    try {
        for val item in sourceMayFail() { consume(item) }
    } catch (error: SourceFailure) {
        return 1
    }
    0
}

func catchDoesNotCatchDifferentBodyEffect(values: []int): () throws Failure {
    try {
        for val item in values { mayFail(item) }
    } catch (error: SourceFailure) {
        consume(error)
    }
}

func effectFromOneBodyBranch(values: []int, fail: bool): () throws Failure {
    for val item in values {
        if fail { mayFail(item) } else { harmless(item) }
    }
}

func bodyEffectNotRequiredAfterStaticEmpty(values: []int): () {
    val empty: []int = []
    for val item in empty {
        if values.length > 0 { mayFail(values[0]) }
    }
}

func sourceEffectStillRequiredWhenBodyIsStaticallyEmpty(): () throws SourceFailure {
    for val item in sourceMayReturnEmpty() { harmless(item) }
}

func effectInNestedInnerBody(values: [][]int): () throws Failure {
    for val row in values {
        for val item in row { mayFail(item) }
    }
}

func effectInInnerSourceRunsPerOuterIteration(values: []int): () throws SourceFailure {
    for val outer in values {
        for val inner in makeInnerOrFail(outer) { consume(inner) }
    }
}

func sourceFailureBeforeItemAssignment(values: []int): () throws SourceFailure {
    for val item in makeArrayOrFail(values) { consume(item) }
}

func bodyFailureAfterItemAssignment(values: []int): () throws Failure {
    for val item in values { mayFail(item) }
}

func functionTypeBodyEffect(values: []int, callback: (int) -> int throws Failure): int
    throws Failure {
    var total = 0
    for val item in values { total += callback(item) }
    total
}

func callbackEffectMissing(values: []int, callback: (int) -> int throws Failure): int {
    // Expected unhandled effect from invoking the selected callback in a reachable body.
    for val item in values { callback(item) }
    0
}

func uncheckedRuntimeFailureIsNotCheckedEffect(values: []int): int {
    var total = 0
    for val item in values { total += uncheckedOperation(item) }
    total
}

func cleanupEffectIsDistinctFromBodyEffect(values: []int): () throws Failure, SourceFailure {
    for val item in values {
        defer { cleanupMayFail() }
        mayFail(item)
    }
}

func staticEmptyDoesNotRegisterCleanup(): () {
    val empty: []int = []
    for val item in empty {
        defer { cleanupMayFail() }
        consume(item)
    }
}

// A checked-effect set is a union over reachable escaping operations, after catch subtraction.
// The source expression executes once. The dynamic-array body may execute zero or more times. A
// static empty literal removes only the body path; it never rewrites effects of the source expression.
