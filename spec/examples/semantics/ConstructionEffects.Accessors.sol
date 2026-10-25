// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: selected property operations contribute initialization effects.
module spec.construction.accessors

class ReadFailure: Exception {}
class WriteFailure: Exception {}
class InitFailure: Exception {}

func record(value: int): () {}
func readRemote(): String throws ReadFailure { "remote" }
func writeRemote(value: String): () throws WriteFailure {}

// A field initializer that reads a property contributes that getter's effect.
class ReadsPropertyDuringConstruction(source: RemoteProperties) {
    val value: String = source.name
    constructor(source: RemoteProperties) throws ReadFailure {}
}

class RemoteProperties {
    val name: String { get throws ReadFailure = readRemote() }
    var label: String {
        get throws ReadFailure = readRemote()
        set throws WriteFailure { writeRemote(value) }
    }
}

func constructFromGetter(source: RemoteProperties): ReadsPropertyDuringConstruction throws ReadFailure {
    ReadsPropertyDuringConstruction(source)
}

// Negative: the constructor cannot omit the getter effect introduced by its initializer.
class MissingGetterBudget(source: RemoteProperties) {
    val value: String = source.name
    constructor(source: RemoteProperties) {}
}

// A setter invoked in a constructor contributes only its selected write effect.
class WritesPropertyDuringConstruction(target: RemoteProperties) {
    val complete: String
    constructor(target: RemoteProperties) throws WriteFailure {
        target.label = "initialized"
        complete = "yes"
    }
}

func constructUsingSetter(target: RemoteProperties): WritesPropertyDuringConstruction throws WriteFailure {
    WritesPropertyDuringConstruction(target)
}

// A getter effect is not added when construction only assigns the property.
func setterOnlyConstructionEffect(target: RemoteProperties): WritesPropertyDuringConstruction throws WriteFailure {
    WritesPropertyDuringConstruction(target)
}

// A custom init accessor establishes the backing value only after normal completion.
class InitProperty {
    val name: String {
        get = field
        init { field = value }
    }
    constructor(name: String) {
        this.name = name
    }
}

func constructInitProperty(): InitProperty = InitProperty("valid")

// A checked init accessor contributes an effect to the containing constructor.
class CheckedInitProperty {
    val name: String {
        get = field
        init throws InitFailure {
            validateInit(value)
            field = value
        }
    }
    constructor(name: String) throws InitFailure {
        this.name = name
    }
}

func validateInit(value: String): () throws InitFailure {
    if value.byteLength == 0 { throw InitFailure() }
}

func constructCheckedInitProperty(): CheckedInitProperty throws InitFailure {
    CheckedInitProperty("valid")
}

// Negative: the init accessor effect is not removed by the constructor body.
class DropsInitAccessorEffect {
    val name: String {
        get = field
        init throws InitFailure {
            validateInit(value)
            field = value
        }
    }
    constructor(name: String) { this.name = name }
}

// A failing init accessor leaves its destination backing slot unassigned.
class InitAccessorFailureDoesNotAssign {
    val name: String {
        get = field
        init throws InitFailure { validateInit(value) }
    }
    constructor(name: String) throws InitFailure {
        this.name = name
    }
}

// If validation throws, the instance is not published with a missing non-null property.
func failedInitAccessorNoPublication(): InitAccessorFailureDoesNotAssign throws InitFailure {
    InitAccessorFailureDoesNotAssign("")
}

// An accessor's effect appears at the operation site, not on unrelated property accesses.
class IndependentProperties {
    val safe: int = 1
    val remote: String { get throws ReadFailure = readRemote() }
}

func readSafeProperty(owner: IndependentProperties): int = owner.safe

func readRemoteProperty(owner: IndependentProperties): String throws ReadFailure {
    owner.remote
}

// A constructor does not invoke an unused getter simply because the type declares it.
class DoesNotReadRemoteProperty {
    val local: String = "local"
    val remote: String { get throws ReadFailure = readRemote() }
    constructor() {}
}

