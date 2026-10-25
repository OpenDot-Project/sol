// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: effect collection follows actual single-pass evaluation order.
module spec.effects.argumentevaluationorder

class FirstFailure: Exception {}
class SecondFailure: Exception {}
class BodyFailure: Exception {}

func first(): int throws FirstFailure { 1 }
func second(): int throws SecondFailure { 2 }
func body(left: int, right: int): int throws BodyFailure { left + right }

func allThreeEffects(): int throws FirstFailure, SecondFailure, BodyFailure {
    body(first(), second())
}

func missingFirstEffect(): int throws SecondFailure, BodyFailure {
    // Expected: first() contributes FirstFailure before the second argument is evaluated.
    body(first(), second())
}

func firstArgumentCanStopLaterEvaluation(): int {
    try {
        body(first(), second())
    } catch (failure: FirstFailure) {
        return 0
    } catch (failure: SecondFailure) {
        return 1
    } catch (failure: BodyFailure) {
        return 2
    }
}

func calleeBodyRunsAfterArguments(): int throws FirstFailure, SecondFailure, BodyFailure {
    body(first(), second())
}

func receiverRunsBeforeArguments(): int throws FirstFailure, SecondFailure, BodyFailure {
    val service = makeService()
    service.combine(first(), second())
}

func makeService(): Service throws FirstFailure { Service() }

class Service {
    func combine(left: int, right: int): int throws BodyFailure { left + right }
}

func compoundLocationReadThenRightThenOperatorThenWrite(box: Box): ()
    throws FirstFailure, SecondFailure, BodyFailure {
    box.value += second()
}

class Box {
    var value: int
}

func propertyGet(): int throws FirstFailure { 1 }
func propertySet(value: int): () throws SecondFailure {}

class RemoteBox {
    var value: int {
        get throws FirstFailure = propertyGet()
        set throws SecondFailure { propertySet(value) }
    }
}

func delegatedCompoundOrder(box: RemoteBox): () throws FirstFailure, SecondFailure {
    box.value += 1
}

func setterBudgetMissing(box: RemoteBox): () throws FirstFailure {
    // Expected: a successful getter still reaches the setter and may throw SecondFailure.
    box.value += 1
}

func getterBudgetMissing(box: RemoteBox): () throws SecondFailure {
    // Expected: the getter effect occurs before operator and setter evaluation.
    box.value += 1
}

func effectfulShortCircuit(flag: bool): bool throws FirstFailure {
    flag && first() > 0
}

func rightBranchNotAlwaysInvoked(flag: bool): bool throws FirstFailure {
    // The effect is still part of the callable budget because the true path may evaluate it.
    flag || first() > 0
}

// A thrown earlier evaluation prevents later operations; it does not erase earlier side effects.
// Overload selection and defaults are resolved before the chosen call summary is accumulated.
// Implemented today: syntax tree only; ordering-aware effect binding is planned.
