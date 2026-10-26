// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: getter/indexer receiver effects occur once before iteration begins.
module spec.arrayiteration.accessoreffects

class AccessFailure: Exception {}

class SourceOwner {
    var reads: int
    var value: []int
    val values: []int
        get throws AccessFailure { reads += 1; value }
}

func getterEffectIncluded(owner: SourceOwner): int throws AccessFailure {
    var total = 0
    for val item in owner.values { total += item }
    total
}

func getterEffectMissing(owner: SourceOwner): int {
    // Expected checked-effect error from the single source getter call.
    for val item in owner.values { consume(item) }
    0
}

func getterRunsOnceEvenWhenEmpty(owner: SourceOwner): int throws AccessFailure {
    for val item in owner.values { consume(item) }
    owner.reads
}

func getterRunsBeforeFirstBody(owner: SourceOwner): int throws AccessFailure {
    var total = 0
    for val item in owner.values { total += owner.reads + item }
    total
}

func secondGetterReadIsExplicit(owner: SourceOwner): int throws AccessFailure {
    var total = 0
    for val item in owner.values { total += item; total += owner.values.length }
    total
}

func getterFailureHasNoItemAssignment(owner: SourceOwner): int throws AccessFailure {
    var item: int
    for val current in owner.values { item = current }
    // The handler at the source edge cannot read the body-only item assignment.
    item
}

class IndexedSources {
    var reads: int
    val rows: [][]int
    operator get(index: int): []int throws AccessFailure {
        reads += 1
        rows[index]
    }
}

func indexerSourceReceiverRunsOnce(source: IndexedSources, index: int): int
    throws AccessFailure {
    var total = 0
    for val item in source[index] { total += item }
    total
}

func indexerArgumentRunsBeforeBody(source: IndexedSources): int throws AccessFailure {
    var total = 0
    for val item in source[nextIndex()] { total += item }
    total
}

func indexerGetterResultRemainsActive(source: IndexedSources, replacement: []int): int
    throws AccessFailure {
    var total = 0
    for val item in source[0] {
        source.rows[0] = replacement
        total += item
    }
    total
}

func setterBodyMutationIsSeparate(source: IndexedSources, values: []int): ()
    throws AccessFailure {
    for val item in source[0] { source.rows[0] = values; consume(item) }
}

func sourceCallAndGetterOrder(source: IndexedSources): int throws AccessFailure {
    var total = 0
    for val item in selectSource(source)[nextIndex()] { total += item }
    total
}

func safeCallCannotBeUsedAsUnprovenSource(owner: NullableSourceOwner): int {
    // Expected nullable/type diagnostic unless the safe-call result is coalesced to a non-null array.
    for val item in owner?.values { consume(item) }
    0
}

func safeCallCoalescesBeforeIteration(owner: NullableSourceOwner): int {
    var total = 0
    for val item in owner?.values ?: [] { total += item }
    total
}

func accessorCheckedEffectDoesNotRepeatAtBackedge(owner: SourceOwner): int
    throws AccessFailure {
    var total = 0
    for val item in owner.values { total += item }
    total
}

func propertyBodyRefinementIsNotStable(owner: NullableSourceOwner): int {
    var total = 0
    for val item in owner.values ?: [] {
        if owner.values != null { total += owner.values.length }
    }
    total
}

func localSnapshotAvoidsRepeatGetter(owner: SourceOwner): int throws AccessFailure {
    val snapshot = owner.values
    var total = 0
    for val item in snapshot { total += item }
    total
}

class NullableSourceOwner { val values: []int? }

// Property/indexer evaluation uses the normal getter and argument order. The loop retains its result;
// an explicit later getter read is a separate operation with its own effect and source origin.
