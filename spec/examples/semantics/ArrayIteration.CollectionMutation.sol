// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: collection iterator invalidation differs from fixed-array traversal.
module spec.arrayiteration.collectionmutation

func vectorReadOnlyTraversal(values: Vector<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func vectorIteratorIsIndependent(values: Vector<int>): int {
    val first = values.iterator()
    val second = values.iterator()
    var count = 0
    for val item in first { count += item }
    for val item in second { count += item }
    count
}

func vectorStructuralWriteInvalidatesIterator(values: Vector<int>): () {
    for val item in values {
        values.add(item)
    }
}

func vectorNonStructuralElementReplace(values: Vector<int>): () {
    var index = 0
    for val item in values {
        values[index] = item + 1
        index += 1
    }
}

func vectorRemoveDuringIteration(values: Vector<int>): () {
    for val item in values { values.remove(item) }
}

func vectorClearDuringIteration(values: Vector<int>): () {
    for val item in values { values.clear() }
}

func vectorReserveDuringIteration(values: Vector<int>): () {
    for val item in values { values.reserve(values.capacity + 1) }
}

func vectorTrimDuringIteration(values: Vector<int>): () {
    for val item in values { values.trimToFit() }
}

func vectorIteratorFailFastAfterStructuralChange(values: Vector<int>): bool {
    val iterator = values.iterator()
    iterator.moveNext()
    values.add(99)
    // The next operation observes the documented invalidation failure.
    iterator.moveNext()
}

func vectorIteratorPreservesPositionAfterValueReplace(values: Vector<int>): int {
    val iterator = values.iterator()
    if iterator.moveNext() {
        values[0] = 9
        iterator.current()
    } else { 0 }
}

func dequeInsertionInvalidatesTraversal(values: Deque<int>): () {
    for val item in values { values.addFirst(item) }
}

func queueEnqueueDuringTraversal(values: Queue<int>): () {
    for val item in values { values.enqueue(item) }
}

func stackPushDuringTraversal(values: Stack<int>): () {
    for val item in values { values.push(item) }
}

func hashSetInsertDuringTraversal(values: HashSet<int>): () {
    for val item in values { values.add(item) }
}

func treeSetRemoveDuringTraversal(values: TreeSet<int>): () {
    for val item in values { values.remove(item) }
}

func hashMapInsertDuringTraversal(values: HashMap<int, int>): () {
    for val entry in values { values[entry.key + 1] = entry.value }
}

func treeMapRemoveDuringTraversal(values: TreeMap<int, int>): () {
    for val entry in values { values.remove(entry.key) }
}

func priorityQueueEnqueueDuringTraversal(values: PriorityQueue<int>): () {
    for val item in values { values.enqueue(item + 1) }
}

func spanIsNotGrowableCollection(values: Span<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func readOnlySpanRejectsWrite(values: ReadOnlySpan<int>): () {
    for val item in values {
        // Expected error: the loop item is a value, and the source view is read-only.
        item = 0
    }
}

func spanMutationDoesNotResizeOrigin(values: Span<int>): () {
    var index = 0
    for val item in values {
        values[index] = item + 1
        index += 1
    }
}

func structuralMutationExceptionIsChecked(values: Vector<int>): () throws ConcurrentModificationException {
    for val item in values { values.add(item) }
}

func uncheckedVersionMismatchStillTerminates(values: Vector<int>): () {
    // If a concrete API chooses unchecked fail-fast failure, no checked effect is added, but the
    // structural invalidation result remains deterministic.
    for val item in values { values.clear() }
}

func arrayElementStoreDoesNotInvalidateArrayTraversal(values: []int): () {
    var index = 0
    for val item in values {
        values[index] = item + 1
        index += 1
    }
}

func arrayHasNoStructuralVersionCounter(values: []int): int {
    var count = 0
    for val item in values { count += 1 }
    count
}

class ConcurrentModificationException: RuntimeException {}

// A collection's iterator contract owns structural-mutation detection. Intrinsic array traversal
// has a saved reference and immutable length and does not inherit Vector's fail-fast behavior.
