// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative indexer-effect fixture. Accessor selection follows the operation being performed.
module spec.effects.indexers

class IndexFailure: Exception {}
class StoreFailure: Exception {}
class OperatorFailure: Exception {}

func readAt(index: int): int throws IndexFailure { index }
func writeAt(index: int, value: int): () throws StoreFailure {}

class Table {
    this[index: int]: int {
        get throws IndexFailure = readAt(index)
        set throws StoreFailure { writeAt(index, value) }
    }
}

func indexedRead(table: Table): int throws IndexFailure {
    table[0]
}

func indexedWrite(table: Table): () throws StoreFailure {
    table[0] = 1
}

func indexedCompound(table: Table): () throws IndexFailure, StoreFailure {
    table[0] += 1
}

func indexedReadMissingBudget(table: Table): int {
    // Expected: getter effect is checked.
    table[0]
}

func indexedWriteMissingBudget(table: Table): () {
    // Expected: setter effect is checked.
    table[0] = 1
}

func indexedCompoundMissingRead(table: Table): () throws StoreFailure {
    table[0] += 1
}

func indexedCompoundMissingWrite(table: Table): () throws IndexFailure {
    table[0] += 1
}

func receiverAndIndexEvaluatedOnce(): () throws IndexFailure, StoreFailure {
    getTable()[nextIndex()] += 1
}

func getTable(): Table { Table() }
func nextIndex(): int throws IndexFailure { 0 }

class MutableIndex {
    var value: int {
        get throws IndexFailure = readAt(0)
        set throws StoreFailure { writeAt(0, value) }
    }
}

func propertyCompoundIncludesIndexerLikeEffects(index: MutableIndex): () throws IndexFailure, StoreFailure {
    index.value += 1
}

func operatorEffectJoinsWithAccessor(table: Table): () throws IndexFailure, StoreFailure, OperatorFailure {
    table[0] += combine(1, 2)
}

func combine(left, right: int): int throws OperatorFailure = left + right

// An indexer uses independently selected getter and setter contracts. Compound assignment
// evaluates receiver, indices, read, right side, operator, and write in order. Each receiver
// and index expression is evaluated once even when an earlier step throws; completed side
// effects are not rolled back.
