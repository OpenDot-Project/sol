// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: required fields become valid before publication.
module spec.construction.fields

class FieldFailure: Exception {}

func record(value: int): () {}
func loadField(value: int): int { record(value); value }

// Every required field is assigned before the constructor returns normally.
class AllFieldsAssigned {
    val first: int
    val second: String
    constructor() {
        first = loadField(1)
        second = "ready"
    }
}

func createAllFields(): AllFieldsAssigned = AllFieldsAssigned()

// Each normal branch assigns the required field.
class BranchAssigned(val choose: bool) {
    val result: int
    constructor(choose: bool) {
        if choose {
            result = 10
        } else {
            result = 11
        }
    }
}

// A terminating branch does not weaken assignment on the only normal path.
class OneNormalPath(flag: bool) {
    val result: int
    constructor(flag: bool) {
        if flag {
            throw FieldFailure()
        }
        result = 20
    }
}

// A branch that completes normally without assignment is insufficient.
class MissingOnOnePath(flag: bool) {
    val result: int
    constructor(flag: bool) {
        if flag {
            result = 30
        }
        // Negative: false path reaches normal constructor exit without result.
    }
}

// A constructor with no normal exit does not publish an unassigned instance.
class NeverCompletes {
    val result: int
    constructor() {
        throw FieldFailure()
    }
}

// Assignment is established only after a potentially throwing initializer returns.
func maybeField(value: int): int throws FieldFailure {
    if value < 0 { throw FieldFailure() }
    value
}

class AssignmentAfterThrow(val source: int) {
    val result: int
    constructor(source: int) {
        result = maybeField(source)
    }
}

func constructAfterThrowingAssignment(source: int): AssignmentAfterThrow throws FieldFailure {
    AssignmentAfterThrow(source)
}

// On an exception edge, the destination is not assumed assigned.
class LaterFieldNotAssigned(val source: int) {
    val first: int = loadField(40)
    val second: int = maybeField(source)
    val third: int = loadField(41)
    constructor(source: int) {}
}

// Earlier fields remain valid if a later initializer throws, but the object stays unpublished.
func earlierFieldDoesNotPublish(source: int): LaterFieldNotAssigned throws FieldFailure {
    LaterFieldNotAssigned(source)
}

// The body cannot read a field before the explicit assignment.
class ReadBeforeWrite {
    val value: int
    constructor() {
        val ignored = this.value
        this.value = 50
    }
}

// The constructor cannot return this before required slots are initialized.
func expose(value: Object): () {}

class EscapeBeforeAssignment {
    val value: String
    constructor() {
        expose(this)
        value = "later"
    }
}

// Storing this into a globally reachable object is also an escape.
object GlobalSlot {
    var value: Object? = null
}

class GlobalEscapeBeforeAssignment {
    val value: String
    constructor() {
        GlobalSlot.value = this
        value = "later"
    }
}

// A callback can observe a captured this before its field is initialized.
func saveCallback(callback: () -> ()): () {}

class CallbackEscapeBeforeAssignment {
    val value: String
    constructor() {
        saveCallback({ observe(this) })
        value = "later"
    }
}

func observe(value: Object): () {}

// Event or delegate registration is treated as an escape unless proven otherwise.
class EventHub {
    func subscribe(callback: () -> ()): () {}
}

class EventEscapeBeforeAssignment(hub: EventHub) {
    val value: String
    constructor(hub: EventHub) {
        hub.subscribe({ observe(this) })
        value = "later"
    }
}

// A native handle cannot retain an incompletely initialized receiver.
func registerNative(value: Object): () {}

class NativeEscapeBeforeAssignment {
    val value: String
    constructor() {
        registerNative(this)
        value = "later"
    }
}

// A virtual call can dispatch into user code and is not automatically safe during construction.
open class VirtualConstruction {
    val ready: String
    constructor() {
        initialize()
        ready = "done"
    }
    open func initialize(): () {}
}

class ObserveUnreadyOverride: VirtualConstruction {
    val extra: String
    constructor(): super() {
        extra = this.ready
    }
    override func initialize(): () {
        observe(this)
    }
}

// A final/private helper may be accepted only after a non-escape and field-read proof.
class ProvenLocalHelper {
    val value: String
    constructor() {
        initializeLocal()
        value = "done"
    }
    private func initializeLocal(): () { record(60) }
}

