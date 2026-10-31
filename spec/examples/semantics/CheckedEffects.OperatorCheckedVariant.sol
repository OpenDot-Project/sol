// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative checked-operator fixture. Variant choice is part of operation binding.
module spec.effects.operatorcheckedvariant

class ArithmeticFailure: Exception {}
class DomainFailure: Exception {}

struct Distance {
    val raw: int
    constructor(raw: int) { this.raw = raw }

    companion object {
        operator func +(left: Distance, right: Distance): Distance throws DomainFailure {
            Distance(left.raw + right.raw)
        }
        operator func checked +(left: Distance, right: Distance): Distance throws ArithmeticFailure {
            Distance(left.raw + right.raw)
        }
        operator func -(left: Distance, right: Distance): Distance throws DomainFailure {
            Distance(left.raw - right.raw)
        }
    }
}

func ordinaryAddition(left: Distance, right: Distance): Distance throws DomainFailure {
    left + right
}

func checkedAddition(left: Distance, right: Distance): Distance throws ArithmeticFailure {
    left + right
}

func missingCheckedBudget(left: Distance, right: Distance): Distance throws DomainFailure {
    // Expected in a checked arithmetic context: the checked variant is selected and its effect
    // must be covered. An ordinary candidate's effects do not replace it.
    left + right
}

func checkedCompound(left: Distance, right: Distance): Distance throws ArithmeticFailure {
    var result = left
    result += right
    result
}

func nullableLiftedChecked(left: Distance?, right: Distance?): Distance? throws ArithmeticFailure {
    // If either value is null, lifting yields null and does not invoke the checked operator.
    // If both are non-null, the selected checked variant may throw.
    left + right
}

func operatorBodyStillHasBuiltinEffects(left, right: int): int {
    // A checked overload declaration is not proof that its body is mathematically safe.
    left + right
}

func arithmeticEffectIsNotSwallowed(left: Distance, right: Distance): Distance {
    left + right
}

func comparisonEffectsRemainOrdinary(left: Distance, right: Distance): bool throws DomainFailure {
    left < right
}

// Checked variants are accepted only for the closed arithmetic/bitwise token set. The checked
// candidate is preferred in the matching checked context; absent one, the ordinary operator is
// still applicable and its declared effects remain visible. Nullable lifting chooses the
// underlying non-null overload and propagates its effects only on the non-null execution path.
// Operators do not alter evaluation order or storage side effects.
