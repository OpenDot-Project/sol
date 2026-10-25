// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: delegated initialization effects differ from access effects.
module spec.construction.delegates

import std.reflect

class SetupFailure: Exception {}
class ReadFailure: Exception {}
class WriteFailure: Exception {}
class OwnerFailure: Exception {}

func record(value: int): () {}

// The descriptor identifies the declaration, not a per-instance value slot.
class SimpleDelegate(private var value: String) {
    operator func getValue(owner: Object?, property: SolProperty<String>): String {
        this.value
    }
    operator func setValue(owner: Object?, property: SolProperty<String>, value: String): () {
        this.value = value
    }
}

class SimpleOwner(initial: String) {
    var name: String by SimpleDelegate(initial)
}

func createSimpleOwner(): SimpleOwner = SimpleOwner("Ada")

// provideDelegate runs during initialization and returns the stored delegate.
class ValidatingDelegate {
    operator func provideDelegate(owner: Object?, property: SolProperty<String>): ValidatingDelegate {
        this
    }
    operator func getValue(owner: Object?, property: SolProperty<String>): String = "value"
}

class ValidatedOwner {
    val name: String by ValidatingDelegate()
}

func setupValidatedOwner(): ValidatedOwner = ValidatedOwner()

// A checked provideDelegate effect is part of constructing the owner.
class CheckedSetupDelegate {
    operator func provideDelegate(owner: Object?, property: SolProperty<int>): CheckedSetupDelegate throws SetupFailure {
        validateSetup()
        this
    }
    operator func getValue(owner: Object?, property: SolProperty<int>): int = 1
}

func validateSetup(): () throws SetupFailure { throw SetupFailure() }

class CheckedSetupOwner {
    val count: int by CheckedSetupDelegate()
}

func constructCheckedSetupOwner(): CheckedSetupOwner throws SetupFailure {
    CheckedSetupOwner()
}

// Negative: owner construction cannot drop the selected provideDelegate effect.
func constructWithoutSetupBudget(): CheckedSetupOwner {
    CheckedSetupOwner()
}

// A property read invokes getValue and contributes only its read effect.
class CheckedReader {
    operator func getValue(owner: Object?, property: SolProperty<int>): int throws ReadFailure {
        readRemote()
    }
}

func readRemote(): int throws ReadFailure { throw ReadFailure() }

class ReadOwner {
    val value: int by CheckedReader()
}

func constructReadOwner(): ReadOwner = ReadOwner()

func readDelegatedValue(owner: ReadOwner): int throws ReadFailure {
    owner.value
}

// Construction of ReadOwner does not run getValue merely because a property is declared.
func setupDoesNotReadProperty(): ReadOwner = ReadOwner()

// A property write invokes setValue and contributes its write effect.
class CheckedWriter {
    operator func setValue(owner: Object?, property: SolProperty<int>, value: int): () throws WriteFailure {
        writeRemote(value)
    }
}

func writeRemote(value: int): () throws WriteFailure {}

class WriteOwner {
    var value: int by CheckedWriter()
}

func writeDelegatedValue(owner: WriteOwner): () throws WriteFailure {
    owner.value = 2
}

// A val delegate has no write call and need not expose setValue.
class ReadOnlyDelegate {
    operator func getValue(owner: Object?, property: SolProperty<String>): String = "read only"
}

class ReadOnlyOwner {
    val name: String by ReadOnlyDelegate()
}

func readOnlyName(owner: ReadOnlyOwner): String = owner.name

// A compound update accumulates getter, operator, and setter effects.
class ReadWriteDelegate {
    operator func getValue(owner: Object?, property: SolProperty<int>): int throws ReadFailure { 0 }
    operator func setValue(owner: Object?, property: SolProperty<int>, value: int): () throws WriteFailure {}
}

class ReadWriteOwner {
    var count: int by ReadWriteDelegate()
}

func compoundDelegatedUpdate(owner: ReadWriteOwner): () throws ReadFailure, WriteFailure {
    owner.count += 1
}

// Negative: the selected getter effect is required even when the setter budget is present.
func compoundMissingReadEffect(owner: ReadWriteOwner): () throws WriteFailure {
    owner.count += 1
}

// Negative: the selected setter effect is required even when the getter budget is present.
func compoundMissingWriteEffect(owner: ReadWriteOwner): () throws ReadFailure {
    owner.count += 1
}

// A failing getter prevents right-side evaluation and setter invocation.
func compoundGetterFailsFirst(owner: ReadWriteOwner): () throws ReadFailure, WriteFailure {
    owner.count += rightHandSide()
}

func rightHandSide(): int throws OwnerFailure { throw OwnerFailure() }

// The getter effect, right-side effect, and setter effect are all part of the expression.
func compoundAllEffects(owner: ReadWriteOwner): () throws ReadFailure, OwnerFailure, WriteFailure {
    owner.count += rightHandSide()
}

// A failed setter occurs after read and operator side effects; those effects are not rolled back.
func setterFailureAfterRead(owner: ReadWriteOwner): () throws ReadFailure, OwnerFailure, WriteFailure {
    owner.count += rightHandSide()
}

// Each instance stores its delegate value separately despite sharing descriptor metadata.
func twoIndependentOwners(): int {
    val first = SimpleOwner("first")
    val second = SimpleOwner("second")
    first.name.byteLength + second.name.byteLength
}

