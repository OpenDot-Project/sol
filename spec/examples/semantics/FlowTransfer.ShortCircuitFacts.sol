// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: short-circuit edges carry only stable predicate facts.
module spec.flow.shortcircuitfacts

class Entry(val code: int) {}

func andGuardRefinesRight(entry: Entry?): int {
    if entry != null && entry.code > 0 {
        // Expected: the right operand is reached only after the non-null test succeeds.
        return entry.code
    }
    0
}

func orGuardRefinesAfterEarlyExit(entry: Entry?): int {
    if entry == null || entry.code == 0 {
        return 0
    }
    // Expected: continuation requires entry != null and entry.code != 0.
    entry.code
}

func conjunctionFalseDoesNotChooseOneCause(left: Entry?, right: Entry?): int {
    if left != null && right != null {
        return left.code + right.code
    }
    // Expected: the false edge does not prove which operand was null.
    0
}

func disjunctionTrueDoesNotChooseOneValue(left: Entry?, right: Entry?): int {
    if left != null || right != null {
        // Expected: at least one is non-null, but neither individual local is known non-null.
        return left.code
    }
    0
}

func negatedDisjunctionProvesBothNull(left: Entry?, right: Entry?): int {
    if !(left != null || right != null) {
        // Expected: stable reads and the false disjunction edge imply both references are null.
        return 0
    }
    1
}

func shortCircuitSkipsThrowingCall(flag: bool): bool {
    // Expected: mayFail is reachable on one branch and its checked effect is uncovered.
    flag && mayFail()
}

func shortCircuitEffectStillNeedsBudget(flag: bool): bool throws ReadFailure {
    flag && mayFail()
}

func shortCircuitCallNotOnEveryPath(flag: bool): bool throws ReadFailure {
    // The call is conditional, but it is reachable and its effect can escape on the true path.
    flag || mayFail()
}

func mayFail(): bool throws ReadFailure { true }
class ReadFailure: Exception {}

func mutableLocalWriteInvalidatesRightOperandFact(entry: Entry?): int {
    var current = entry
    if current != null && clear(ref current) {
        // Expected: clear can write null after the left test and before this body read.
        return current.code
    }
    0
}

func clear(ref entry: Entry?): bool {
    entry = null
    true
}

func separateTestFromEffectfulRead(entry: Entry?): int {
    if entry == null {
        return 0
    }
    mutateGlobalState()
    // Expected: a call invalidates only facts for storage it can reach; local alias identity
    // may remain stable while mutable heap properties require a fresh read.
    entry.code
}

func mutateGlobalState(): () {}

func stableLocalSurvivesIndependentCall(entry: Entry?): int {
    val saved = entry
    if saved == null {
        return 0
    }
    mutateGlobalState()
    // Expected: immutable local storage cannot be rebound by a call that does not alias it.
    saved.code
}

func evaluationCountIsOne(entry: Entry?): int {
    if nextEntry(entry) != null && nextEntry(entry)!!.code > 0 {
        // Expected: each explicit call expression is a distinct evaluation; the first result
        // does not refine the second call's return value. The non-null assertion is explicit.
        return nextEntry(entry)!!.code
    }
    0
}

func nextEntry(entry: Entry?): Entry? { entry }

// Predicate provenance records stable local tests and invalidating writes. Boolean algebra may
// preserve relational facts only for pure completed operands. Implemented today: source syntax;
// planned: path-sensitive condition evaluation and fact invalidation.