func constructWithoutAccessorCall(): DoesNotReadRemoteProperty = DoesNotReadRemoteProperty()

// A body that reads the getter adds its effect to construction.
class ReadsRemoteInBody {
    val value: String
    constructor(owner: IndependentProperties) throws ReadFailure {
        value = owner.remote
    }
}

func constructReadingRemote(owner: IndependentProperties): ReadsRemoteInBody throws ReadFailure {
    ReadsRemoteInBody(owner)
}

// A compound update includes getter, operator, RHS, and setter effects in order.
func compoundRemoteProperty(owner: RemoteProperties): () throws ReadFailure, WriteFailure {
    owner.label += "suffix"
}

// Negative: a write-only budget cannot cover the read half of a compound update.
func compoundMissingGetterBudget(owner: RemoteProperties): () throws WriteFailure {
    owner.label += "suffix"
}

// Negative: a read-only budget cannot cover the final setter operation.
func compoundMissingSetterBudget(owner: RemoteProperties): () throws ReadFailure {
    owner.label += "suffix"
}

// The receiver is evaluated once even when its getter and setter both throw.
func receiverEvaluatedOnce(): () throws ReadFailure, WriteFailure {
    receiver().label += "x"
}

func receiver(): RemoteProperties = RemoteProperties()

// A failed getter prevents RHS evaluation and setter invocation.
func getterFailureStopsUpdate(owner: RemoteProperties): () throws ReadFailure, WriteFailure {
    owner.label += rightValue()
}

func rightValue(): String throws InitFailure { "x" }

// A selected overloaded operator adds its own effect to compound property update.
class Meter(val raw: int) {
    companion object {
        operator func +(left: Meter, right: Meter): Meter throws InitFailure {
            Meter(left.raw + right.raw)
        }
    }
}

class MeterProperty {
    var value: Meter { get throws ReadFailure = readMeter(); set throws WriteFailure {} }
}

func readMeter(): Meter throws ReadFailure = Meter(0)

func meterCompoundEffects(owner: MeterProperty): () throws ReadFailure, InitFailure, WriteFailure {
    owner.value += Meter(1)
}

// A simple assignment does not invoke the getter.
func simpleSetterOnly(owner: RemoteProperties): () throws WriteFailure {
    owner.label = "new"
}

// A simple read does not invoke the setter.
func simpleGetterOnly(owner: RemoteProperties): String throws ReadFailure {
    owner.label
}

// An initializer's getter effect is covered by a constructor supertype budget.
class CoversAccessorByException(source: RemoteProperties) {
    val value: String = source.name
    constructor(source: RemoteProperties) throws Exception {}
}

// Effects from rejected accessor overloads do not enter the selected getter contract.
class OverloadedAccessor {
    val value: int { get = 1 }
    val text: String { get throws ReadFailure = readRemote() }
}

func readSelectedPureGetter(value: OverloadedAccessor): int = value.value

// Reflection metadata records accessor effects independently from constructor effects.
func accessorEffectMetadata(owner: RemoteProperties): String throws ReadFailure {
    owner.name
}

// An init-only accessor cannot be invoked after the object has been published.
func lateInitAssignment(owner: CheckedInitProperty): () {
    // Negative: `owner.name = "late"` is not valid after construction.
    record(100)
}

// Initialization effect metadata survives a constructor call even if the body is empty.
func emptyBodyStillHasInitializerEffect(source: RemoteProperties): ReadsPropertyDuringConstruction throws ReadFailure {
    ReadsPropertyDuringConstruction(source)
}

// Specification note: getters, setters, and init accessors are distinct selected operations.
// Specification note: a selected accessor effect contributes only when that operation executes.
// Specification note: required backing storage becomes valid only after a successful init accessor.
// Specification note: compound update order is read, RHS/operator, then write with each effect.
// Specification note: property names do not let the analyzer infer purity or storage identity.
