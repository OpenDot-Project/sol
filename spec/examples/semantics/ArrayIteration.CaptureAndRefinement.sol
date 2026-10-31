// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: loop bindings and source refinements are tied to storage identities.
module spec.arrayiteration.capture

func capturedImmutableItem(values: []int): () {
    for val item in values {
        val observe = { item }
        save(observe)
    }
}

func capturedMutableItem(values: []int): () {
    for var item in values {
        val observe = { item }
        save(observe)
        item += 1
    }
}

func capturedItemHasPerIterationStorage(values: []int): Vector<() -> int> {
    var callbacks: Vector<() -> int>
    for var item in values { callbacks.add({ item }) }
    callbacks
}

func sourceLocalNarrowingBeforeLoop(values: []String?): int {
    if values == null { return 0 }
    var total = 0
    for val item in values { total += item.length }
    total
}

func sourceLocalNarrowingIsStableAcrossReadOnlyBody(values: []String?): int {
    if values == null { return 0 }
    var total = 0
    for val item in values {
        total += item.length
        logItem(item)
    }
    total
}

func mutableSourceRebindDoesNotChangeSavedReference(values: []String?, other: []String): int {
    if values == null { return 0 }
    var source = values
    var total = 0
    for val item in source {
        source = other
        total += item.length
    }
    total
}

func callInvalidatesMutableSourceNarrowing(values: []String?): int {
    if values == null { return 0 }
    var source = values
    for val item in source {
        invokeCallbackThatMayRebind(out source)
        // The retained loop reference remains valid; the mutable local's later use needs a fresh proof.
        if source != null { consume(source.length) }
        consume(item.length)
    }
    0
}

func capturedSourceCanChangeAfterEvaluation(values: []int): int {
    var source = values
    val rebind = { source = [] }
    var total = 0
    for val item in source {
        rebind()
        total += item
    }
    total
}

func bodyRefinementDoesNotEscapeZeroTrip(values: []String?): int {
    var total = 0
    if values != null {
        for val item in values { total += item.length }
    }
    total
}

func itemRefinementIsLocalToCurrentItem(values: []String?): int {
    if values == null { return 0 }
    var total = 0
    for val item in values { total += item.length }
    total
}

func nullableElementsNeedTheirOwnProof(values: []String?): int {
    if values == null { return 0 }
    var total = 0
    for val item in values {
        if item != null { total += item.length }
    }
    total
}

func nullableItemProofDoesNotNarrowArrayType(values: []String?): int {
    if values == null { return 0 }
    var total = 0
    for val item in values {
        if item != null { total += item.length }
    }
    total
}

func loopItemAssignmentInvalidatesItsOldFact(values: []String?): int {
    if values == null { return 0 }
    var count = 0
    for var item in values {
        if item == null { item = "fallback" }
        count += item.length
    }
    count
}

func sourceMutationByIndexedWriteDoesNotChangeSourceBinding(values: []String?): int {
    if values == null { return 0 }
    var count = 0
    for val item in values {
        if item == null { values[0] = "replacement" }
        count += item?.length ?: 0
    }
    count
}

func refCallCannotAliasValItem(values: []int): () {
    for val item in values {
        // Expected mode error: immutable local item cannot be a writable ref argument.
        mutate(ref item)
    }
}

func refCallCanMutateVarItemOnly(values: []int): () {
    for var item in values { mutate(ref item) }
}

func outCallAssignsMutableItemOnNormalReturn(values: []int): () {
    for var item in values { replace(out item) }
}

func exceptionalOutCallDoesNotProveLaterBodyState(values: []int): () throws Failure {
    for var item in values {
        replaceThenThrow(out item)
        consume(item)
    }
}

func sourceArrayRefAliasDoesNotConferWritePermission(values: []int): () {
    for val item in values { consume(item) }
}

func propertyReadIsNotTheStableLoopLocal(values: []int): () {
    val owner = ArrayOwner(values)
    for val item in owner.values {
        if owner.values.length > 0 { consume(owner.values[0]) }
    }
}

func mutationOfReferencedElementDoesNotRebindItem(values: []Cell): int {
    var total = 0
    for val item in values {
        item.value += 1
        total += item.value
    }
    total
}

class Cell { var value: int }
class ArrayOwner(val values: []int)
class Failure: Exception {}

// A flow fact belongs to one local or property access identity. Capturing or mutating another alias
// cannot turn the loop item into the array element's storage location.
