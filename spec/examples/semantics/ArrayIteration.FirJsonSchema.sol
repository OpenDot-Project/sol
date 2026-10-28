// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative implementation contract: FIR JSON schema 7 identifies dynamic-array iteration and
// closed generic specialization identities.
module spec.arrayiteration.firjsonschema

func serializedArrayIteration(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func serializedMutableItem(values: []int): int {
    var total = 0
    for var item in values { item += 1; total += item }
    total
}

func serializedArrayExpression(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func serializedEmptyArray(values: []int): int {
    val empty: []int = []
    var total = 0
    for val item in empty { total += item }
    total
}

func serializedNestedIteration(rows: [][]int): int {
    var total = 0
    for val row in rows {
        for val item in row { total += item }
    }
    total
}

func serializedBreak(values: []int): int {
    for val item in values { if item == 0 { break } }
    0
}

func serializedContinue(values: []int): int {
    var total = 0
    for val item in values { if item < 0 { continue }; total += item }
    total
}

func serializedReturn(values: []int): int {
    for val item in values { return item }
    0
}

func serializedSourceCall(values: []int): int {
    var total = 0
    for val item in identityArray(values) { total += item }
    total
}

func serializedStringItem(values: []String): int {
    var total = 0
    for val item in values { total += item.length }
    total
}

func schemaVersionChangesWithNodeSet(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func schemaIdsAreCompilationLocal(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func schemaDoesNotExposeHostPaths(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func flowGraphRecordsItemWrite(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func flowGraphKeepsZeroTripEdge(values: []int): int {
    var result: int
    for val item in values { result = item }
    0
}

func flowGraphPrunesKnownEmptyBody(): int {
    val empty: []int = []
    var total = 0
    for val item in empty { total += item }
    total
}

func schemaNodeContainsNoCursorMetadata(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func cursorNodeNeedsDifferentSchemaShape(values: UserIterable<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

// The serializer reports kind `FirArrayForEach` with item-slot ID, array expression, and body.
// schema 7 is compatibility-visible; identifiers are deterministic for one compilation only and
// are not stable package ABI identities. The flow graph remains verification metadata.
