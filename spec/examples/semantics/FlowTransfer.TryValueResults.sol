// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative try-result fixture. Result typing uses only normal completion paths.
module spec.flow.tryvalueresults

open class ResultBase {
    val code: int = 0
}

class LeftResult: ResultBase {
    val left: int = 1
}

class RightResult: ResultBase {
    val right: int = 2
}

class Failure: RuntimeException {}

func sameTypeBranches(flag: bool): int {
    try {
        if flag {
            1
        } else {
            2
        }
    } finally {
        record()
    }
}

func subtypeBranches(flag: bool): ResultBase {
    try {
        if flag {
            LeftResult()
        } else {
            RightResult()
        }
    } finally {
        record()
    }
}

func incompatibleBranches(flag: bool): int {
    try {
        if flag {
            1
        } else {
            "text"
        }
    } finally {
        record()
    }
}

func throwingArmHasNoResult(flag: bool): int {
    try {
        if flag {
            throw Failure()
        }
        5
    } catch (failure: Failure) {
        10
    }
}

func catchResultJoinsBodyResult(flag: bool): int {
    try {
        if flag {
            1
        } else {
            throw Failure()
        }
    } catch (failure: Failure) {
        2
    }
}

func allResultArmsBottom(flag: bool): ! {
    try {
        if flag {
            throw Failure()
        }
        throw Failure()
    } catch (failure: Failure) {
        throw Failure()
    } finally {
        record()
    }
}

func finallyExpressionDoesNotChangeValue(flag: bool): int {
    try {
        if flag {
            return 1
        }
        2
    } finally {
        // The finally tail expression is evaluated and discarded.
        record()
    }
}

func finallyValueIsNotAlternative(): int {
    try {
        1
    } finally {
        // Expected: finally's value is not joined with the try body's int result.
        "not an int"
    }
}

func tryStatementDoesNotYield(): int {
    try {
        record()
    } finally {
        record()
    }
    // Expected: statement-form try does not create an expression result.
}

func blockTailAfterTry(): int {
    try {
        record()
    } finally {
        record()
    }
    3
}

func bottomResultMaySatisfyAnyExpectedType(): String {
    try {
        throw Failure()
    } catch (failure: Failure) {
        throw Failure()
    }
}

func record(): () {}

// Try value joins the result types of reachable normally completing body and handler paths.
// A bottom path is omitted; an all-bottom try has type !. Finally is a cleanup transfer and
// cannot contribute a value. Statement-form try remains a statement even when its body ends in
// an expression.
