// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: generic construction and generic exception effects are substituted.
module spec.construction.genericeffects

class GenericReadFailure: Exception {}
class GenericNetworkFailure: Exception {}
class GenericRuntimeFailure: RuntimeException {}

// A generic value container does not erase the selected argument's construction effects.
class Box<T>(val value: T)

func readGenericValue(): int throws GenericReadFailure { 1 }

func constructBoxWithEffect(): Box<int> throws GenericReadFailure {
    Box(readGenericValue())
}

// Supplying a previously computed value moves its effect to the earlier statement.
func constructBoxAfterEffect(): Box<int> throws GenericReadFailure {
    val value = readGenericValue()
    Box(value)
}

// Once the value exists, copying the generic container does not repeat its constructor.
func copyConstructedBox(value: Box<int>): Box<int> = value

// A generic reference constraint affects storage eligibility, not checked-effect coverage.
class ReferenceBox<T: Object>(val value: T)

class Customer {}

func constructReferenceBox(customer: Customer): ReferenceBox<Customer> {
    ReferenceBox(customer)
}

// Boxing a value type does not satisfy a nominal reference constraint.
func invalidReferenceBoxArgument(): ReferenceBox<int> {
    // Negative design shape: int does not satisfy T: Object.
    ReferenceBox(1)
}

// A generic checked exception parameter must be constrained to Throwable.
func <E: Throwable> makeFailure(error: E): E = error

func genericFailureValue(): GenericReadFailure = makeFailure(GenericReadFailure())

// Throwing the closed generic value retains its concrete checked effect.
func throwConcreteGenericFailure(): ! throws GenericReadFailure {
    val error = makeFailure(GenericReadFailure())
    throw error
}

// Widening storage to Throwable widens the statically visible effect.
func throwWidenedGenericFailure(error: Throwable): ! throws Throwable {
    val stored: Throwable = error
    throw stored
}

// A valid smart cast can narrow a broad local before it is thrown.
func narrowGenericFailure(error: Throwable): ! throws GenericReadFailure {
    if error is GenericReadFailure {
        throw error
    }
    throw GenericReadFailure()
}

// A RuntimeException specialization remains unchecked after substitution.
func throwGenericRuntimeFailure(): ! {
    throw makeFailure(GenericRuntimeFailure())
}

// A generic initializer's exception parameter is part of the selected call contract.
func <E: Throwable> createCheckedBox(error: E): Box<E> throws E {
    Box(error)
}

func checkedBoxCall(): Box<GenericReadFailure> throws GenericReadFailure {
    createCheckedBox(GenericReadFailure())
}

// The call-site budget is checked after E resolves to GenericReadFailure.
func checkedBoxWrongBudget(): Box<GenericReadFailure> throws GenericNetworkFailure {
    createCheckedBox(GenericReadFailure())
}

// A generic constructor body can contribute effects independent of its type argument.
class ValidatingBox<T>(val value: T) throws GenericReadFailure {
    init { validateGenericValue() }
}

func validateGenericValue(): () throws GenericReadFailure {}

func constructValidatingInt(): ValidatingBox<int> throws GenericReadFailure {
    ValidatingBox(5)
}

func constructValidatingString(): ValidatingBox<String> throws GenericReadFailure {
    ValidatingBox("value")
}

// Specializing T does not erase the constructor's independent validation effect.
func constructValidatingWithoutEffect(): ValidatingBox<int> {
    ValidatingBox(6)
}

// A generic default's checked effect is evaluated only when omitted.
class DefaultedGenericBox<T>(val value: T, val token: int = loadToken())

func loadToken(): int throws GenericNetworkFailure { 7 }

func omitGenericDefault(): DefaultedGenericBox<String> throws GenericNetworkFailure {
    DefaultedGenericBox("value")
}

func supplyGenericDefault(): DefaultedGenericBox<String> {
    DefaultedGenericBox("value", 8)
}

// A generic default is evaluated after explicit arguments, with source evaluation order preserved.
func explicitBeforeGenericDefault(): DefaultedGenericBox<String> throws GenericNetworkFailure {
    DefaultedGenericBox(value = createString(), token = loadToken())
}

func createString(): String { "first" }

// A generic constructor effect does not depend on which property is read later.
func constructThenRead(value: ValidatingBox<int>): int = value.value

// Constructor and getter effects compose when the expression constructs and immediately reads.
class GenericReadBox<T>(val value: T) throws GenericReadFailure {
    val label: String { get throws GenericNetworkFailure = readGenericLabel() }
}

func readGenericLabel(): String throws GenericNetworkFailure { "label" }

func constructAndReadGenericBox(): String throws GenericReadFailure, GenericNetworkFailure {
    GenericReadBox(1).label
}

// A broad constructor budget does not make a narrower getter effect disappear.
func omitGenericGetterBudget(): String throws Exception {
    GenericReadBox(1).label
}

// Generic function values preserve their checked effect type after specialization.
func <E: Throwable> makeCallback(error: E): () -> () throws E {
    { throw error }
}

func invokeSpecializedCallback(): () throws GenericReadFailure {
    val callback = makeCallback(GenericReadFailure())
    callback()
}

// Constructing the callback does not execute its body or throw its captured effect.
func callbackCreationOnly(): () {
    val callback = makeCallback(GenericReadFailure())
    record(callback.hashCode())
}

// The captured exception remains a managed root while the callback is retained.
class CallbackHolder<T>(val callback: T)

func retainGenericCallback(): CallbackHolder<() -> () throws GenericReadFailure> {
    CallbackHolder(makeCallback(GenericReadFailure()))
}

// A generic type parameter cannot silently bypass ref-like heap restrictions.
func <T: Object> storeInHeap(value: T): Box<T> = Box(value)

func noValueTypeReferenceCoercion(): Box<int> {
    // Negative design shape: generic T: Object does not accept int by boxing.
    Box(1)
}

// Constraint resolution and checked-effect substitution are independent analysis passes.
func constraintAndEffectBothChecked(): ValidatingBox<Customer> throws GenericReadFailure {
    ValidatingBox(Customer())
}

// Recursive generic constructor expansion is bounded and cannot overflow the host stack.
class NestedBox<T>(val value: T)

func nestedBoxChain(): NestedBox<NestedBox<NestedBox<int>>> {
    NestedBox(NestedBox(NestedBox(1)))
}

// A resource-limited generic expansion remains Deferred rather than pure.
func deferredGenericEffectProof(): ValidatingBox<int> throws GenericReadFailure {
    ValidatingBox(2)
}

// Serialized generic metadata includes closed type arguments and substituted effect types.
func metadataForClosedSpecialization(): Box<GenericReadFailure> {
    Box(GenericReadFailure())
}

// A metadata reader without the exception hierarchy cannot assume a specialization is unchecked.
func incompleteGenericHierarchy(): ValidatingBox<int> throws GenericReadFailure {
    ValidatingBox(3)
}

// Specification note: generic checked effects are resolved after type substitution.
// Specification note: effectful defaults belong only to calls that omit the corresponding parameter.
// Specification note: object/reference constraints do not use boxing as constraint satisfaction.
// Specification note: closure creation does not execute the body's checked effect.
// Specification note: resource exhaustion creates a diagnostic/Deferred result, not an empty budget.
