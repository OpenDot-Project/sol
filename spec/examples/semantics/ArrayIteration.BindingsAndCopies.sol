// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: each element load initializes a fresh value slot with source mutability.
module spec.arrayiteration.bindings

func immutablePrimitive(values: []int): int {
    var total = 0
    for val item in values {
        total += item
    }
    total
}

func immutableAssignmentRejected(values: []int): () {
    for val item in values {
        // Expected error: the loop item is declared with val.
        item = 4
    }
}

func mutablePrimitiveCopy(values: []int): int {
    var total = 0
    for var item in values {
        item = item + 1
        total += item
    }
    total
}

func mutableItemNotStored(values: []int): int {
    var firstPass = 0
    for var item in values {
        item += 1
        firstPass += item
    }
    var secondPass = 0
    for val item in values {
        secondPass += item
    }
    firstPass + secondPass
}

func itemCompoundAssignmentIsLocal(values: []int): int {
    var result = 0
    for var item in values {
        item *= 2
        result += item
    }
    result
}

func itemPostfixLikeValueUpdate(values: []int): int {
    var total = 0
    for var item in values {
        val old = item
        item += 1
        total += old + item
    }
    total
}

func bodyCopyCanBePassedByValue(values: []int): int {
    var total = 0
    for var item in values {
        total += adjustByValue(item)
    }
    total
}

func bodyCopyCanBePassedByRef(values: []int): int {
    var total = 0
    for var item in values {
        adjustByRef(ref item)
        total += item
    }
    total
}

func itemCannotBePassedAsRefWhenImmutable(values: []int): () {
    for val item in values {
        // Expected error: ref requires writable local storage.
        mutate(ref item)
    }
}

func itemCanBeInitializedOut(values: []int): int {
    var result = 0
    for var item in values {
        writeNewValue(out item)
        result += item
    }
    result
}

func outMutationDoesNotModifyArray(values: []int): int {
    var total = 0
    for var item in values {
        overwrite(out item)
        total += item
    }
    total
}

func perIterationItemDoesNotCarryPreviousWrite(values: []int): int {
    var previous = 0
    var total = 0
    for var item in values {
        item = previous
        total += item
        previous += 1
    }
    total
}

func shadowsOuterName(values: []int): int {
    var item = 100
    var total = 0
    for val item in values {
        total += item
    }
    total + item
}

func sameSpellingUsesDifferentLocalIdentity(first: []int, second: []int): int {
    var item = 1
    var total = 0
    for val item in first { total += item }
    for var item in second { item += 2; total += item }
    total + item
}

func itemScopeEndsWithLoop(values: []int): int {
    for val current in values { consume(current) }
    // Expected name-resolution error: the item exists only in the loop scope.
    current
}

func itemLocalCanBeReadWithinNestedBranch(values: []int): int {
    var total = 0
    for val item in values {
        if item > 0 {
            total += item
        } else {
            total -= item
        }
    }
    total
}

func nestedItemShadowsOuterItem(rows: [][]int): int {
    var total = 0
    for val item in rows {
        for val item in item {
            total += item
        }
    }
    total
}

class MutableBox {
    var number: int
}

func copiedReferenceKeepsObjectIdentity(values: []MutableBox): int {
    var total = 0
    for val item in values {
        item.number += 1
        total += item.number
    }
    total
}

func assigningReferenceLocalDoesNotReplaceElement(values: []MutableBox, replacement: MutableBox): () {
    for var item in values {
        item = replacement
    }
}

func explicitReferenceElementReplacement(values: []MutableBox, replacement: MutableBox): () {
    var index = 0
    for val item in values {
        values[index] = replacement
        consume(item)
        index += 1
    }
}

func nullableElementCopy(values: []MutableBox?): int {
    var present = 0
    for val item in values {
        if item != null { present += item.number }
    }
    present
}

func nullableItemReassignmentIsLocal(values: []MutableBox?, replacement: MutableBox): int {
    var count = 0
    for var item in values {
        item = replacement
        count += item.number
    }
    count
}

func itemReferenceCanEscapeWhenTypeAllows(values: []MutableBox): MutableBox? {
    for val item in values {
        if item.number > 0 { return item }
    }
    null
}

func mutableItemCaptureObservesItsIterationCell(values: []int): () -> int {
    var callbacks: Vector<() -> int>
    for var item in values {
        callbacks.add({ item })
        item += 1
    }
    callbacks.first()
}

func refLikeItemCaptureIsRejected(values: []Span<int>): () {
    for val item in values {
        // Expected lifetime error: an iteration-scoped ref-like item cannot escape by capture.
        val callback = { item.length }
        save(callback)
    }
}

// `val` protects the local binding. It does not deep-freeze a referenced object. `var` permits
// rebinding the local copy; it does not imply write-through iteration or a writable array alias.
