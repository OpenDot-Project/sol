// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative accessor-effect fixture. Property reads and writes have independent contracts.
module spec.effects.accessors

class ReadFailure: Exception {}
class WriteFailure: Exception {}
class OperatorFailure: Exception {}

func readRemote(): int throws ReadFailure { 0 }
func writeRemote(value: int): () throws WriteFailure {}

class RemoteValue {
    var value: int {
        get throws ReadFailure = readRemote()
        set throws WriteFailure { writeRemote(value) }
    }
}

func readWithBudget(remote: RemoteValue): int throws ReadFailure {
    remote.value
}

func readWithoutBudget(remote: RemoteValue): int {
    // Expected: getter effect is uncovered.
    remote.value
}

func writeWithBudget(remote: RemoteValue): () throws WriteFailure {
    remote.value = 3
}

func writeWithoutBudget(remote: RemoteValue): () {
    // Expected: setter effect is uncovered.
    remote.value = 3
}

func compoundNeedsBothBudgets(remote: RemoteValue): () throws ReadFailure, WriteFailure {
    remote.value += 1
}

func compoundReadMissing(remote: RemoteValue): () throws WriteFailure {
    // Expected: the getter effect is missing even though the setter is declared.
    remote.value += 1
}

func compoundWriteMissing(remote: RemoteValue): () throws ReadFailure {
    // Expected: the setter effect is missing even though the getter is declared.
    remote.value += 1
}

func operatorMayAddThirdEffect(left, right: int): int throws OperatorFailure = left + right

func compoundIncludesOperator(remote: RemoteValue): () throws ReadFailure, WriteFailure, OperatorFailure {
    remote.value += operatorMayAddThirdEffect(1, 2)
}

class Delegate {
    operator func getValue(owner: Object?, property: SolProperty<int>): int throws ReadFailure = readRemote()
    operator func setValue(owner: Object?, property: SolProperty<int>, value: int): () throws WriteFailure {
        writeRemote(value)
    }
    operator func provideDelegate(owner: Object?, property: SolProperty<int>): Delegate throws OperatorFailure {
        this
    }
}

class DelegatedOwner {
    var count: int by Delegate()
}

func delegatedRead(owner: DelegatedOwner): int throws ReadFailure {
    owner.count
}

func delegatedWrite(owner: DelegatedOwner): () throws WriteFailure {
    owner.count = 1
}

func delegationInitializationEffect(): DelegatedOwner throws OperatorFailure {
    DelegatedOwner()
}

func delegationEffectsRemainSeparate(owner: DelegatedOwner): int throws ReadFailure {
    // An already initialized delegated read does not re-run provideDelegate.
    owner.count
}

// The SolProperty argument is declaration metadata. Each owner receives its own delegate
// storage as specified by the delegation contract; the descriptor is not the property's value.
// Receiver and index expressions are evaluated once. Compound assignment is read, operator,
// then write; exceptions do not roll back side effects already performed by earlier steps.
