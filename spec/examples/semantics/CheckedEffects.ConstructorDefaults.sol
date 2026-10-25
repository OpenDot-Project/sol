// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative constructor/default fixture. Construction combines argument, default and body effects.
module spec.effects.constructordefaults

class ArgumentFailure: Exception {}
class DefaultFailure: Exception {}
class BaseFailure: Exception {}
class BodyFailure: Exception {}

func argument(): int throws ArgumentFailure { 1 }
func defaultValue(): int throws DefaultFailure { 2 }
func initialize(): int throws BodyFailure { 3 }

open class Base(value: int = baseDefault()) throws BaseFailure {}
func baseDefault(): int throws DefaultFailure { 4 }

class Child(
    value: int = defaultValue()
) throws BodyFailure: Base(value) {
    val result: int = initialize()
}

func allConstructionEffects(): Child throws DefaultFailure, BaseFailure, BodyFailure {
    Child()
}

func suppliedDefaultSuppressesOneEffect(): Child throws BaseFailure, BodyFailure {
    Child(value = 1)
}

func argumentEffectsAreAlsoChecked(): Child throws ArgumentFailure, BaseFailure, BodyFailure {
    Child(value = argument())
}

func missingDefaultBudget(): Child throws BaseFailure, BodyFailure {
    // Expected: Child's omitted defaultValue contributes DefaultFailure.
    Child()
}

func missingBaseEffectBudget(): Child throws DefaultFailure, BodyFailure {
    // Expected: the base constructor/default effect remains part of construction.
    Child(value = 1)
}

func missingFieldInitializerBudget(): Child throws DefaultFailure, BaseFailure {
    // Expected: initialize contributes BodyFailure.
    Child()
}

func catchConstructionAtCallSite(): Child throws BaseFailure, BodyFailure {
    try {
        Child()
    } catch (failure: DefaultFailure) {
        Child(value = 0)
    }
}

func constructorDefaultIsEvaluatedAtCall(): Child throws DefaultFailure, BaseFailure, BodyFailure {
    val first = Child()
    val second = Child()
    first
}

func explicitArgumentDoesNotRunDefault(): Child throws BaseFailure, BodyFailure {
    Child(value = 7)
}

// A derived constructor cannot remove a checked effect from its base initializer. A selected
// constructor call contributes its explicit argument effects, only the omitted defaults,
// required base construction, field/property initializers, init blocks, and constructor body.
// Each operation is evaluated once in the specified order; failure prevents object publication.
