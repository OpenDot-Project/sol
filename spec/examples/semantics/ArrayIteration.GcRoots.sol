// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative runtime matrix: the array and reference item remain visible across GC polls.
module spec.arrayiteration.gcroots

func arrayReferenceSurvivesBodyAllocation(values: []String): int {
    var total = 0
    for val item in values {
        allocateTemporaryStrings()
        total += item.length
    }
    total
}

func arrayReferenceSurvivesExplicitCollection(values: []Cell): int {
    var total = 0
    for val item in values {
        collectGuestHeap()
        total += item.number
    }
    total
}

func referenceItemStoredInRootedSlot(values: []String): () {
    for val item in values {
        collectGuestHeap()
        consume(item)
    }
}

func mutableReferenceItemUsesTracedStore(values: []String, replacement: String): () {
    for var item in values {
        item = replacement
        collectGuestHeap()
        consume(item)
    }
}

func explicitReferenceElementStoreUsesBarrier(values: []String, replacement: String): () {
    var index = 0
    for val item in values {
        values[index] = replacement
        collectGuestHeap()
        consume(item)
        index += 1
    }
}

func noInteriorPointerSurvivesPoll(values: []String): int {
    var total = 0
    for val item in values {
        collectGuestHeap()
        total += item.length
    }
    total
}

func arrayExpressionTemporaryIsRooted(values: []String): int {
    var total = 0
    for val item in cloneArray(values) {
        collectGuestHeap()
        total += item.length
    }
    total
}

func arrayRootPrecedesPotentialPoll(values: []Cell): int {
    var total = 0
    for val item in values {
        allocateCells(100)
        total += item.number
    }
    total
}

func arrayRootLivesThroughNestedCalls(values: []String): int {
    var total = 0
    for val item in values {
        helperThatMayCollect(item)
        total += item.length
    }
    total
}

func arrayRootRemovedAtLoopExit(values: []Cell): int {
    var total = 0
    for val item in values { total += item.number }
    collectGuestHeap()
    total
}

func arrayRootRemovedAtBreak(values: []Cell): int {
    for val item in values {
        collectGuestHeap()
        break
    }
    collectGuestHeap()
    0
}

func arrayRootRemovedAtReturn(values: []Cell): int {
    for val item in values {
        collectGuestHeap()
        return item.number
    }
    0
}

func arrayRootIsNotAHostClrReference(values: []Cell): int {
    // Sol objects are owned by the guest Yellow Tea process. The compiler CLR cannot trace this array.
    var total = 0
    for val item in values { total += item.number }
    total
}

func nestedArrayOuterAndInnerRoots(rows: [][]String): int {
    var total = 0
    for val row in rows {
        for val item in row {
            collectGuestHeap()
            total += item.length
        }
    }
    total
}

func referenceArrayElementIsVisitedByDescriptor(values: []Cell): () {
    for val item in values { consume(item) }
}

func scalarArrayNeedsNoElementReferenceRoot(values: []int): int {
    var total = 0
    for val item in values {
        collectGuestHeap()
        total += item
    }
    total
}

func emptyArrayHasNoItemRoot(): () {
    val values: []String = []
    for val item in values { consume(item) }
}

func emptyArrayStillKeepsSourceExpressionTemporaryUntilExit(): () {
    for val item in allocateEmptyStringArray() { consume(item) }
}

func pollRunsAfterCompletedIteration(values: []int): () {
    for val item in values { doWork(item) }
}

func continueRunsBackedgePoll(values: []int): () {
    for val item in values { if item < 0 { continue }; doWork(item) }
}

func breakDoesNotRequireBackedgePoll(values: []int): () {
    for val item in values { break }
}

func returnDoesNotRequireBackedgePoll(values: []int): int {
    for val item in values { return item }
    0
}

class Cell { val number: int }

// The retained reference is a strong guest root. The current collector is non-moving; a moving
// collector must add an explicit relocation/update rule before preserving this lowering strategy.