// A helper that reads the still-unassigned field fails that proof.
class HelperReadsUnreadyField {
    val value: String
    constructor() {
        inspectField()
        value = "done"
    }
    private func inspectField(): () { record(this.value.byteLength) }
}

// An initializer reads only a previously initialized field.
class EarlierFieldDependency {
    val first: int = loadField(70)
    val second: int = this.first + 1
    constructor() {}
}

// Source declaration order fixes initializer dependencies.
class OrderedDependency {
    val first: int = loadField(80)
    init { record(this.first) }
    val second: int = this.first + 1
    constructor() {}
}

// A read of a later field is not justified by the fact that it appears in the same type.
class LaterFieldReadShape {
    val first: int = readSecond(this)
    val second: int = loadField(90)
    constructor() {}
}

func readSecond(value: LaterFieldReadShape): int = 0

// Assignment through a selected init accessor counts only after it completes.
class InitAccessorField {
    val text: String { init { field = value } }
    constructor(value: String) {
        this.text = value
    }
}

// A throwing init accessor does not establish the target field as assigned.
class ThrowingInitAccessorField {
    val text: String {
        init throws FieldFailure {
            throw FieldFailure()
        }
    }
    constructor(value: String) {
        this.text = value
    }
}

// A setter call after publication is not constructor definite assignment proof.
class SetterDoesNotProveBackingSlot {
    var value: String {
        get = field
        set { field = value }
    }
    constructor() {
        this.value = "assigned"
    }
}

// A property that exposes a non-null backing slot must satisfy that slot's construction rule.
class RequiredBackingSlot {
    val value: String { get = field }
    constructor() {
        value = "ready"
    }
}

// A field initializer's exception prevents publication even if later constructor code assigns.
class ThrowBeforeBodyAssignment {
    val first: int = maybeField(-1)
    val second: String
    constructor() {
        second = "not reached"
    }
}

// A nullable field may begin null when the type contract explicitly allows it.
class NullableInitialField {
    var current: Object? = null
    constructor() {}
}

// Zero-filled storage does not satisfy a required non-null reference field.
class NonNullNeedsValue {
    val name: String
    constructor() {
        name = "complete"
    }
}

// Constructor parameters are distinct from heap-visible fields unless declared val/var.
class ParameterIsNotField(name: String) {
    val stored: String = name
    constructor(name: String) {}
}

// A primary `val` parameter initializes its property under the constructor plan.
class PrimaryValIsField(val name: String)

// A primary `var` parameter initializes writable property storage.
class PrimaryVarIsField(var count: int)

// Structs require valid fields before a value can be returned.
struct RequiredValue {
    val text: String
    val count: int
    constructor(text: String, count: int) {
        this.text = text
        this.count = count
    }
}

func createRequiredValue(): RequiredValue = RequiredValue("valid", 1)

// A partial struct is subject to the same invariant across its merged field set.
partial struct PartialRequiredValue {
    val text: String
}

partial struct PartialRequiredValue {
    val count: int
    constructor(text: String, count: int) {
        this.text = text
        this.count = count
    }
}

// A ref struct cannot escape to the heap to avoid field-validity checks.
ref struct BorrowedPair {
    val first: String
    val second: String
    constructor(first: String, second: String) {
        this.first = first
        this.second = second
    }
}

// Assignment to one field does not establish another field with a similar name.
class DistinctFieldSlots {
    val left: String
    val right: String
    constructor() {
        left = "left"
        // Negative: right is still unassigned on normal exit.
    }
}

// Constructor effects from an initializer remain visible at the allocation site.
func initializedByCheckedCall(): AssignmentAfterThrow throws FieldFailure {
    AssignmentAfterThrow(1)
}

// A failed constructor does not yield a value whose fields can be read by the caller.
func noValueOnThrowingConstruction(): int throws FieldFailure {
    val value = AssignmentAfterThrow(-1)
    value.result
}

// Exception paths keep earlier side effects but do not make a half-valid instance observable.
func noHalfInitializedCallback(): AssignmentAfterThrow throws FieldFailure {
    AssignmentAfterThrow(-1)
}

// Specification note: field assignment is a storage identity fact, not a name-based heuristic.
// Specification note: an exceptional edge preserves prior successful writes only.
// Specification note: publication requires every required field to be valid on every normal path.
// Specification note: root maps and GC descriptors must track initialized reference slots.
// Specification note: a constructor cannot use zeroed bytes as a non-null Object or String value.
// Specification note: source effect proof and runtime construction-root handling are both required.
