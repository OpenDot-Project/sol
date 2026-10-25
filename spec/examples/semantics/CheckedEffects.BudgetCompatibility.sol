// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative function-effect compatibility matrix, including nested function positions.
module spec.effects.budgetcompatibility

class ReadFailure: Exception {}
class FileFailure: ReadFailure {}
class WriteFailure: Exception {}

func pure(): int { 1 }
func read(): int throws ReadFailure { 2 }
func readFile(): int throws FileFailure { 3 }
func write(): int throws WriteFailure { 4 }

func acceptPure(action: () -> int): int {
    action()
}

func acceptRead(action: () -> int throws ReadFailure): int throws ReadFailure {
    action()
}

func acceptException(action: () -> int throws Exception): int throws Exception {
    action()
}

func pureFitsPure(): int {
    acceptPure({ pure() })
}

func pureFitsReadBudget(): int throws ReadFailure {
    acceptRead({ pure() })
}

func narrowFitsBroadBudget(): int throws Exception {
    acceptException({ readFile() })
}

func checkedCannotFitPure(action: () -> int throws ReadFailure): int {
    // Expected: checked effect cannot be erased from the callback type.
    acceptPure(action)
}

func siblingCannotFitReadBudget(action: () -> int throws WriteFailure): int
    throws ReadFailure {
    // Expected: WriteFailure is not covered by ReadFailure.
    acceptRead(action)
}

func broadCannotFitNarrowBudget(action: () -> int throws Exception): int
    throws ReadFailure {
    // Expected: Exception includes types outside the ReadFailure subtree.
    acceptRead(action)
}

func nestedProducerEffects(): () -> int throws ReadFailure {
    { read() }
}

func nestedProducerFitsBroadReturn(): () -> int throws Exception {
    nestedProducerEffects()
}

func nestedProducerCannotNarrowReturn(): () -> int throws ReadFailure {
    broadProducer()
}

func broadProducer(): () -> int throws Exception {
    { write() }
}

func independentEffectsAreNotUnionedIntoOneSubtype(
    first: () -> int throws ReadFailure,
    second: () -> int throws WriteFailure
): int throws Exception {
    first() + second()
}

func allEffectsNeedCoverage(
    first: () -> int throws ReadFailure,
    second: () -> int throws WriteFailure
): int throws ReadFailure {
    // Expected: WriteFailure remains uncovered even though ReadFailure is listed.
    first() + second()
}

// Conversion is directional: a pure function may be used where a wider checked budget is
// allowed. A wider budget cannot be narrowed. Nested function types preserve their own budgets
// independently of effects on the outer function that returns or accepts them.
