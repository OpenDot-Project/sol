// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative call-effect fixture. Defaults are evaluated at the selected call site.
module spec.effects.defaults

class DefaultFailure: Exception {}

func nextDefault(): int throws DefaultFailure { 7 }
func defaultArray(): []int throws DefaultFailure { [1, 2, 3] }
func cleanDefault(): String { "clean" }

func values(
    first: int = nextDefault(),
    label: String = cleanDefault(),
    vararg items: int = defaultArray(),
    enabled: bool = true
): int {
    items.length + first
}

func allDefaultsNeedEffectBudget(): int throws DefaultFailure {
    values()
}

func omittedFirstNeedsEffectBudget(): int throws DefaultFailure {
    values(label = "explicit", enabled = false)
}

func suppliedFirstSuppressesFirstEffect(): int {
    // Expected: nextDefault is not called for first.
    values(first = 1, label = "explicit", enabled = true)
}

func explicitVarargArraySuppressesArrayDefault(): int {
    // Expected: defaultArray is not called.
    values(first = 1, items = [], enabled = false)
}

func omittedVarargUsesDefaultEffect(): int throws DefaultFailure {
    values(first = 1, enabled = false)
}

func explicitEmptyArrayIsStillSupplied(): int {
    values(first = 1, items = [], enabled = false)
}

func missingBudgetForOmittedArgument(): int {
    // Expected: the actual mapping omits first and items, so their checked defaults escape.
    values(label = "only label", enabled = false)
}

func explicitArgumentsEvaluateInSourceOrder(): int throws DefaultFailure {
    // This call supplies values in declaration order except for the named final argument.
    // Evaluation remains in textual order, then no default for a filled parameter is run.
    values(first = nextDefault(), label = "value", items = [], enabled = true)
}

func skippedMiddleParameterUsesItsDefault(): int throws DefaultFailure {
    values(first = 1, enabled = true)
}

func laterDefaultMayNotReadEarlierParameterItself(): int throws DefaultFailure {
    values(first = 1, label = cleanDefault(), enabled = true)
}

func defaultCannotReadLaterParameter(): int {
    // Expected declaration error if first's default refers to enabled or items.
    values()
}

func publicParameterNamesAreContract(): int throws DefaultFailure {
    // Named call sites bind to exact selected parameter names, not source positions alone.
    values(label = "name", first = nextDefault(), enabled = true)
}

func defaultExpressionEvaluatesPerCall(): int throws DefaultFailure {
    val first = values()
    val second = values()
    first + second
}

func namedVarargRequiresArray(): int {
    // Expected: a named vararg accepts []int, not one int element.
    values(first = 0, items = 1)
}

func namedVarargAcceptsSpreadArray(items: []int): int {
    values(first = 0, items = *items)
}

// The effect set comes from the selected candidate and its actual argument mapping. An explicit
// argument suppresses only that formal's default. Supplying one named argument does not disable
// defaults for other missing formals.
