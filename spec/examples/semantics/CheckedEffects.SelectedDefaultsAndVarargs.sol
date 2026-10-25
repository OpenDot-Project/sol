// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: only selected default expressions contribute call effects.
module spec.effects.selecteddefaultsandvarargs

class DefaultFailure: Exception {}
class BodyFailure: Exception {}

func defaultName(): String throws DefaultFailure { "fallback" }
func record(value: Object): () throws BodyFailure {}

func emit(value: String = defaultName()): () throws BodyFailure {
    record(value)
}

func explicitArgumentSkipsDefault(): () throws BodyFailure {
    emit("ready")
}

func omittedArgumentEvaluatesDefault(): () throws DefaultFailure, BodyFailure {
    emit()
}

func omittedDefaultCannotBeDropped(): () throws BodyFailure {
    // Expected: emit() selects defaultName and may throw DefaultFailure.
    emit()
}

func explicitNamedArgumentSkipsDefault(): () throws BodyFailure {
    emit(value = "complete")
}

func defaultEffectsRunAtCallTime(): () throws DefaultFailure, BodyFailure {
    val action: () -> () throws BodyFailure = { emit("fixed") }
    // Creating the closure did not evaluate a default. Invoking the lambda calls emit explicitly.
    action()
}

func createDeferredDefaultCall(): () -> () throws DefaultFailure, BodyFailure {
    // The body contains an omitted argument; its default effect belongs to callback invocation.
    { emit() }
}

func callbackEffectIncludesSelectedDefault(
    action: () -> () throws DefaultFailure, BodyFailure
): () throws DefaultFailure, BodyFailure {
    action()
}

func selectedDefaultIsCaughtAtInvocation(): () {
    try {
        emit()
    } catch (failure: DefaultFailure) {
        record("default failed")
    } catch (failure: BodyFailure) {
        record("body failed")
    }
}

func makeValue(): String throws DefaultFailure { "item" }

func orderedArgumentsBeforeDefault(): () throws DefaultFailure, BodyFailure {
    emit(makeValue())
}

func varargElementEffectsArePerArgument(): () throws DefaultFailure, BodyFailure {
    record(makeValue())
    record(makeValue())
}

func oneCoveredArgumentCannotCoverTheOther(): () throws DefaultFailure {
    record(makeValue())
    record("safe")
}

func explicitSpreadDoesNotEvaluateDefaults(): () throws BodyFailure {
    val items: []Object = ["first", "second"]
    record(values = *items)
}

func repeatDefaultCallsRepeatEffects(): () throws DefaultFailure, BodyFailure {
    emit()
    emit()
}

// Defaults are selected per call, evaluated at invocation, and sequenced after explicit
// arguments according to declaration order. A vararg spread supplies values; it does not select
// a missing default for that vararg. Implemented today: parser only; selected-call summaries are
// planned binder/effect-analysis behavior.
