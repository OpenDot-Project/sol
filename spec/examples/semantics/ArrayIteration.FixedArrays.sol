// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: inline fixed arrays have a known extent and distinct value storage.
module spec.arrayiteration.fixedarrays

func fixedTwo(values: [2]int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func fixedZero(values: [0]int): int {
    var count = 0
    for val item in values { count += 1 }
    count
}

func fixedOne(values: [1]String): int {
    var total = 0
    for val item in values { total += item.length }
    total
}

func fixedLengthDoesNotRequireRuntimeSearch(values: [8]int): int {
    var count = 0
    for val item in values { count += 1 }
    count
}

func fixedElementTypeIsExact(values: [4]byte): int {
    var total = 0
    for val item in values { total += item }
    total
}

func fixedArrayIterationDoesNotAllocateCursor(values: [4]int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func fixedArrayIsCopiedByValueParameter(values: [4]int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func fixedElementItemIsCopied(values: [4]int): int {
    var total = 0
    for var item in values {
        item += 1
        total += item
    }
    total
}

func fixedItemWriteDoesNotChangeInlineSource(values: [4]int): int {
    var first = 0
    for var item in values { item += 1; first += item }
    var second = 0
    for val item in values { second += item }
    first + second
}

func fixedArrayLiteralHasExactLength(): int {
    var total = 0
    for val item in [4, 5, 6] { total += item }
    total
}

func fixedEmptyLiteralNeedsElementType(): int {
    val values: [0]int = []
    var total = 0
    for val item in values { total += item }
    total
}

func fixedArrayMayContainReferenceValues(values: [3]String): int {
    var total = 0
    for val item in values { total += item.length }
    total
}

func fixedNullableElements(values: [3]String?): int {
    var count = 0
    for val item in values { if item != null { count += item.length } }
    count
}

func fixedArrayNestedInDynamic(values: [][3]int): int {
    var total = 0
    for val row in values {
        for val item in row { total += item }
    }
    total
}

func dynamicArrayNestedInFixed(values: [3][]int): int {
    var total = 0
    for val row in values {
        for val item in row { total += item }
    }
    total
}

func fixedArrayCannotBeResized(values: [3]int): () {
    for val item in values {
        // Expected type error: fixed-array length is encoded in the type.
        values.add(item)
    }
}

func fixedLoopZeroTripProof(): int {
    val values: [0]int = []
    var output: int
    for val item in values { output = item }
    // Positive definite assignment result: the body is unreachable, so output remains unassigned.
    output
}

func fixedNonEmptyProof(values: [3]int): int {
    var output: int
    for val item in values { output = item; break }
    output
}

func fixedContinueVisitsEveryRemainingItem(values: [3]int): int {
    var visits = 0
    for val item in values { visits += 1; continue }
    visits
}

func fixedBreakMaySkipRemainingItems(values: [3]int): int {
    var visits = 0
    for val item in values { visits += 1; break }
    visits
}

func fixedArrayAndManualIndexHaveSameOrder(values: [3]int): bool {
    val fromLoop = collectByIteration(values)
    val fromIndex = collectByIndex(values)
    fromLoop == fromIndex
}

func unsupportedFixedArrayNativeStillHasSourceMeaning(values: [3]int): int {
    // Source semantics are defined even while the first native FIR gate rejects this layout.
    var total = 0
    for val item in values { total += item }
    total
}

func fixedZeroEffectsAreUnreachable(): () {
    val values: [0]int = []
    for val item in values { failEffect(item) }
}

func fixedSizeDoesNotMakeItemAnAlias(values: [3]MutableCell): () {
    for var item in values { item = newCell() }
}

class MutableCell { var value: int }

// Fixed-array length may prove non-empty or empty from its type identity. The native dynamic-array
// FIR node does not accept this representation; a separate typed operation and verifier rule are
// needed before lowering fixed arrays.
