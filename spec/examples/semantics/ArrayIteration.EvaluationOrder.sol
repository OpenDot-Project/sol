// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: source expression and fixed length are observed once, in order.
module spec.arrayiteration.evaluationorder

func record(value: int): int { trace(value); value }
func source(): []int { record(1); [10, 20] }

func callSourceOnce(): int {
    var total = 0
    for val item in source() { total += item }
    total
}

func callSourceReturningEmptyOnce(): int {
    var total = 0
    for val item in emptySourceWithTrace() { total += item }
    total
}

func localArraySnapshot(values: []int, replacement: []int): int {
    var sourceArray = values
    var total = 0
    for val item in sourceArray {
        sourceArray = replacement
        total += item
    }
    total
}

func propertyReadOnce(owner: ArrayHolder): int {
    var total = 0
    for val item in owner.values {
        total += item
    }
    total
}

func getterSideEffectPrecedesBody(owner: ArrayHolder): int {
    var total = 0
    for val item in owner.loggedValues {
        trace(item)
        total += item
    }
    total
}

func indexExpressionOnce(rows: [][]int, selector: int): int {
    var total = 0
    for val item in rows[nextIndex(selector)] {
        total += item
    }
    total
}

func conditionalSourceOnce(left: []int, right: []int, chooseLeft: bool): int {
    var total = 0
    for val item in if chooseLeft { left } else { right } {
        total += item
    }
    total
}

func coalescedSourceOnce(values: []int?): int {
    var total = 0
    for val item in values ?: fallbackArray() {
        total += item
    }
    total
}

func iterableFailurePreventsBody(): int throws IterationFailure {
    var bodyCount = 0
    for val item in failingArraySource() {
        bodyCount += item
    }
    bodyCount
}

func sourceArgumentsKeepTextualOrder(first: []int, second: []int): int {
    var total = 0
    for val item in selectArray(first = traceArray(1, first), second = traceArray(2, second)) {
        total += item
    }
    total
}

func nestedSourcesEvaluateOuterFirst(outer: [][]int): int {
    var total = 0
    for val row in traceRows(outer) {
        for val item in traceArray(2, row) {
            total += item
        }
    }
    total
}

func nestedSourcesEvaluateInnerPerOuter(outer: []int): int {
    var total = 0
    for val first in outer {
        for val second in makeInner(first) {
            total += first * second
        }
    }
    total
}

func arrayLiteralElementsPrecedeTheFirstTest(): int {
    var total = 0
    for val item in [traceValue(1), traceValue(2), traceValue(3)] {
        total += item
    }
    total
}

func sourceRunsEvenWhenNoBodyEntries(): int {
    var bodyCount = 0
    for val item in traceEmptyArray() {
        bodyCount += 1
    }
    bodyCount
}

func sourceEvaluationDoesNotRepeatAtBackedge(values: []int): int {
    var total = 0
    for val item in chooseArrayWithCounter(values) {
        total += item
    }
    total
}

func lengthSnapshotIsAfterSourceEvaluation(values: []int): int {
    mutateSourceBeforeReturning(values)
    var total = 0
    for val item in values {
        total += item
    }
    total
}

func sameReferenceIsUsedForEveryLoad(values: []int, replacement: []int): int {
    var sourceArray = values
    var total = 0
    for val item in sourceArray {
        sourceArray = replacement
        total += item
        if total < 0 { break }
    }
    total
}

func indexedMutationUsesOriginalReference(values: []int, replacement: []int): int {
    var sourceArray = values
    var index = 0
    var total = 0
    for val item in sourceArray {
        sourceArray[index] = item + 1
        sourceArray = replacement
        total += sourceArray[index - 1]
        index += 1
    }
    total
}

func sourcePropertyChangesInsideBodyDoNotRetarget(owner: ArrayHolder, replacement: []int): int {
    var total = 0
    for val item in owner.values {
        owner.values = replacement
        total += item
    }
    total
}

func secondPropertyReadIsSeparateExpression(owner: ArrayHolder): int {
    var total = 0
    for val item in owner.values {
        // This source-level property access is a new read, unrelated to the retained loop reference.
        total += owner.values.length
        total += item
    }
    total
}

func sourceExpressionHasACheckedEffect(): int throws IterationFailure {
    var total = 0
    for val item in acquireArrayOrThrow() {
        total += item
    }
    total
}

func bodyNeverStartsAfterSourceFailure(): () throws IterationFailure {
    for val item in acquireArrayOrThrow() {
        use(item)
    }
}

func bodyMayThrowAfterSuccessfulSource(values: []int): () throws IterationFailure {
    for val item in values {
        failForValue(item)
    }
}

func sourceFailureAndBodyFailureHaveDifferentOrigins(values: []int): () throws IterationFailure {
    var sourceArray = prepareArrayOrThrow(values)
    for val item in sourceArray {
        failForValue(item)
    }
}

func iterableAliasDoesNotCloneTheElements(values: []String): int {
    var total = 0
    for val item in values {
        total += item.length
    }
    total
}

func outerMutationDoesNotChangeSavedInnerArray(row: []int, replacement: []int): int {
    var sourceRow = row
    var total = 0
    for val item in sourceRow {
        sourceRow = replacement
        total += item
    }
    total
}

func independentLoopExpressionsRunIndependently(first: []int, second: []int): int {
    var total = 0
    for val item in first { total += item }
    for val item in second { total += item }
    total
}

func sequentialSourceMutationAppliesToLaterLoop(values: []int, replacement: []int): int {
    var sourceArray = values
    var total = 0
    for val item in sourceArray { total += item }
    sourceArray = replacement
    for val item in sourceArray { total += item }
    total
}

// The first source evaluation determines the active traversal. Later independent expressions are
// evaluated when reached. No rule permits a compiler to hoist a source call across a side effect.
