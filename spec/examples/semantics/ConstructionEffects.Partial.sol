// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: partial members merge, initialization side effects have one owner.
module spec.construction.parts

class PartialFailure: Exception {}

func record(value: int): () {}
func initial(value: int): int { record(value); value }

// One partial class part owns ordered field initialization and its constructor.
partial class PartialRecord {
    val first: int = initial(1)
    init { record(2) }
    constructor(second: int) {
        this.second = second
    }
}

// A second part may contribute an uninitialized field and methods.
partial class PartialRecord {
    val second: int
    func total(): int = this.first + this.second
}

func makePartialRecord(): PartialRecord = PartialRecord(3)

// Every merged required field is assigned before publication.
func readMergedFields(value: PartialRecord): int {
    value.first + value.second
}

// Source-file order does not move the second part's field before the designated initializer.
func partOrderIsNotInitializationOrder(): PartialRecord = PartialRecord(4)

// A second initializer-bearing part is invalid even if the files sort consistently.
partial class ConflictingInitializers {
    val first: int = initial(10)
    constructor() { this.second = 11 }
}

partial class ConflictingInitializers {
    val second: int = initial(12)
}

// Negative: ConflictingInitializers has side effects in two distinct partial parts.
func invalidTwoInitializationParts(): () {}

// A constructor assignment in the designated part covers a field from another part.
partial class CentralAssignment {
    val first: String = "first"
    constructor(second: String) {
        this.second = second
    }
}

partial class CentralAssignment {
    val second: String
    func values(): String = this.first + this.second
}

func makeCentralAssignment(): CentralAssignment = CentralAssignment("second")

// An unassigned merged field remains invalid at normal constructor exit.
partial class MissingMergedField {
    val first: String = "first"
    constructor() {}
}

partial class MissingMergedField {
    val second: String
}

// Negative: the constructor does not assign the required second field.
func missingMergedAssignmentIsRejected(): () {}

// Every declaration in a merged type is marked partial.
partial class MarkerAgreement {
    val first: int = initial(20)
    constructor(second: int) { this.second = second }
}

partial class MarkerAgreement {
    val second: int
    func value(): int = first + second
}

// Negative: mixing partial and non-partial declarations for one identity is rejected.
class MarkerAgreementConflict {
    val value: int = 21
}

partial class MarkerAgreementConflict {
    val other: int
}

// Generic arity is part of the merged identity.
partial class GenericPart<T> {
    val first: T
    constructor(first: T) { this.first = first }
}

partial class GenericPart<T> {
    func getFirst(): T = first
}

// A mismatched generic parameter list cannot be merged by textual name alone.
partial class GenericArityConflict<T> {
    val first: T
}

partial class GenericArityConflict<T, U> {
    val second: U
}

// Negative: the nominal arity conflicts.
func genericArityConflictIsRejected(): () {}

// Generic constraints must agree in every part.
partial class GenericConstraintAgreement<T: Object> {
    val value: T
    constructor(value: T) { this.value = value }
}

partial class GenericConstraintAgreement<T: Object> {
    func getValue(): T = value
}

// Negative: the same type parameter cannot have different constraints across parts.
partial class GenericConstraintConflict<T: Object> {
    val value: T
}

partial class GenericConstraintConflict<T: Throwable> {
    func getValue(): T = value
}

// Nested partial declarations are identified by their nominal containing type.
partial class Container {
    partial class Nested {
        val first: int = initial(30)
        constructor(second: int) { this.second = second }
    }
}

partial class Container {
    partial class Nested {
        val second: int
        func sum(): int = first + second
    }
}

func nestedPartialConstruction(): Container.Nested = Container.Nested(31)

// An initializer in an unrelated nested nominal type is not a second part initializer.
partial class SeparateNestedOwners {
    val value: int = initial(40)
    constructor() {}
    class Inner {
        val value: int = initial(41)
    }
}

partial class SeparateNestedOwners {
    func getValue(): int = value
}