// SolProperty is a declaration descriptor shared by accesses; it is not the owner's stored name.
func descriptorDoesNotOwnCurrentValue(owner: SimpleOwner): String = owner.name

// Instance thisRef is passed only through a verified construction boundary.
class OwnerAwareDelegate {
    operator func provideDelegate(owner: Object?, property: SolProperty<String>): OwnerAwareDelegate {
        this
    }
    operator func getValue(owner: Object?, property: SolProperty<String>): String = property.name
}

class OwnerAware {
    val title: String by OwnerAwareDelegate()
}

func constructOwnerAware(): OwnerAware = OwnerAware()

// Negative: a descriptor hook cannot publish this before required fields are assigned.
class EscapingProvideDelegate {
    val title: String
    constructor() {
        // Negative design shape: evaluate provideDelegate(this, property) before title is valid.
        title = "later"
    }
}

// A nested callback stored by provideDelegate is an escape unless proven non-observing.
class CallbackDelegate {
    operator func provideDelegate(owner: Object?, property: SolProperty<String>): CallbackDelegate {
        register({ inspect(owner) })
        this
    }
}

func register(callback: () -> ()): () {}
func inspect(value: Object?): () {}

class CallbackOwner {
    val ready: String
    val name: String by CallbackDelegate()
    constructor() {
        ready = "after declaration"
    }
}

// An eager callback from provideDelegate can observe the owner before publication.
func callbackOwnerConstruction(): CallbackOwner = CallbackOwner()

// A local delegated property has no instance receiver and follows its local initialization scope.
func localDelegation(): String {
    val local: String by SimpleDelegate("local")
    local
}

// A top-level or object delegated property has its own initialization effect metadata.
object LazyDelegatedSettings {
    val current: int by CheckedSetupDelegate()
}

func firstOrCachedDelegateAccess(): int throws SetupFailure {
    LazyDelegatedSettings.current
}

// A delegated initializer failure leaves the containing type unpublished.
class FailingDelegatedOwner {
    val value: int by CheckedSetupDelegate()
    val later: String = "not reached"
}

func failedDelegatedOwnerSetup(): FailingDelegatedOwner throws SetupFailure {
    FailingDelegatedOwner()
}

// An accessor effect does not migrate into the constructor if no access occurs there.
class DeferredReadOwner {
    val value: int by CheckedReader()
    constructor() {}
}

func constructWithoutGetterCall(): DeferredReadOwner = DeferredReadOwner()

func invokeGetterLater(owner: DeferredReadOwner): int throws ReadFailure {
    owner.value
}

// An initializer that explicitly reads its delegated property includes the getter effect.
class ReadsDuringConstruction {
    val source: int by CheckedReader()
    val copy: int = this.source
    constructor() {}
}

func constructByReadingDelegate(): ReadsDuringConstruction throws ReadFailure {
    ReadsDuringConstruction()
}

// A delegated property cannot provide ref/out writable storage through getter/setter copying.
func delegatedPropertyCannotBeRef(owner: WriteOwner): () {
    // Negative semantic shape: updateByReference(ref owner.value)
    owner.value = 3
}

func updateByReference(ref value: int): () { value += 1 }

// A property descriptor may expose name/type/accessor metadata but not mutable instance contents.
class DescriptorSensitiveDelegate {
    operator func getValue(owner: Object?, property: SolProperty<int>): int = property.name.byteLength
}

class DescriptorSensitiveOwner {
    val count: int by DescriptorSensitiveDelegate()
}

func descriptorNameLookup(owner: DescriptorSensitiveOwner): int = owner.count

// Delegate expression evaluation itself occurs once per owner initialization.
func makeDelegate(): SimpleDelegate {
    record(200)
    SimpleDelegate("created")
}

class ExpressionDelegateOwner {
    val name: String by makeDelegate()
}

func expressionDelegateRunsAtConstruction(): ExpressionDelegateOwner = ExpressionDelegateOwner()

// A delegate expression failure prevents the owner's later fields and publication.
func failedDelegateExpression(): ExpressionDelegateOwner throws OwnerFailure {
    ExpressionDelegateOwner()
}

// Rejected delegate candidates do not contribute phantom initialization effects.
func selectedDelegateContractOnly(): CheckedSetupOwner throws SetupFailure {
    CheckedSetupOwner()
}

// A property read invokes one selected getValue overload exactly once.
class OverloadedReadDelegate {
    operator func getValue(owner: Object?, property: SolProperty<int>): int = 1
    operator func getValue(owner: Object?, property: SolProperty<String>): String = "text"
}

class OverloadedReadOwner {
    val count: int by OverloadedReadDelegate()
    val name: String by OverloadedReadDelegate()
}

func readOneSelectedOverload(owner: OverloadedReadOwner): int = owner.count

// A malformed or incomplete SolProperty metadata object does not imply a pure accessor.
func incompleteDescriptorProof(owner: ReadOwner): int throws ReadFailure {
    owner.value
}

// Specification note: provideDelegate belongs to initialization, getValue to read, setValue to write.
// Specification note: construction does not call getValue unless an initializer explicitly reads.
// Specification note: descriptor identity is declaration-based and shared across instances.
// Specification note: eager callback registration must pass the constructor no-escape proof.
// Specification note: delegates cannot manufacture writable ref/out locations from copies.
// Specification note: checked effect metadata for each protocol method remains distinct.
