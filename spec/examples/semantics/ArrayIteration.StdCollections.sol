// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative library matrix: standard collection iteration delegates to each collection's cursor.
module spec.arrayiteration.stdcollections

func vectorItems(values: Vector<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func hashSetItems(values: HashSet<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func treeSetItems(values: TreeSet<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func hashMapEntries(values: HashMap<String, int>): int {
    var total = 0
    for val entry in values { total += entry.key.length + entry.value }
    total
}

func treeMapEntries(values: TreeMap<String, int>): int {
    var total = 0
    for val entry in values { total += entry.key.length + entry.value }
    total
}

func queueItems(values: Queue<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func dequeItems(values: Deque<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func stackItems(values: Stack<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func priorityQueueItems(values: PriorityQueue<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func spanItems(values: Span<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func readonlySpanItems(values: ReadOnlySpan<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func vectorIteratorIsExplicit(values: Vector<int>): VectorIterator<int> {
    values.iterator()
}

func hashMapIteratorYieldsEntryValues(values: HashMap<String, int>): () {
    for val entry in values { consume(entry.key, entry.value) }
}

func mapEntryIsNotTupleDestructuring(values: HashMap<String, int>): () {
    for val entry in values { consume(entry.key, entry.value) }
}

func setIterationDoesNotPromiseInsertionOrder(values: HashSet<int>): []int {
    collect(values)
}

func treeIterationUsesOrderingContract(values: TreeSet<int>): []int {
    collect(values)
}

func queueIterationDoesNotDequeue(values: Queue<int>): int {
    var count = 0
    for val item in values { count += 1 }
    count
}

func stackIterationDoesNotPop(values: Stack<int>): int {
    var count = 0
    for val item in values { count += 1 }
    count
}

func priorityIterationDoesNotDequeue(values: PriorityQueue<int>): int {
    var count = 0
    for val item in values { count += 1 }
    count
}

func spanIterationDoesNotOwnTheBuffer(values: Span<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

func collectionCopyToSnapshotIsIndependent(values: Vector<int>): int {
    val snapshot = values.toArray()
    var total = 0
    for val item in snapshot { total += item }
    total
}

func collectionCursorHasItsOwnLifetime(values: Vector<int>): int {
    val cursor = values.iterator()
    var total = 0
    for val item in cursor { total += item }
    total
}

func collectionCloseFollowsResolvedInterface(values: CloseableCollection<int>): () {
    for val item in values { consume(item) }
}

func collectionWithoutCloseHasNoSyntheticDispose(values: PlainCollection<int>): () {
    for val item in values { consume(item) }
}

func collectionTypeDoesNotGuaranteeNativeEligibility(values: Vector<int>): int {
    var total = 0
    for val item in values { total += item }
    total
}

// The standard library's public collection families expose different order, mutation, and cursor
// behavior. The shared syntax does not turn Queue iteration into dequeue, Stack iteration into pop,
// or PriorityQueue iteration into a sorted drain. Native support requires per-family qualification.
