// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: source representation decides iteration semantics.
module spec.arrayiteration.shapes

func dynamicEmpty(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func dynamicBytes(values: []byte): int {
    var total = 0
    for val item in values { total += item }
    total
}

func dynamicSignedBytes(values: []sbyte): int {
    var total = 0
    for val item in values { total += item }
    total
}

func dynamicShorts(values: []short): long {
    var total: long = 0
    for val item in values { total += item }
    total
}

func dynamicUnsigned(values: []uint): ulong {
    var total: ulong = 0
    for val item in values { total += item }
    total
}

func dynamicLongs(values: []long): long {
    var total: long = 0
    for val item in values { total += item }
    total
}

func dynamicSingles(values: []single): double {
    var total: double = 0.0
    for val item in values { total += item }
    total
}

func dynamicDoubles(values: []double): double {
    var total: double = 0.0
    for val item in values { total += item }
    total
}

func dynamicBooleans(values: []bool): bool {
    var every = true
    for val item in values { every = every && item }
    every
}

func dynamicStrings(values: []String): int {
    var total = 0
    for val item in values { total += item.length }
    total
}

func nullableElements(values: []String?): int {
    var present = 0
    for val item in values {
        if item != null { present += item.length }
    }
    present
}

func nestedDynamic(values: [][]int): int {
    var total = 0
    for val row in values {
        for val item in row { total += item }
    }
    total
}

func nestedStrings(values: [][]String): int {
    var total = 0
    for val row in values {
        for val item in row { total += item.length }
    }
    total
}

func dynamicFromParameter(values: []int): int {
    var count = 0
    for val item in values { count += 1 }
    count
}

func dynamicFromLocal(values: []int): int {
    val local = values
    var count = 0
    for val item in local { count += item }
    count
}

func dynamicFromLiteral(): int {
    var total = 0
    for val item in [2, 3, 5, 7] { total += item }
    total
}

func dynamicFromTypedEmpty(): int {
    val empty: []int = []
    var count = 0
    for val item in empty { count += item }
    count
}

func dynamicFromCall(): int {
    var total = 0
    for val item in makeValues() { total += item }
    total
}

func fixedInlineArray(values: [4]int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func fixedInlineZero(values: [0]int): int {
    var count = 0
    for val item in values { count += 1 }
    count
}

func vectorUsesCollectionProtocol(values: Vector<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func setUsesCollectionProtocol(values: HashSet<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func arrayDoesNotBecomeVector(values: []int): int {
    var index = 0
    for val item in values {
        index += 1
        consume(index, item)
    }
    index
}

func scalarIsNotIterable(value: int): () {
    // Expected type error: a scalar is not an array or a resolved cursor.
    for val item in value { consume(item) }
}

func nullableArrayNeedsProof(values: []int?): () {
    // Expected nullable-flow error: nullable array reference is not an intrinsic array source.
    for val item in values { consume(item) }
}

func nullableArrayAfterCheck(values: []int?): int {
    if values == null { return 0 }
    var total = 0
    for val item in values { total += item }
    total
}

func nullableArraySafeDefault(values: []int?): int {
    val actual = values ?: []
    var total = 0
    for val item in actual { total += item }
    total
}

func genericArray<T>(values: []T): () {
    for val item in values { consume(item) }
}

func genericConcreteArray<T>(values: []T): () {
    // The source contract permits generic element iteration after type binding. Native lowering
    // remains unavailable until the closed element layout is qualified.
    for val item in values { consume(item) }
}

// These functions differ in storage shape or element type. The implementation may share a lowering
// only after proving layout, item-copy, nullability, and lifetime equivalence for that shape.