// Partial structs share required-field assignment across all parts.
partial struct PartialPoint {
    val x: int
    constructor(x: int, y: int) {
        this.x = x
        this.y = y
    }
}

partial struct PartialPoint {
    val y: int
    func sum(): int = x + y
}

func makePartialPoint(): PartialPoint = PartialPoint(1, 2)

// A partial ref struct keeps byref-like escape restrictions after members merge.
partial ref struct PartialBorrow {
    val first: int
    constructor(first: int, second: int) {
        this.first = first
        this.second = second
    }
}

partial ref struct PartialBorrow {
    val second: int
    func sum(): int = first + second
}

// Partial object declarations have one logical lazy initialization owner.
partial object PartialSettings {
    val first: int = initial(50)
}

partial object PartialSettings {
    val second: int = initial(51)
}

// Negative: multiple object initialization parts have ordering-sensitive effects.
func partialObjectInitConflict(): () {}

// An object with one initialization part may have additional noninitializing parts.
partial object SingleOwnerSettings {
    val first: int = initial(60)
}

partial object SingleOwnerSettings {
    val second: int
    func sum(): int = first + second
}

// Object initialization effect metadata belongs to the merged object symbol.
func readMergedObject(): int throws PartialFailure {
    SingleOwnerSettings.first
}

// Checked effects from the one initialization part are not lost in a method-only part.
partial class EffectfulPartial {
    val value: int = effectfulInitial()
    constructor() {}
}

func effectfulInitial(): int throws PartialFailure { throw PartialFailure() }

partial class EffectfulPartial {
    func getValue(): int = value
}

func constructEffectfulPartial(): EffectfulPartial throws PartialFailure {
    EffectfulPartial()
}

// An initializer failure leaves the merged object unpublished.
func noPartialOnFailure(): EffectfulPartial throws PartialFailure {
    EffectfulPartial()
}

// Partial field layout is independent of part file path and manifest order.
func stablePartialLayout(value: PartialPoint): int = value.sum()

// Every part must agree on the same base type and interfaces.
partial class BaseAgreement: Object {
    val value: int = initial(70)
}

partial class BaseAgreement: Object {
    constructor() {}
}

// Negative: different base identities cannot merge as one class.
open class BaseOne {}
open class BaseTwo {}

partial class BaseConflict: BaseOne {
    val value: int
}

partial class BaseConflict: BaseTwo {
    constructor() { this.value = 71 }
}

// Duplicate constructor signatures across parts are rejected.
partial class DuplicateConstructor {
    constructor(value: int) { this.value = value }
    val value: int
}

partial class DuplicateConstructor {
    constructor(value: int) { this.value = value }
}

// Method overload collection merges, but duplicate exact signatures remain errors.
partial class MergedMethods {
    func describe(value: int): int = value
}

partial class MergedMethods {
    func describe(value: String): int = value.byteLength
}

// Negative: same method signature in two parts is still duplicate.
partial class DuplicateMethod {
    func value(): int = 1
}

partial class DuplicateMethod {
    func value(): int = 2
}

// A field initializer in the designated part can use only the resolved ordering contract.
partial class InitializerDependencies {
    val first: int = initial(80)
    val second: int = first + 1
    constructor() { this.third = second + 1 }
}

partial class InitializerDependencies {
    val third: int
}

// A different part cannot interpose an initializer between first and second.
func initializerPartSequence(): InitializerDependencies = InitializerDependencies()

// Partial metadata identifies all source parts without using source path as nominal identity.
func metadataUsesLogicalIdentity(value: GenericPart<String>): String = value.getFirst()

// A failed part merge blocks constructor verification and native lowering.
func invalidMergeCannotBeConstructed(): () {}

// Specification note: one designated part owns side effects; other parts contribute declarations.
// Specification note: all merged required fields participate in the same definite-assignment proof.
// Specification note: declaration merge and field initialization order are separate contracts.
// Specification note: no part is selected by sort order, checkout order, or parallel discovery.
// Specification note: incompatible metadata never degrades to a partial successful type.
