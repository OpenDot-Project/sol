// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: `this` remains private until required fields are valid.
module spec.construction.noescape

import std.reflect

class EscapeFailure: Exception {}

func record(value: int): () {}
func observe(value: Object): () {}

// A receiver cannot be stored globally while a required field remains unassigned.
object SharedOwnerSlot {
    var value: Object? = null
}

class GlobalThisEscape {
    val ready: String
    constructor() {
        SharedOwnerSlot.value = this
        ready = "ready"
    }
}

// Passing this to an unknown function is an escape unless the callee has a verified no-escape contract.
func unknownConsumer(value: Object): () {}

class UnknownCallEscape {
    val ready: String
    constructor() {
        unknownConsumer(this)
        ready = "ready"
    }
}

// Calling a proven local final helper is permitted when it cannot read or publish unready fields.
class ProvenLocalCall {
    val ready: String
    constructor() {
        recordLocally()
        ready = "ready"
    }
    private func recordLocally(): () { record(1) }
}

// The helper is not safe if it reads a required field before assignment.
class LocalReadBeforeAssignment {
    val ready: String
    constructor() {
        inspectReady()
        ready = "ready"
    }
    private func inspectReady(): () { record(this.ready.byteLength) }
}

// A private method that stores this indirectly still escapes.
class IndirectLocalEscape {
    val ready: String
    constructor() {
        publishLocally()
        ready = "ready"
    }
    private func publishLocally(): () { SharedOwnerSlot.value = this }
}

// Virtual dispatch may reach an override that observes fields before derived initialization.
open class VirtualOwner {
    val baseReady: String
    constructor() {
        initialize()
        baseReady = "base"
    }
    open func initialize(): () {}
}

class DerivedVirtualOwner: VirtualOwner {
    val derivedReady: String
    constructor(): super() { derivedReady = "derived" }
    override func initialize(): () { observe(this) }
}

// A call to a non-final open method is not assumed to be local just because the current body is empty.
class OpenMethodEscape {
    val ready: String
    constructor() {
        onInitialize()
        ready = "ready"
    }
    open func onInitialize(): () {}
}

// Explicitly final method dispatch can be analyzed with its verified body and call effects.
class FinalMethodOwner {
    val ready: String
    constructor() {
        initializeLocally()
        ready = "ready"
    }
    final func initializeLocally(): () { record(2) }
}

// Storing this in a closure that escapes publishes the incomplete receiver.
func saveCallback(callback: () -> ()): () {}

class ClosureEscape {
    val ready: String
    constructor() {
        saveCallback({ observe(this) })
        ready = "ready"
    }
}

// A closure invoked synchronously before construction continues still needs a proof of non-escape.
func invokeImmediately(callback: () -> ()): () { callback() }

class ImmediateCallbackOwner {
    val ready: String
    constructor() {
        invokeImmediately({ record(3) })
        ready = "ready"
    }
}

// Capturing this in an immediately invoked closure can be valid only if the capture cannot escape.
class ImmediateThisCapture {
    val ready: String
    constructor() {
        invokeImmediately({ record(this.identityHashCode()) })
        ready = "ready"
    }
}

// A returned closure that captures this outlives the construction scope.
func makeObserver(value: Object): () -> () {
    { observe(value) }
}

class ReturnedClosureEscape {
    val ready: String
    val observer: () -> ()
    constructor() {
        observer = makeObserver(this)
        ready = "ready"
    }
}

// A lambda stored in a field is an escape even when it is not invoked immediately.
class StoredCallbackEscape {
    val ready: String
    val callback: () -> ()
    constructor() {
        callback = { observe(this) }
        ready = "ready"
    }
}

// Passing this as a property setter value may expose it to external code.
class PropertySink {
    var value: Object? = null
}

class SetterEscape(sink: PropertySink) {
    val ready: String
    constructor(sink: PropertySink) {
        sink.value = this
        ready = "ready"
    }
}

// A delegate can retain this if provideDelegate stores its owner argument.
class RetainingDelegate {
    operator func provideDelegate(owner: Object?, property: SolProperty<String>): RetainingDelegate {
        retain(owner)
        this
    }
}

func retain(value: Object?): () {}

class DelegateOwnerEscape {
    val ready: String
    val name: String by RetainingDelegate()
    constructor() {
        ready = "ready"
    }
}

