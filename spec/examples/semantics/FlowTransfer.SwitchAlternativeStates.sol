// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: switch labels refine the scrutinee on isolated case edges.
module spec.flow.switchalternativestates

enum Input {
    Empty,
    Numeric(value: int),
    Named(value: String),
}

func joinCaseValues(input: Input): int {
    switch input {
        case Input.Empty:
            return 0
        case Input.Numeric(value):
            return value
        case Input.Named(value):
            return value.length
    }
}

func assignmentInEveryExhaustiveArm(input: Input): int {
    var result: int
    switch input {
        case Input.Empty:
            result = 0
        case Input.Numeric(value):
            result = value
        case Input.Named(value):
            result = value.length
    }
    // Expected: exhaustive normal completion assigns result on every matching arm.
    result
}

func omittedArmLeavesAnUnmatchedPath(input: Input): int {
    var result: int
    switch input {
        case Input.Empty:
            result = 0
        case Input.Numeric(value):
            result = value
    }
    // Expected: Named is unmatched, so the switch has a normal exit with result unassigned.
    result
}

func returningArmDoesNotJoinNormalState(input: Input): int {
    var result: int
    switch input {
        case Input.Empty:
            return 0
        case Input.Numeric(value):
            result = value
        case Input.Named(value):
            result = value.length
    }
    // Expected: both normal arms assign result; the returning arm has no normal successor.
    result
}

func caseScopesDoNotShareBindings(input: Input): int {
    switch input {
        case Input.Numeric(value):
            val local = value + 1
            return local
        case Input.Named(value):
            val local = value.length
            return local
        case Input.Empty:
            return 0
    }
}

func booleanSwitchHasTwoAlternatives(flag: bool): int {
    switch flag {
        case true:
            return 1
        case false:
            return 0
    }
}

func anUnreachableCaseDoesNotRepairAssignment(flag: bool): int {
    var result: int
    switch flag {
        case true:
            result = 1
        case false:
            return 0
        case true:
            // Expected: duplicate label is diagnosed; it is not an additional proof path.
            result = 2
    }
    result
}

func expressionInCaseMayThrow(input: Input): int throws ReadFailure {
    switch input {
        case Input.Empty:
            return 0
        case Input.Numeric(value):
            return value
        case Input.Named(value):
            return parse(value)
    }
}

class ReadFailure: Exception {}

func parse(value: String): int throws ReadFailure { 0 }

func catchOnlyOneCaseEffect(input: Input): int throws ReadFailure {
    try {
        switch input {
            case Input.Empty:
                return 0
            case Input.Numeric(value):
                return value
            case Input.Named(value):
                return parse(value)
        }
    } catch (failure: ReadFailure) {
        return -1
    }
}

// Labels are alternatives, not sequential fallthrough. Only reachable normal case completions
// join after the switch. Implemented today: syntax parsing. Planned: exhaustiveness, duplicate
// label checks, scoped payload binding, and exception edges are part of binding/flow analysis.
