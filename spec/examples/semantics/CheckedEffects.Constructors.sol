// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative initialization-effect fixture. Runtime lazy initialization is not demonstrated.
module spec.effects.constructors

class InitializationFailure: Exception {}
class ReadFailure: Exception {}

func loadValue(): int throws InitializationFailure { 1 }

class Resource() throws InitializationFailure {
    val value: int = loadValue()
}

func constructedWithBudget(): Resource throws InitializationFailure {
    Resource()
}

func constructedWithoutBudget(): Resource {
    // Expected: constructor effect is checked at this call site.
    Resource()
}

class InitializedField {
    val value: int = loadValue()
}

func fieldInitializerBudget(): InitializedField throws InitializationFailure {
    InitializedField()
}

object LazyConfiguration {
    val value: int = loadValue()
}

func firstOrLaterLazyAccess(): int throws InitializationFailure {
    // The static contract is enforced whether the backing initialization has already run or not.
    LazyConfiguration.value
}

open class BaseResource() throws InitializationFailure {}

class DerivedResource() throws InitializationFailure: BaseResource() {}

func inheritedConstructorChain(): DerivedResource throws InitializationFailure {
    DerivedResource()
}

func uncoveredBaseInitialization(): DerivedResource {
    // Expected: the derived construction surface cannot hide a checked base effect.
    DerivedResource()
}

class Reader {
    val value: int {
        get throws ReadFailure = readValue()
    }
}

func readValue(): int throws ReadFailure { 1 }

class UsesReader(reader: Reader) throws ReadFailure {
    val value: int = reader.value
}

func constructFromEffectfulGetter(reader: Reader): UsesReader throws ReadFailure {
    UsesReader(reader)
}

func partialConstructionDoesNotEscape(reader: Reader): UsesReader throws ReadFailure {
    // Required fields are assigned before publication; no partially initialized reference
    // becomes visible if the getter throws.
    UsesReader(reader)
}

func objectFailureIsNotConvertedToRuntimeFailure(): int throws InitializationFailure {
    LazyConfiguration.value
}

// Module, object, and companion initialization use an explicit Uninitialized/Initializing/
// Ready/Failed state contract. The first initializer failure is retained for subsequent access;
// repeated reads do not silently change a checked failure into a success.
