// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative invocation fixture. Effects are attached to executed calls, not function values.
module spec.effects.invocationbranches

class ActionFailure: Exception {}
class OtherFailure: Exception {}

func action(): int throws ActionFailure { 1 }
func otherAction(): int throws OtherFailure { 2 }
func predicate(): bool throws ActionFailure { true }

func invokeOnEitherArm(flag: bool): int throws ActionFailure, OtherFailure {
    if flag {
        action()
    } else {
        otherAction()
    }
}

func invokeOnOneArm(flag: bool): int throws ActionFailure {
    if flag {
        action()
    } else {
        0
    }
}

func missingOtherBudget(flag: bool): int throws ActionFailure {
    if flag {
        action()
    } else {
        // Expected: OtherFailure escapes from this branch.
        otherAction()
    }
}

func conditionalCallStillNeedsBudget(flag: bool): int {
    if flag {
        action()
    } else {
        0
    }
}

func shortCircuitCallStillNeedsBudget(flag: bool): bool throws ActionFailure {
    flag && predicate()
}

func shortCircuitFalseSkipsCall(flag: bool): bool {
    false && predicate()
}

func catchOnlyExecutedBranch(flag: bool): int {
    try {
        if flag {
            action()
        } else {
            otherAction()
        }
    } catch (failure: ActionFailure) {
        1
    } catch (failure: OtherFailure) {
        2
    }
}

func catchOneOfSeveralEffects(flag: bool): int throws OtherFailure {
    try {
        if flag {
            action()
        } else {
            otherAction()
        }
    } catch (failure: ActionFailure) {
        1
    }
}

func branchEffectsJoinOnlyAtMerge(flag: bool): int throws ActionFailure, OtherFailure {
    var result = 0
    if flag {
        result = action()
    } else {
        result = otherAction()
    }
    result
}

func noCallOnTerminatingBranch(flag: bool): int throws ActionFailure {
    if flag {
        action()
        return 1
    } else {
        return 0
    }
}

func bottomCallHasNoNormalResult(): ! throws ActionFailure {
    action()
    throw ActionFailure()
}

func callbackCreationHasNoInvocationEffect(): () -> int throws ActionFailure {
    { action() }
}

func callbackInvocationAddsEffect(callback: () -> int throws ActionFailure): int throws ActionFailure {
    callback()
}

func callbackInvocationAtCatch(callback: () -> int throws ActionFailure): int {
    try {
        callback()
    } catch (failure: ActionFailure) {
        0
    }
}

// An effect can be conditional in control flow and still require a caller budget if that path
// can escape. A catch removes only matching effects reaching it. Function-value construction
// does not run the body; invocation adds its declared effect at the invocation edge.
