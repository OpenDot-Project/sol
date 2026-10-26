// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative future contract: iteration order is declared by each collection, not by foreach itself.
module spec.arrayiteration.collectionorder

func vectorUsesIndexOrder(values: Vector<int>): []int {
    collect(values)
}

func queueUsesDequeueOrder(values: Queue<int>): []int {
    collect(values)
}

func dequeUsesFrontToBackOrder(values: Deque<int>): []int {
    collect(values)
}

func stackUsesTopToBottomOrder(values: Stack<int>): []int {
    collect(values)
}

func treeSetUsesAscendingOrder(values: TreeSet<int>): []int {
    collect(values)
}

func treeMapUsesKeyOrder(values: TreeMap<int, String>): []int {
    var keys: []int = []
    for val entry in values { keys.add(entry.key) }
    keys
}

func hashSetOrderIsUnspecified(values: HashSet<int>): []int {
    // The result contains the observed iteration order, but callers cannot rely on a stable order.
    collect(values)
}

func hashMapOrderIsUnspecified(values: HashMap<int, String>): []int {
    var keys: []int = []
    for val entry in values { keys.add(entry.key) }
    keys
}

func priorityQueueIteratorIsHeapLayout(values: PriorityQueue<int>): []int {
    // A collection-specific iterator may expose heap layout rather than dequeue priority order.
    collect(values)
}

func priorityQueueDrainHasPriorityOrder(values: PriorityQueue<int>): []int {
    val clone = values.clone()
    var drained: []int = []
    for clone.length > 0 { drained.add(clone.dequeue()) }
    drained
}

func mapEntriesCopyKeysAndValues(values: HashMap<int, String>): int {
    var total = 0
    for val entry in values { total += entry.key + entry.value.length }
    total
}

func setItemsAreValues(values: TreeSet<String>): int {
    var total = 0
    for val item in values { total += item.length }
    total
}

func queueMutationChangesFutureOrder(values: Queue<int>): []int {
    var result: []int = []
    for val item in values {
        result.add(item)
        if item == 0 { values.enqueue(9) }
    }
    result
}

func dequeEndMutationIsCollectionSpecific(values: Deque<int>): []int {
    var result: []int = []
    for val item in values { result.add(item); values.addFirst(item) }
    result
}

func stackMutationIsCollectionSpecific(values: Stack<int>): []int {
    var result: []int = []
    for val item in values { result.add(item); values.push(item) }
    result
}

func treeMutationMayChangeSuccessor(values: TreeSet<int>): []int {
    var result: []int = []
    for val item in values { result.add(item); values.add(item + 1) }
    result
}

func hashResizeDoesNotPromiseOrder(values: HashSet<int>): []int {
    var result: []int = []
    for val item in values { result.add(item); values.add(item + 1000) }
    result
}

func explicitSortedSnapshotProvidesStableOrder(values: HashSet<int>): []int {
    val snapshot = values.toArray()
    snapshot.sort()
    snapshot
}

func arrayTraversalIsAlwaysAscendingIndexOrder(values: []int): []int {
    collect(values)
}

func arrayStoreDoesNotChangeOrdering(values: []int): []int {
    var index = 0
    for val item in values { values[index] = item + 1; index += 1 }
    collect(values)
}

func unorderedCollectionCannotBeUsedForDeterministicSerialization(values: HashMap<String, String>): String {
    var result = ""
    for val entry in values { result += entry.key + entry.value }
    result
}

func explicitOrderedSnapshotCanBeSerialized(values: HashMap<String, String>): String {
    val keys = values.keys().toArray()
    keys.sort()
    serializeInKeyOrder(values, keys)
}

func iterationOrderDoesNotComeFromMemoryAddress(values: HashSet<int>): []int {
    collect(values)
}

// `for-in` preserves the chosen collection protocol's order. It does not impose one universal order
// or infer a sorted order from type names, hash values, capacity, pointer layout, or current storage.
