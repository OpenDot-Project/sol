// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Runtime contract: array element descriptors define alignment, width, and tracing behavior.
module spec.arrayiteration.descriptorlayout

func boolArrayValues(values: []bool): bool {
    var result = true
    for val item in values { result = result && item }
    result
}

func byteArrayValues(values: []byte): int {
    var total = 0
    for val item in values { total += item }
    total
}

func sbyteArrayValues(values: []sbyte): int {
    var total = 0
    for val item in values { total += item }
    total
}

func shortArrayValues(values: []short): int {
    var total = 0
    for val item in values { total += item }
    total
}

func intArrayValues(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func uintArrayValues(values: []uint): uint {
    var total: uint = 0
    for val item in values { total += item }
    total
}

func longArrayValues(values: []long): long {
    var total: long = 0
    for val item in values { total += item }
    total
}

func ulongArrayValues(values: []ulong): ulong {
    var total: ulong = 0
    for val item in values { total += item }
    total
}

func singleArrayValues(values: []single): double {
    var total: double = 0.0
    for val item in values { total += item }
    total
}

func doubleArrayValues(values: []double): double {
    var total: double = 0.0
    for val item in values { total += item }
    total
}

func stringArrayValues(values: []String): int {
    var total = 0
    for val item in values { total += item.length }
    total
}

func stringElementIsPointerTraced(values: []String): () {
    for val item in values { allocateThenUse(item) }
}

func classElementIsPointerTraced(values: []Cell): () {
    for val item in values { allocateThenUse(item) }
}

func nestedArrayNeedsNestedDescriptor(values: [][]int): int {
    var total = 0
    for val row in values {
        for val item in row { total += item }
    }
    total
}

func tupleElementNeedsAggregateLayout(values: [](int, String)): () {
    for val item in values { consume(item) }
}

func halfNeedsQualifiedElementKind(values: []half): () {
    for val item in values { consume(item) }
}

func quadNeedsQualifiedElementKind(values: []quad): () {
    for val item in values { consume(item) }
}

func elementAlignmentMatchesRuntime(values: []long): long {
    var total: long = 0
    for val item in values { total += item }
    total
}

func elementWidthDoesNotUseHostPointerSize(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func pointerWidthDoesNotDetermineElementWidth(values: []String): int {
    var total = 0
    for val item in values { total += item.length }
    total
}

func referenceSlotStoreUsesWriteBarrier(values: []String, replacement: String): () {
    for var item in values { item = replacement }
}

func arrayElementStoreUsesArrayBarrier(values: []String, replacement: String): () {
    var index = 0
    for val item in values { values[index] = replacement; index += 1 }
}

func descriptorOwnsPointerTracing(values: []Cell): () {
    for val item in values { consume(item) }
}

func invalidDescriptorIsRuntimeFailure(values: []int): () {
    // A forged/corrupt descriptor fails runtime validation; lowering cannot infer a different width.
    for val item in values { consume(item) }
}

func noRawPointerEscapesBody(values: []String): () {
    for val item in values { consume(item) }
}

class Cell { val number: int }

// The verifier and runtime agree on the exact primitive element kind and byte stride. Reference
// arrays use pointer slots and traced barriers. Unsupported layouts cannot be treated as a sequence
// of bytes or lowered with a guessed stride.
