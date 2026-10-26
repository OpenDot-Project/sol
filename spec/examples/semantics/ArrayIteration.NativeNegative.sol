// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Qualification matrix: source-valid loops can remain outside the closed native verifier subset.
module spec.arrayiteration.nativenegative

func fixedArrayIsNotDynamic(values: [3]int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func inlineArrayNotAcceptedAsManagedPointer(values: [4]String): int {
    var total = 0
    for val item in values { total += item.length }
    total
}

func <T> genericElementLayoutNeedsSpecialization(values: []T): () {
    for val item in values { consume(item) }
}

func halfElementHasUnqualifiedWidth(values: []half): half {
    var total: half = 0.0
    for val item in values { total += item }
    total
}

func quadElementHasUnqualifiedWidth(values: []quad): quad {
    var total: quad = 0.0
    for val item in values { total += item }
    total
}

func nestedManagedArrayLayoutIsNotClosed(values: [][]int): int {
    var total = 0
    for val row in values {
        for val item in row { total += item }
    }
    total
}

func nullableArrayReferenceNeedsNullCheck(values: []int?): int {
    var total = 0
    for val item in values { total += item }
    total
}

func arrayOfNullableStringNeedsNullableElementLayout(values: []String?): int {
    var total = 0
    for val item in values { if item != null { total += item.length } }
    total
}

func userIteratorRequiresResolvedCalls(values: UserIterable<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func userCursorCleanupRequiresExitGraph(values: ResourceIterable<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func iteratorCurrentCanThrowNeedsNativeEh(values: ThrowingIterable<int>): int
    throws IterationFailure {
    var total = 0
    for val item in values { total += item }
    total
}

func iteratorCloseCanThrowNeedsNativeEh(values: ResourceIterable<int>): int
    throws CloseFailure {
    var total = 0
    for val item in values { total += item }
    total
}

func refLikeCursorNeedsScopedLowering(values: Span<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func escapingCursorClosureNeedsBorrowProof(values: Span<int>): () {
    for val item in values { save({ values[0] }) }
}

func capturedLoopItemNeedsClosureLowering(values: []int): () {
    for var item in values { save({ item }) }
}

func bodyDeferNeedsNativeCleanupLowering(values: []int): () {
    for val item in values { defer { cleanup(item) } }
}

func bodyFinallyNeedsNativeEhLowering(values: []int): () {
    for val item in values {
        try { consume(item) } finally { cleanup(item) }
    }
}

func statefulObjectReceiverNeedsStorageInitialization(values: []int): int {
    for val item in values { state.total += item }
    state.total
}

func userOperatorInBodyNeedsExactCallLowering(values: []Meter): Meter {
    var total = Meter(0)
    for val item in values { total += item }
    total
}

func unsupportedArrayStoreDoesNotEraseLoop(values: []HalfStruct): () {
    for val item in values { consume(item) }
}

func ambiguousIterableDoesNotGuessArray(values: MaybeIterable): () {
    // Expected unresolved or ambiguous source category; do not lower by spelling alone.
    for val item in values { consume(item) }
}

func dynamicArrayStillNeedsSupportedBody(values: []int): () {
    for val item in values { unsupportedIntrinsic(item) }
}

class IterationFailure: Exception {}
class CloseFailure: Exception {}

// A source-valid loop is not automatically native-supported. FIR must reject an unsupported node
// with a diagnostic and preserve the source origin; it cannot substitute an empty body or guess ABI.
