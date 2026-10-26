// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative branch-correlation fixture. Correlation is not permission to invent path facts.
module spec.flow.correlatedbranches

class Entry {
    val number: int = 1
}

func bothValuesArePresent(left: Entry?, right: Entry?, flag: bool): int {
    if flag {
        if left == null {
            return 0
        }
        if right == null {
            return 1
        }
    } else {
        if left == null {
            return 2
        }
        if right == null {
            return 3
        }
    }
    // Expected: each surviving arm proves both same local identities non-null.
    left.number + right.number
}

func oneValueDiffersBetweenArms(left: Entry?, right: Entry?, flag: bool): int {
    if flag {
        if left == null {
            return 0
        }
        right?.number ?: 0
    } else {
        if right == null {
            return 1
        }
        left?.number ?: 0
    }
    // Expected: the common proof is not the union of facts seen on either arm.
    left.number + right.number
}

func conditionCorrelationDoesNotLeak(flag: bool, left: Entry?, right: Entry?): int {
    if flag && left != null && right != null {
        return left.number + right.number
    }
    // Expected: the true edge proves both. The false edge does not identify which check failed.
    0
}

func disjunctionDoesNotProveEitherValue(flag: bool, left: Entry?, right: Entry?): int {
    if flag || left != null || right != null {
        return 1
    }
    // Expected: the true result of the disjunction does not prove left or right individually.
    left.number + right.number
}

func shortCircuitAssignmentIsConditional(flag: bool): int {
    var assigned: int
    flag && assign(out assigned)
    // Expected: false skips the right operand.
    assigned
}

func assign(out value: int): bool {
    value = 1
    true
}

func equivalentFactsSurviveJoin(left: Entry?, right: Entry?, flag: bool): int {
    if flag {
        if left == null {
            return 0
        }
    } else {
        if left == null {
            return 1
        }
    }
    // Expected: both normal arms refine left, even though the right value is unrelated.
    left.number
}

func onlyOnePredecessorRefines(left: Entry?, flag: bool): int {
    if flag {
        if left == null {
            return 0
        }
    }
    // Expected: the false edge of flag does not establish non-nullness.
    left.number
}

func writeOneCorrelatedLocal(left: Entry?, right: Entry?, flag: bool): int {
    var first = left
    var second = right
    if flag {
        if first == null || second == null {
            return 0
        }
    } else {
        if first == null {
            return 0
        }
    }
    second = null
    // Expected: first can remain non-null; second's old fact is killed by assignment.
    first.number + second.number
}

// A sound implementation may track relational facts where the model supports them, but it
// cannot promote branch history to a fact after the paths have joined. It must preserve the
// exact local identities and evaluate short-circuit operands only on their real edges.
