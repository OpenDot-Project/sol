// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative future contract: ordinary overload and override rules select each cursor operation.
module spec.arrayiteration.methodoverloads

class Failure: Exception {}

interface IntCursor {
    func moveNext(): bool throws Failure
    func current(): int throws Failure
}

class OverloadedCursor: IntCursor {
    func moveNext(): bool throws Failure = false
    func moveNext(mode: int): bool = true
    func current(): int throws Failure = 0
    func current(index: int): int = index
}

func exactZeroArgumentMethodsSelected(source: OverloadedCursor): int throws Failure {
    var total = 0
    for val item in source { total += item }
    total
}

class AmbiguousCursorFactory {
    func iterator(): IntCursor = IntCursorImpl()
    func iterator(): StringCursor = StringCursorImpl()
}

func ambiguousReturnOverloadNeedsContext(source: AmbiguousCursorFactory): () {
    // Expected ambiguity: body usage cannot retroactively replace ordinary candidate resolution.
    for val item in source { consume(item) }
}

class GenericCursor<T> {
    func moveNext(): bool = false
    func current(): T = defaultValue()
}

class GenericSource<T> {
    val cursor: GenericCursor<T>
    func iterator(): GenericCursor<T> = cursor
}

func genericMethodSubstitution(source: GenericSource<String>): int {
    var total = 0
    for val item in source { total += item.length }
    total
}

open class BaseCursor {
    open func moveNext(): bool throws Failure = false
    open func current(): int throws Failure = 0
}

class DerivedCursor: BaseCursor {
    override func moveNext(): bool throws Failure = true
    override func current(): int throws Failure = 7
}

func virtualDispatchUsesStaticEffectContract(cursor: BaseCursor): int throws Failure {
    var total = 0
    for val item in cursor { total += item }
    total
}

class WidenedOverrideCursor: BaseCursor {
    // Expected override error: checked effects cannot be widened.
    override func current(): int throws Failure, OtherFailure = 0
}

func inaccessibleOverloadCannotBeSelected(source: PrivateCursorSource): () {
    // Expected visibility failure under ordinary member access rules.
    for val item in source { consume(item) }
}

class HiddenOverloadSource: PublicCursorSource {
    private func iterator(): PrivateCursor
}

func hiddenMemberFollowsHidingRules(source: HiddenOverloadSource): () {
    for val item in source { consume(item) }
}

func baseInterfaceCursorPreservesDeclaredEffects(source: BaseIterable<int>): () throws Failure {
    for val item in source { consume(item) }
}

func derivedCursorCannotInventCallSiteEffect(source: DerivedIterable<int>): () throws Failure {
    for val item in source { consume(item) }
}

func extensionLikeMethodIsNotImplicitCandidate(source: UnrelatedType): () {
    // Sol does not add global extension-method guessing as a special foreach fallback.
    for val item in source { consume(item) }
}

func overloadResolutionPrecedesEffectUnion(source: EffectOverloadSource): ()
    throws Failure {
    for val item in source { consume(item) }
}

func noUnionOfRejectedOverloads(source: EffectOverloadSource): ()
    throws Failure {
    for val item in source { consume(item) }
}

func exactCurrentDeclarationSetsItemType(source: CurrentOverloadSource): () {
    for val item in source { consume(item) }
}

func receiverTypeSubstitutionSetsCurrentType<T>(source: SourceFor<T>): () {
    for val item in source { consume(item) }
}

class IntCursorImpl: IntCursor {
    func moveNext(): bool throws Failure = false
    func current(): int throws Failure = 0
}

class StringCursorImpl: Cursor<String> {
    func moveNext(): bool = false
    func current(): String = ""
}

// `iterator`, `moveNext`, and `current` are resolved with the language's ordinary constructed
// member, override, access, and overload rules. The backend receives exact target IDs and effects.
