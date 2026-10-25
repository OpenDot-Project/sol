// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative pattern-scope fixture. Payload names are assigned to one switch case only.
module spec.flow.patternscopes

enum Outcome {
    Empty,
    Value(number: int),
    Text(value: String),
}

open class Animal {
    val age: int = 1
}

class Dog: Animal {
    val bark: int = 2
}

func payloadAssignedInCase(outcome: Outcome): int {
    switch outcome {
        case Outcome.Value(number):
            return number
        case Outcome.Text(value):
            return value.length
        case Outcome.Empty:
            return 0
    }
}

func payloadNameDoesNotEscape(outcome: Outcome): int {
    switch outcome {
        case Outcome.Value(number):
            return number
        case Outcome.Text(value):
            return value.length
        case Outcome.Empty:
            // Expected: number is not in this case's lexical scope.
            return number
    }
}

func repeatedNameInSeparateCasesIsLocal(outcome: Outcome): int {
    switch outcome {
        case Outcome.Value(value):
            return value
        case Outcome.Text(value):
            return value.length
        case Outcome.Empty:
            return 0
    }
}

func duplicateNameInsideOnePattern(outcome: Outcome): int {
    switch outcome {
        case Outcome.Value(value):
            val value = 1
            // Expected: a case binding cannot be redeclared in its own case scope.
            return value
        case Outcome.Text(text):
            return text.length
        case Outcome.Empty:
            return 0
    }
}

func typeTestRefinesInsideCase(animal: Animal): int {
    switch animal {
        case is Dog:
            // Type pattern selects a branch; explicit binding syntax is not implied.
            return 1
        default:
            return animal.age
    }
}

func unmatchedCaseBlocksReturnProof(outcome: Outcome): int {
    switch outcome {
        case Outcome.Value(number):
            return number
        case Outcome.Empty:
            return 0
        // Expected: Text remains an unmatched normal path.
    }
}

func scrutineeAssignmentOccursOnce(): int {
    switch nextOutcome() {
        case Outcome.Value(number):
            return number
        case Outcome.Text(value):
            return value.length
        case Outcome.Empty:
            return 0
    }
}

func nextOutcome(): Outcome { Outcome.Empty }

func defaultSubsumedByClosedCases(outcome: Outcome): int {
    switch outcome {
        case Outcome.Value(number):
            return number
        case Outcome.Text(value):
            return value.length
        case Outcome.Empty:
            return 0
        default:
            // Expected: all Outcome variants are already covered.
            return -1
    }
}

// Pattern variables have source origins and resolved payload types. Their lifetime ends at
// the case boundary. Exhaustiveness contributes no unmatched predecessor only after the full
// closed domain has been verified.
