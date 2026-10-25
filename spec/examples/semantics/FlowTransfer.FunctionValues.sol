// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative callable-result fixture. Tail values and non-local control transfers are distinct.
module spec.flow.functionvalues

class Failure: RuntimeException {}

func namedLambdaJoinsResults(flag: bool): (bool) -> int {
    answer@ { value: bool ->
        if value {
            return@answer 1
        }
        2
    }
}

func labelledLambdaResult(): () -> int {
    answer@ { ->
        return@answer 3
    }
}

func positionalLambdaTail(): (int, int) -> int {
    { $0 + $1 }
}

func headerlessLambdaValue(): () -> int {
    // Constructing the lambda stores its function value; it does not execute this body.
    { 42 }
}

func lambdaAssignmentNeedsAllArms(flag: bool): () -> int {
    var result: int
    val assign = { result = 1 }
    if flag {
        assign()
    }
    // This lambda body is parseable but its capture does not make result definitely assigned
    // on the path where assign was not invoked.
    { result }
}

func allNormalArmsHaveValue(flag: bool): int {
    if flag {
        return 1
    } else {
        return 2
    }
}

func oneNormalArmMissingValue(flag: bool): int {
    if flag {
        return 1
    } else {
        throw Failure()
    }
}

func allPathsBottom(flag: bool): ! {
    if flag {
        throw Failure()
    } else {
        for {}
    }
}

func tryValueWithThrowingArm(flag: bool): int {
    try {
        if flag {
            throw Failure()
        }
        10
    } catch (failure: Failure) {
        20
    } finally {
        record()
    }
}

func tryValueWithAllBottom(): ! {
    try {
        throw Failure()
    } catch (failure: Failure) {
        throw Failure()
    }
}

func emptyTailIsUnit(): () {
    val work = { record() }
    work()
}

func statementTailDoesNotYield(): int {
    val value = 1
    // Expected: a declaration is not an int-valued tail result.
}

func bareReturnDoesNotExitOuter(): int {
    val callback = { -> return 1 }
    0
}

func explicitLabelReturnsFromLambda(): int {
    val callback = answer@ { -> return@answer 4 }
    callback()
}

func record(): () {}

// Lambda parameters and return scope belong to the lambda itself. A bare return cannot
// transfer to an enclosing function. The lambda's final expression determines its result;
// declaration, assignment, and loop statements do not produce result values.
