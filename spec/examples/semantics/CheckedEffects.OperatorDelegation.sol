// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative operator/delegation fixture. Exact overload and effect selection is semantic.
module spec.effects.operatordelegation

class OverflowFailure: Exception {}
class ReadFailure: Exception {}
class WriteFailure: Exception {}
class SetupFailure: Exception {}

class Quantity(val raw: int) {
    companion object {
        operator func +(left: Quantity, right: Quantity): Quantity throws OverflowFailure {
            Quantity(left.raw + right.raw)
        }
        operator func checked +(left: Quantity, right: Quantity): Quantity throws OverflowFailure {
            Quantity(left.raw + right.raw)
        }
        operator func -(left: Quantity, right: Quantity): Quantity throws OverflowFailure {
            Quantity(left.raw - right.raw)
        }
    }
}

func addQuantities(left: Quantity, right: Quantity): Quantity throws OverflowFailure {
    left + right
}

func compoundQuantity(left: Quantity, right: Quantity): Quantity throws OverflowFailure {
    var result = left
    result += right
    result
}

func compoundEffectCannotBeDropped(left: Quantity, right: Quantity): Quantity {
    var result = left
    // Expected: compound assignment reaches the selected binary operator.
    result += right
    result
}

func nullableQuantity(left: Quantity?, right: Quantity?): Quantity? throws OverflowFailure {
    left + right
}

func nullableOperatorDoesNotPassNullToBody(left: Quantity?, right: Quantity?): Quantity? throws OverflowFailure {
    // Lifting returns null if either value is null; the non-null operator body is not called
    // with a null operand.
    left + right
}

class RemoteDelegate {
    operator func getValue(owner: Object?, property: SolProperty<int>): int throws ReadFailure {
        readValue()
    }
    operator func setValue(owner: Object?, property: SolProperty<int>, value: int): () throws WriteFailure {
        writeValue(value)
    }
    operator func provideDelegate(owner: Object?, property: SolProperty<int>): RemoteDelegate throws SetupFailure {
        this
    }
}

func readValue(): int throws ReadFailure { 0 }
func writeValue(value: int): () throws WriteFailure {}

class Owner {
    var count: int by RemoteDelegate()
}

func delegateRead(owner: Owner): int throws ReadFailure {
    owner.count
}

func delegateWrite(owner: Owner): () throws WriteFailure {
    owner.count = 1
}

func delegateSetup(): Owner throws SetupFailure {
    Owner()
}

func delegateCompound(owner: Owner): () throws ReadFailure, WriteFailure {
    owner.count += 1
}

func delegateCompoundReadOnlyBudget(owner: Owner): () throws ReadFailure {
    // Expected: compound update also needs the setter's WriteFailure budget.
    owner.count += 1
}

func delegateCompoundWriteOnlyBudget(owner: Owner): () throws WriteFailure {
    // Expected: compound update also needs the getter's ReadFailure budget.
    owner.count += 1
}

// A selected checked operator is called as an ordinary resolved operation. Compound assignment
// has no independent overload: it evaluates the writable location once, reads it, invokes the
// binary operator, then writes the result. Delegation uses instance protocol methods and keeps
// initialization, read, write, and update effects distinct.
