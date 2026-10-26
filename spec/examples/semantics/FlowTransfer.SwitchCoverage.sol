// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative switch fixture. All case labels below are parsed independently of exhaustiveness.
module spec.flow.switchcoverage

enum Message {
    Stop,
    Move(x: int, y: int),
    Rename(value: String),
}

func exhaustiveEnum(message: Message): int {
    switch message {
        case Message.Stop:
            return 0
        case Message.Move(x, y):
            return x + y
        case Message.Rename(value):
            return value.length
    }
}

func missingEnumCase(message: Message): int {
    switch message {
        case Message.Stop:
            return 0
        case Message.Move(x, y):
            return x + y
        // Expected: unmatched Message.Rename path prevents exhaustive return proof.
    }
}

func boolCoverage(flag: bool): int {
    switch flag {
        case true:
            return 1
        case false:
            return 0
    }
}

func boolMissingArm(flag: bool): int {
    switch flag {
        case true:
            return 1
        // Expected: false remains an unmatched path.
    }
}

func openIntegerDomainNeedsDefault(code: int): int {
    switch code {
        case 10:
            return 1
        case 20:
            return 2
        default:
            return -1
    }
}

func openStringDomainNeedsDefault(name: String): int {
    switch name {
        case "ready":
            return 1
        case "closed":
            return 0
        default:
            return -1
    }
}

func scrutineeIsEvaluatedOnce(): int {
    switch nextCode() {
        case 1:
            return 10
        case 2:
            return 20
        default:
            return 0
    }
}

func nextCode(): int { 1 }

func caseScopesAreDisjoint(message: Message): int {
    switch message {
        case Message.Move(value, offset):
            val local = value + offset
            return local
        case Message.Rename(value):
            val local = value.length
            return local
        case Message.Stop:
            return 0
    }
}

func discardPayload(message: Message): int {
    switch message {
        case Message.Move(_, y):
            return y
        case Message.Rename(_):
            return 1
        case Message.Stop:
            return 0
    }
}

func duplicateLabelsAreInvalid(flag: bool): int {
    switch flag {
        case true:
            return 1
        case true:
            // Expected: duplicate label diagnostic; declaration order is not a tie breaker.
            return 2
        case false:
            return 0
    }
}

func defaultAfterExhaustiveBoolIsInvalid(flag: bool): int {
    switch flag {
        case true:
            return 1
        case false:
            return 0
        default:
            // Expected: the default has no possible input value.
            return -1
    }
}

func conditionalCaseUsesBodyIf(value: int): int {
    switch value {
        case 1:
            if nextCode() > 0 {
                return 2
            }
            return 3
        default:
            return 0
    }
}

// The language has no guarded case header. Branching belongs inside the case body.
// Arbitrary nested patterns and implicit fallthrough are not part of this contract.
// A failed exhaustiveness proof creates a real unmatched continuation for return and
// definite-assignment analysis; it is not repaired by selecting the first case.
