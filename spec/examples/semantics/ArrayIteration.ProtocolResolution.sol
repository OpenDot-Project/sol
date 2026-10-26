// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative future contract: protocol discovery uses ordinary member binding and exact signatures.
module spec.arrayiteration.protocolresolution

class AdvanceFailure: Exception {}
class ReadFailure: Exception {}

interface Cursor<T> {
    func moveNext(): bool throws AdvanceFailure
    func current(): T throws ReadFailure
}

interface Iterable<T> {
    func iterator(): Cursor<T>
}

class InaccessibleCursor<T> {
    private func moveNext(): bool
    private func current(): T
}

func inaccessibleMethodIsNotProtocol(source: InaccessibleSource): () {
    // Expected visibility error; a backend cannot call private methods by name.
    for val item in source { consume(item) }
}

class WrongAdvanceResult {
    func iterator(): WrongAdvanceResult = this
    func moveNext(): int = 1
    func current(): int = 2
}

func wrongAdvanceResultRejected(source: WrongAdvanceResult): () {
    // Expected protocol error: moveNext must return bool exactly.
    for val item in source { consume(item) }
}

class MissingCurrent {
    func iterator(): MissingCurrent = this
    func moveNext(): bool = false
}

func missingCurrentRejected(source: MissingCurrent): () {
    for val item in source { consume(item) }
}

class WrongCurrentResult {
    func iterator(): WrongCurrentResult = this
    func moveNext(): bool = false
    func current(): String = "wrong"
}

func wrongCurrentTypeRejected(source: WrongCurrentResult): () {
    // Expected: binding type derives from current() and cannot be guessed from the loop body.
    for val item in source { consumeInt(item) }
}

class NoIteratorFactory {
    func moveNext(): bool = false
    func current(): int = 0
}

func noFactoryIsNotImplicitlyTheCursor(source: NoIteratorFactory): () {
    // Expected error: the pattern requires iterator() on the iterable source.
    for val item in source { consume(item) }
}

class MultipleFactories {
    func iterator(): IntCursor = IntCursor()
    func iterator(): StringCursor = StringCursor()
}

func ambiguousFactoryRejected(source: MultipleFactories): () {
    // Expected overload ambiguity unless ordinary argument/type context selects exactly one.
    for val item in source { consume(item) }
}

class ExactFactoryOverload {
    func iterator(): IntCursor = IntCursor()
    func iterator(mode: int): StringCursor = StringCursor()
}

func exactFactoryOverloadSelected(source: ExactFactoryOverload): () {
    for val item in source { consumeInt(item) }
}

class InheritedCursor<T> {
    func moveNext(): bool = false
    func current(): T = defaultValue()
}

class InheritedSource<T>: InheritedCursor<T> {
    func iterator(): InheritedCursor<T> = this
}

func inheritedProtocolUsesConstructedType(source: InheritedSource<int>): () {
    for val item in source { consumeInt(item) }
}

func hiddenBaseMemberDoesNotReappear(source: HiddenDerivedSource): () {
    // Expected visibility/hiding result follows ordinary member-resolution rules.
    for val item in source { consume(item) }
}

func interfaceRouteUsesDeclaredType(source: Iterable<int>): () {
    for val item in source { consumeInt(item) }
}

func structuralRouteUsesStaticType(source: StructuralSource): () {
    for val item in source { consumeInt(item) }
}

func <T> cursorTypeVariableSubstitutesCurrent(source: Iterable<T>): () {
    for val item in source { consume(item) }
}

func <T> cursorTypeVariableKeepsInvariantIdentity(source: InvariantSource<T>): () {
    for val item in source { consume(item) }
}

func nullableCurrentRequiresNullableBinding(source: NullableSource): () {
    for val item in source { consumeNullable(item) }
}

func nullableCurrentCannotBindToNonNullable(source: NullableSource): () {
    // Expected conversion error if current() returns T? and the binding requires T.
    for val item in source { consumeInt(item) }
}

func implicitCoreImportDoesNotSelectCursorMethods(source: UnrelatedSource): () {
    // Protocol candidates must come from the source type's applicable members, not spelling alone.
    for val item in source { consume(item) }
}

func explicitExtensionProtocolNotAssumed(source: UnrelatedSource): () {
    // No implicit extension discovery is introduced by the foreach syntax.
    for val item in source { consume(item) }
}

func iteratorCallUsesOrdinaryReceiverDispatch(source: VirtualSource): () {
    for val item in source { consume(item) }
}

func methodGroupIsNotIteratorValue(source: MethodGroupSource): () {
    // Expected type error unless the resolved member is callable with the required receiver shape.
    for val item in source { consume(item) }
}

func propertyNamedIteratorIsNotFactory(source: PropertyIteratorSource): () {
    // `iterator` must resolve to the required callable operation, not a same-named property.
    for val item in source { consume(item) }
}

class IntCursor: Cursor<int> {
    func moveNext(): bool = false
    func current(): int = 0
}

class StringCursor: Cursor<String> {
    func moveNext(): bool = false
    func current(): String = ""
}

// Protocol resolution completes before flow transfer. A partially resolved shape is not lowered as
// an array index loop, and member visibility/override/constructed-type rules remain authoritative.
