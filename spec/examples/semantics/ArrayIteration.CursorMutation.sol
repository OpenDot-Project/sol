// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative future contract: collection structural mutation is defined by each iterator contract.
module spec.arrayiteration.cursormutation

class ConcurrentModificationException: RuntimeException {}

func vectorAppendInvalidatesCurrentTraversal(values: Vector<int>): () {
    for val item in values { values.add(item) }
}

func vectorRemoveInvalidatesCurrentTraversal(values: Vector<int>): () {
    for val item in values { values.remove(item) }
}

func vectorClearInvalidatesCurrentTraversal(values: Vector<int>): () {
    for val item in values { values.clear() }
}

func vectorReserveInvalidatesOnlyIfStorageMoves(values: Vector<int>): () {
    for val item in values { values.reserve(values.capacity + 10) }
}

func vectorElementReplaceIsNonStructural(values: Vector<int>): () {
    var index = 0
    for val item in values { values[index] = item + 1; index += 1 }
}

func vectorStructuralVersionCapturedAtIteratorCreation(values: Vector<int>): bool {
    val cursor = values.iterator()
    values.add(10)
    cursor.moveNext()
}

func vectorCursorChecksVersionBeforeAdvance(values: Vector<int>): bool {
    val cursor = values.iterator()
    values.clear()
    cursor.moveNext()
}

func vectorCursorChecksVersionBeforeCurrent(values: Vector<int>): int {
    val cursor = values.iterator()
    if cursor.moveNext() {
        values.add(10)
        cursor.current()
    } else { 0 }
}

func vectorMutationFailureUsesDeclaredType(values: Vector<int>): ()
    throws ConcurrentModificationException {
    for val item in values { values.add(item) }
}

func mutationFailureMayBeUnchecked(values: Vector<int>): () {
    // If the selected API declares an unchecked failure, callers do not add a checked effect.
    for val item in values { values.clear() }
}

func hashSetInsertCanChangeBucketStructure(values: HashSet<int>): () {
    for val item in values { values.add(item + 1) }
}

func hashSetDuplicateInsertMayBeNonStructural(values: HashSet<int>): () {
    for val item in values { values.add(item) }
}

func treeSetInsertionCanRebalance(values: TreeSet<int>): () {
    for val item in values { values.add(item + 1) }
}

func mapValueReplaceCanRemainNonStructural(values: HashMap<int, int>): () {
    for val entry in values { values[entry.key] = entry.value + 1 }
}

func mapNewKeyInsertIsStructural(values: HashMap<int, int>): () {
    for val entry in values { values[entry.key + 1] = entry.value }
}

func dequeEndMutationChangesOrder(values: Deque<int>): () {
    for val item in values { values.addLast(item) }
}

func queueEnqueueChangesFutureTraversal(values: Queue<int>): () {
    for val item in values { values.enqueue(item) }
}

func stackPushChangesFutureTraversal(values: Stack<int>): () {
    for val item in values { values.push(item) }
}

func priorityEnqueueCanChangeNextItem(values: PriorityQueue<int>): () {
    for val item in values { values.enqueue(item + 1) }
}

func currentItemCopySurvivesCollectionRemoval(values: Vector<String>): int {
    var total = 0
    for val item in values {
        values.removeAt(0)
        total += item.length
    }
    total
}

func collectionContractCanRequireSnapshot(values: Vector<int>): int {
    val snapshot = values.toArray()
    var total = 0
    for val item in snapshot { total += item }
    total
}

func arrayElementStoreDoesNotAdvanceCursor(values: []int): int {
    var index = 0
    var total = 0
    for val item in values { values[index] = item; index += 1; total += item }
    total
}

func unrelatedAliasMutationStillAffectsSameCollection(values: Vector<int>): () {
    val alias = values
    for val item in values { alias.add(item) }
}

func cursorMutationCheckPrecedesUserCurrentBody(values: Vector<int>): () {
    for val item in values {
        values.add(item)
        consume(item)
    }
}

// Fail-fast behavior is a property of a collection cursor, not a universal foreach rule. The
// selected collection contract defines which mutations increment its structural version and which
// cursor operation checks that version. Intrinsic arrays have fixed length and no such version.
