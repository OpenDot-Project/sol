// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative nullable-flow fixture. Source parsing does not prove stable-storage facts.
module spec.flow.nullablefacts

open class Animal {
    val nameLength: int = 1
}

class Cat: Animal {
    val lives: int = 9
}

class Store {
    var current: Animal? = null
}

func earlyExitProvesNonNull(value: Animal?): int {
    if value == null {
        return 0
    }
    value.nameLength
}

func branchReturnsPreserveJoin(value: Animal?, flag: bool): int {
    if flag {
        if value == null {
            return 0
        }
    } else {
        if value == null {
            return 1
        }
    }
    // Both normal edges establish the same non-null fact.
    value.nameLength
}

func equalityOnRightOperand(value: Animal?): bool {
    value != null && value.nameLength > 0
}

func negatedNullTest(value: Animal?): int {
    if !(value != null) {
        return 0
    }
    value.nameLength
}

func typeTestInPositiveArm(value: Animal): int {
    if value is Cat {
        return value.lives
    }
    0
}

func typeTestInNegativeArm(value: Animal): int {
    if value !is Cat {
        return 0
    }
    value.lives
}

func writeInvalidatesNullProof(value: Animal?): int {
    var local = value
    if local == null {
        return 0
    }
    local = null
    // Expected: nullable receiver diagnostic.
    local.nameLength
}

func refCallInvalidatesNullProof(value: Animal?): int {
    var local = value
    if local == null {
        return 0
    }
    replace(ref local)
    // Expected: ref call may replace mutable storage; old fact cannot be reused.
    local.nameLength
}

func replace(ref value: Animal?): () {
    value = null
}

func outCallDoesNotProveNonNull(value: Animal?): int {
    var local: Animal?
    write(out local, value)
    // Expected: the declared out type remains nullable.
    local.nameLength
}

func write(out target: Animal?, source: Animal?): () {
    target = source
}

func safeCallDoesNotAssignOnSkippedPath(value: Animal?): int {
    var result: int
    value?.writeOut(out result)
    // Expected: if value is null, writeOut is skipped and result remains unassigned.
    result
}

class Writer {
    func writeOut(out result: int): () {
        result = 1
    }
}

func propertyIsNotAStableLocal(store: Store): int {
    if store.current == null {
        return 0
    }
    // Expected: second property access may observe a different value.
    store.current.nameLength
}

func explicitlySnapshotProperty(store: Store): int {
    val snapshot = store.current
    if snapshot == null {
        return 0
    }
    // Positive: snapshot is a local with stable val storage.
    snapshot.nameLength
}

func safeAccessResultIsLifted(value: Animal?): int {
    val length = value?.nameLength
    if length == null {
        return 0
    }
    length
}

func assertionDoesNotMutateStorage(value: Animal?): int {
    val length = value!!.nameLength
    length
}

// A refinement affects only a local's expression view. It does not alter the declared type,
// public signature, generic inference evidence, object identity, or the values in another alias.
// Dynamic property reads and mutable captured locals are not stable proofs.
