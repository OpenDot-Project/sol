// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative native boundary: FIR proves source identity and array element slot invariants.
module spec.arrayiteration.firverification

func validItemSlot(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func validMutableItemSlot(values: []int): int {
    var total = 0
    for var item in values { item += 1; total += item }
    total
}

func itemSlotTypeEqualsElement(values: []String): int {
    var total = 0
    for val item in values { total += item.length }
    total
}

func itemSlotOriginEqualsBinding(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func itemSlotCannotBeParameter(values: []int, item: int): int {
    for val element in values { consume(item, element) }
    item
}

func itemSlotCannotAliasRefParameter(values: []int, ref item: int): int {
    for val element in values { consume(item, element) }
    item
}

func itemSlotDeclarationIsUnique(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func itemReadRequiresBodyEntry(values: []int): int {
    var total = 0
    for val item in values {
        if item > 0 { total += item }
    }
    total
}

func itemKilledBeforeLoop(values: []int): int {
    var item: int
    for val item in values { consume(item) }
    item
}

func iterableExpressionTypeMatchesSource(values: []int): int {
    val source = values
    var total = 0
    for val item in source { total += item }
    total
}

func iterableOriginMatchesFirArray(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func bodyOriginMatchesSourceLoop(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func arrayTypeHasExactlyOneElementArgument(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func elementTypeIsWorkspaceOwned(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func unsupportedElementFailsVerification(values: []half): half {
    var total: half = 0.0
    for val item in values { total += item }
    total
}

func userCursorCannotForgeArrayNode(values: UserCursor<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func fixedArrayCannotForgeDynamicNode(values: [4]int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func bindingNameMatchesLocal(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func bindingMutabilityMatchesLocal(values: []int): int {
    var total = 0
    for var item in values { item += 1; total += item }
    total
}

func nestedLoopIdsRemainDistinct(rows: [][]int): int {
    var total = 0
    for val row in rows {
        for val item in row { total += item }
    }
    total
}

func sameNameLoopSlotsRemainDistinct(first: []int, second: []int): int {
    var total = 0
    for val item in first { total += item }
    for val item in second { total += item }
    total
}

func serializeArrayIterationNode(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func schemaVersionIsPartOfFirOutput(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func flowGraphContainsItemWrite(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func knownEmptyFlowGraphHasNoBodyEdge(): int {
    val empty: []int = []
    var total = 0
    for val item in empty { total += item }
    total
}

// The verifier independently checks node source identity, slot identity, mutability, element type,
// supported layout, and body origin. Debug JSON is versioned and records the item slot and array.
