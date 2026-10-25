// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative operator-selection fixture. The chosen operation contributes its own checked effects.
module spec.effects.operatorselection

class OrdinaryFailure: Exception {}
class CheckedFailure: Exception {}
class EqualityFailure: Exception {}

struct Amount {
    val value: int
    constructor(value: int) { this.value = value }

    companion object {
        operator func +(left: Amount, right: Amount): Amount throws OrdinaryFailure {
            Amount(left.value + right.value)
        }
        operator func checked +(left: Amount, right: Amount): Amount throws CheckedFailure {
            Amount(left.value + right.value)
        }
        operator func ==(left: Amount, right: Amount): bool throws EqualityFailure {
            left.value == right.value
        }
        operator func !=(left: Amount, right: Amount): bool throws EqualityFailure {
            !(left == right)
        }
    }
}

func addInCheckedContext(left: Amount, right: Amount): Amount throws CheckedFailure {
    left + right
}

func addOrdinaryContext(left: Amount, right: Amount): Amount throws OrdinaryFailure {
    left + right
}

func selectedCheckedBudgetMissing(left: Amount, right: Amount): Amount
    throws OrdinaryFailure {
    // Expected: when checked context selects the checked variant, the ordinary effect does not
    // satisfy the selected candidate's contract.
    left + right
}

func equalityEffect(left: Amount, right: Amount): bool throws EqualityFailure {
    left == right
}

func equalityEffectMissing(left: Amount, right: Amount): bool {
    left == right
}

func compoundUsesBinaryEffect(left: Amount, right: Amount): Amount throws OrdinaryFailure {
    var result = left
    result += right
    result
}

func nullableLiftKeepsEffect(left: Amount?, right: Amount?): Amount? throws CheckedFailure {
    left + right
}

func nullableNullPathDoesNotInvokeBody(left: Amount?, right: Amount?): Amount? throws CheckedFailure {
    // A null operand returns null without invoking the non-null overload body.
    left + right
}

func identityDoesNotHaveOperatorEffect(left: Amount, right: Amount): bool {
    left === right
}

// A checked context chooses the checked token variant when it is applicable. If it is absent,
// the ordinary candidate remains possible and its own effects are reported. Equality, identity,
// compound update, and nullable lifting each follow their separately declared operation rules.