// Registration of a native callback can keep this reachable after constructor return.
func registerNative(callback: () -> ()): () {}

class NativeCallbackEscape {
    val ready: String
    constructor() {
        registerNative({ observe(this) })
        ready = "ready"
    }
}

// Registering a callback after all fields are assigned is a separate lifetime contract.
class ReadyBeforeCallback {
    val ready: String
    constructor() {
        ready = "ready"
        registerNative({ observe(this) })
    }
}

// A failing callback registration still leaves a fully initialized object unpublished on failure.
func registerMayFail(callback: () -> ()): () throws EscapeFailure {}

class CallbackRegistrationFailure {
    val ready: String
    constructor() throws EscapeFailure {
        ready = "ready"
        registerMayFail({ observe(this) })
    }
}

// A registered callback must be undone if a later constructor operation throws.
func unregisterNative(): () {}
func failAfterRegistration(): () throws EscapeFailure { throw EscapeFailure() }

class RegistrationNeedsCleanup {
    val ready: String
    constructor() throws EscapeFailure {
        ready = "ready"
        registerMayFail({ observe(this) })
        defer { unregisterNative() }
        failAfterRegistration()
    }
}

// The cleanup is registered after the external side effect succeeds.
class RegistrationPrefixFailure {
    val ready: String
    constructor() throws EscapeFailure {
        ready = "ready"
        failAfterRegistration()
        registerMayFail({ observe(this) })
        defer { unregisterNative() }
    }
}

// Returning this from a constructor body is not a valid early publication path.
class ReturnThisShape {
    val ready: String
    constructor() {
        return this
        ready = "later"
    }
}

// Storing this inside an array is heap publication.
class ArrayThisEscape {
    val ready: String
    constructor() {
        val values: []Object = [this]
        ready = "ready"
    }
}

// Adding this to a collection is publication through an aliasing container.
class ObjectCollection {
    func add(value: Object): () {}
}

class CollectionThisEscape(collection: ObjectCollection) {
    val ready: String
    constructor(collection: ObjectCollection) {
        collection.add(this)
        ready = "ready"
    }
}

// Reflection over the type descriptor does not itself publish the owner instance.
func inspectType(type: SolProperty<String>): () {}

class MetadataOnlyInspection {
    val ready: String
    val name: String = "name"
    constructor() {
        inspectType(propertyOf(this.name))
        ready = "ready"
    }
}

func propertyOf(value: String): SolProperty<String> = SolProperty<String>()

// Reflection that receives this as a value remains a possible escape.
func inspectObject(value: Object): () {}

class ReflectiveThisEscape {
    val ready: String
    constructor() {
        inspectObject(this)
        ready = "ready"
    }
}

// Base-constructor execution is part of one verified initialization chain, not external publication.
open class BaseInitialization {
    val baseReady: String
    constructor() { baseReady = "base" }
}

class DerivedInitialization: BaseInitialization {
    val derivedReady: String
    constructor(): super() { derivedReady = "derived" }
}

// Base constructors must not pass this to unknown code before derived fields are valid.
open class EscapingBase {
    val baseReady: String
    constructor() {
        unknownConsumer(this)
        baseReady = "base"
    }
}

class DerivedFromEscapingBase: EscapingBase {
    val derivedReady: String
    constructor(): super() { derivedReady = "derived" }
}

// A final class can still escape through a constructor argument or stored closure.
class FinalClassEscape {
    val ready: String
    constructor() {
        saveCallback({ observe(this) })
        ready = "ready"
    }
}

// A non-escaping analysis result is valid only for the exact call target and compilation metadata.
func verifiedNoEscapeTarget(): () { record(4) }

class SummaryBoundNoEscape {
    val ready: String
    constructor() {
        verifiedNoEscapeTarget()
        ready = "ready"
    }
}

// Missing callee body or stale summary cannot be treated as a no-escape proof.
class DeferredEscapeProof {
    val ready: String
    constructor() {
        unknownConsumer(this)
        ready = "ready"
    }
}

// Specification note: no-escape is a proof tied to selected target identity and body metadata.
// Specification note: virtual dispatch, closures, delegates, globals, reflection, and native handles can publish this.
// Specification note: a successfully initialized object may escape only after required fields are valid.
// Specification note: failed constructor work does not roll back external side effects already completed.
// Specification note: cleanup compensates resources only when an explicit lexical action was registered.
