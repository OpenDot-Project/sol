// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: nullable receiver facts and nullable elements have separate storage identities.
module spec.arrayiteration.nullableflow

func nullableArrayRejectedWithoutProof(values: []String?): int {
    var total = 0
    for val item in values { total += item.length }
    total
}

func nullableArrayAcceptedAfterEarlyReturn(values: []String?): int {
    if values == null { return 0 }
    var total = 0
    for val item in values { total += item.length }
    total
}

func nullableArrayAcceptedAfterThrow(values: []String?): int throws Failure {
    if values == null { throw Failure("null") }
    var total = 0
    for val item in values { total += item.length }
    total
}

func nullableArrayCoalesce(values: []String?): int {
    val source: []String = values ?: []
    var total = 0
    for val item in source { total += item.length }
    total
}

func nullableElementsNeedElementProof(values: []String?): int {
    if values == null { return 0 }
    var total = 0
    for val item in values {
        if item != null { total += item.length }
    }
    total
}

func nullableElementDereferenceRejected(values: []String?): int {
    if values == null { return 0 }
    var total = 0
    for val item in values {
        // Expected nullable-flow error: proving the array non-null says nothing about its elements.
        total += item.length
    }
    total
}

func elementProofDoesNotNarrowOtherElements(values: []String?): int {
    if values == null { return 0 }
    var total = 0
    for val item in values {
        if item != null { total += values[0].length }
    }
    total
}

func localCopyCanBeNarrowed(values: []String?): int {
    if values == null { return 0 }
    var total = 0
    for val item in values {
        if item == null { continue }
        total += item.length
    }
    total
}

func mutableItemCanBeReassignedToNonNull(values: []String?): int {
    if values == null { return 0 }
    var total = 0
    for var item in values {
        if item == null { item = "fallback" }
        total += item.length
    }
    total
}

func itemAssignmentKillsPreviousNarrowing(values: []String?): int {
    if values == null { return 0 }
    var total = 0
    for var item in values {
        if item == null { item = "fallback" }
        item = nullableReplacement()
        // Expected error unless the replacement is proven non-null.
        total += item.length
    }
    total
}

func arrayElementWriteDoesNotNarrowCopiedItem(values: []String?): int {
    if values == null { return 0 }
    var total = 0
    for val item in values {
        if item != null { values[0] = null }
        total += item.length
    }
    total
}

func elementCopyRemainsStableAfterArrayWrite(values: []String?): int {
    if values == null { return 0 }
    var total = 0
    for val item in values {
        if item != null {
            values[0] = null
            total += item.length
        }
    }
    total
}

func mutableArrayLocalCanBeRebound(values: []String?, other: []String): int {
    if values == null { return 0 }
    var active = values
    var total = 0
    for val item in active {
        active = other
        total += item.length
    }
    total
}

func nullableSourceCallCannotBeNarrowedByOldLocal(values: []String?): int {
    if values == null { return 0 }
    var active = values
    mutateArrayReference(out active)
    var total = 0
    for val item in active { total += item.length }
    total
}

func getterResultHasOneLoopSnapshot(owner: NullableArrayOwner): int {
    var total = 0
    for val item in owner.values ?: [] { total += item.length }
    total
}

func getterSecondReadNeedsIndependentNullCheck(owner: NullableArrayOwner): int {
    var total = 0
    for val item in owner.values ?: [] {
        if owner.values != null { total += owner.values.length }
    }
    total
}

func safeArrayReadDoesNotCreateIteration(values: []String?): int {
    val length = values?.length ?: 0
    length
}

func safeArrayReadStillRequiresExplicitLoopSource(values: []String?): int {
    val source = values ?: []
    var total = 0
    for val item in source { total += item.length }
    total
}

func nullableArrayTypeIsNotNullableElementType(values: []String?): int {
    if values == null { return 0 }
    values.length
}

class NullableArrayOwner { val values: []String? }
class Failure: Exception {}

// `[]T?` and `[]T` with nullable reference elements remain different types. Flow facts for the
// iterable receiver do not imply facts about the copied item or a later independent property read.
