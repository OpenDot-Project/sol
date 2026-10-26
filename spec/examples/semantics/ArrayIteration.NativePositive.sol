// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Qualification matrix: these dynamic-array shapes are candidates for the first verified native gate.
module spec.arrayiteration.nativepositive

func sumInt(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func sumSByte(values: []sbyte): int {
    var total = 0
    for val item in values { total += item }
    total
}

func sumByte(values: []byte): int {
    var total = 0
    for val item in values { total += item }
    total
}

func sumShort(values: []short): int {
    var total = 0
    for val item in values { total += item }
    total
}

func sumUInt(values: []uint): ulong {
    var total: ulong = 0
    for val item in values { total += item }
    total
}

func sumLong(values: []long): long {
    var total: long = 0
    for val item in values { total += item }
    total
}

func sumULong(values: []ulong): ulong {
    var total: ulong = 0
    for val item in values { total += item }
    total
}

func allTrue(values: []bool): bool {
    var result = true
    for val item in values { result = result && item }
    result
}

func allFalse(values: []bool): bool {
    var result = false
    for val item in values { result = result || item }
    result
}

func sumSingle(values: []single): double {
    var total: double = 0.0
    for val item in values { total += item }
    total
}

func sumDouble(values: []double): double {
    var total: double = 0.0
    for val item in values { total += item }
    total
}

func totalStringLength(values: []String): int {
    var total = 0
    for val item in values { total += item.length }
    total
}

func countEmptyStrings(values: []String): int {
    var count = 0
    for val item in values { if item.length == 0 { count += 1 } }
    count
}

func immutableArrayItem(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func mutableArrayItem(values: []int): int {
    var total = 0
    for var item in values { item += 1; total += item }
    total
}

func emptyArrayLiteral(): int {
    val values: []int = []
    var total = 0
    for val item in values { total += item }
    total
}

func nonEmptyLiteral(): int {
    var total = 0
    for val item in [1, 2, 3] { total += item }
    total
}

func sourceExpressionCall(values: []int): int {
    var total = 0
    for val item in identityArray(values) { total += item }
    total
}

func bodyContinue(values: []int): int {
    var total = 0
    for val item in values { if item < 0 { continue }; total += item }
    total
}

func bodyBreak(values: []int): int {
    var total = 0
    for val item in values { if item == 0 { break }; total += item }
    total
}

func nestedArrays(rows: [][]int): int {
    var total = 0
    for val row in rows {
        for val item in row { total += item }
    }
    total
}

func objectArrayRead(values: []String): int {
    var total = 0
    for val item in values { total += item.hashCode() }
    total
}

func stringReferenceSurvivesLoop(values: []String): int {
    var total = 0
    for val item in values { total += item.length }
    total
}

func referenceItemIsRooted(values: []String): () {
    for val item in values { print(item) }
}

func guestPollAppearsOnBackedge(values: []int): () {
    for val item in values { work(item) }
}

func loopCanReturnFromBody(values: []int): int {
    for val item in values { if item > 0 { return item } }
    0
}

// These examples define the input category and simple control shapes. They do not certify every
// combination merely by Cartesian product; the native qualification tests own the actual matrix.
