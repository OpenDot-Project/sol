// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: item copies, array aliases, and explicit indexed stores are distinct operations.
module spec.arrayiteration.writes

func readEachOriginal(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func mutateCurrentIndexBySeparateCounter(values: []int): int {
    var index = 0
    var total = 0
    for val item in values {
        values[index] = item + 1
        total += item
        index += 1
    }
    total
}

func laterLoadSeesEarlierElementWrite(values: []int): int {
    var index = 0
    var total = 0
    for val item in values {
        if index == 0 && values.length > 1 { values[1] = 40 }
        total += item
        index += 1
    }
    total
}

func earlierIndexWriteDoesNotRevisitIndex(values: []int): int {
    var total = 0
    for val item in values {
        total += item
        if item > 0 { values[0] = 0 }
    }
    total
}

func writeDoesNotChangeLength(values: []int): int {
    var writes = 0
    for var item in values {
        values[writes] = item
        writes += 1
    }
    writes
}

func aliasWriteIsVisible(values: []int): int {
    val alias = values
    var index = 0
    var total = 0
    for val item in values {
        if index > 0 { total += alias[index - 1] }
        alias[index] = item * 2
        index += 1
    }
    total
}

func rebindingOneAliasKeepsAnother(values: []int, replacement: []int): int {
    var source = values
    val alias = values
    var total = 0
    for val item in source {
        source = replacement
        total += alias[0] + item
    }
    total
}

func arrayExpressionLocalIsRetained(values: []int, replacement: []int): int {
    var active = values
    var total = 0
    for val item in active {
        active = replacement
        total += item
    }
    total
}

func arrayPropertyIsRetained(owner: ArrayOwner, replacement: []int): int {
    var total = 0
    for val item in owner.loggedValues {
        owner.values = replacement
        total += item
    }
    total
}

func expressionFactoryStillRunsOnlyOnce(values: []int): int {
    var total = 0
    for val item in countedFactory(values) {
        total += item
    }
    total
}

class ArrayOwner {
    var values: []int
    var reads: int
    val loggedValues: []int { get { reads += 1; values } }
}

func getterResultIsRetained(owner: ArrayOwner): int {
    var total = 0
    for val item in owner.loggedValues {
        owner.values = []
        total += item
    }
    total
}

func getterReadCountIsOne(owner: ArrayOwner): int {
    for val item in owner.loggedValues { consume(item) }
    owner.reads
}

func indexedSourceReceiverIsEvaluatedOnce(owners: []ArrayOwner, index: int): int {
    var total = 0
    for val item in owners[index].values {
        total += item
    }
    total
}

func repeatedIndexedReadIsNotTheLoopReceiver(values: []int): int {
    var total = 0
    for val item in values {
        total += item
        total += values[0]
    }
    total
}

func currentElementMutationDoesNotReplaceLocalCopy(values: []MutableCell): int {
    var total = 0
    for val item in values {
        val old = item.value
        item.value = old + 1
        total += old
    }
    total
}

class MutableCell { var value: int }

func localReferenceRebindDoesNotMutateArray(values: []MutableCell, replacement: MutableCell): () {
    for var item in values {
        item = replacement
    }
}

func arraySlotReplacementIsExplicit(values: []MutableCell, replacement: MutableCell): () {
    var index = 0
    for val item in values {
        values[index] = replacement
        index += 1
        consume(item)
    }
}

func sameObjectMutationCanBeSeenLater(values: []MutableCell): int {
    var index = 0
    var result = 0
    for val item in values {
        if index == 0 && values.length > 1 { values[1].value = 17 }
        result += item.value
        index += 1
    }
    result
}

func copiedStructItemMutationIsLocal(values: []Point): int {
    var result = 0
    for var point in values {
        point.x += 1
        result += point.x
    }
    result
}

ref struct Point { var x: int }

func inlineArrayItemIsValueCopied(values: [3]Point): int {
    var total = 0
    for var point in values {
        point.x += 1
        total += point.x
    }
    total
}

func mutationThroughNestedArray(values: [][]int): int {
    var total = 0
    for val row in values {
        for var item in row {
            item += 1
            total += item
        }
    }
    total
}

func nestedElementStoreIsExplicit(values: [][]int): () {
    var rowIndex = 0
    for val row in values {
        var column = 0
        for val item in row {
            values[rowIndex][column] = item + 1
            column += 1
        }
        rowIndex += 1
    }
}

func arrayLengthIsNotRecomputedFromRebinding(values: []int, replacement: []int): int {
    var source = values
    var count = 0
    for val item in source {
        source = replacement
        count += 1
    }
    count
}

func aliasMutationsRemainVisibleAfterLoop(values: []int): int {
    val alias = values
    for var item in values { item = 0 }
    alias[0]
}

// The active reference is stable, while element values are loaded at their turn. A compiler must
// not replace this behavior with an eager element snapshot or implicit element reference.
