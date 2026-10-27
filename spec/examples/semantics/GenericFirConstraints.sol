// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative constraint and FIR-boundary cases; not every native expectation has been exercised.
module examples.semantics.generic_fir_constraints

// Constraint evidence is checked by declaration identity and by source type relations.
interface HasName {
    func name(): String
}

interface HasRank {
    func rank(): int
}

interface HasIdentity<T> {
    func identity(): T
}

interface HasComparable<T> {
    func compareTo(other: T): int
}

interface HasLabel: HasName {
    func label(): String
}

open class NamedBase: Object, HasName {
    override func name(): String = "base"
}

class NamedChild: NamedBase, HasRank {
    override func name(): String = "child"
    override func rank(): int = 1
}

class NamedPeer: NamedBase {
    override func name(): String = "peer"
}

class RankOnly: Object, HasRank {
    override func rank(): int = 2
}

class NamedRanked: Object, HasName, HasRank {
    override func name(): String = "both"
    override func rank(): int = 3
}

class NamedIdentity: Object, HasName, HasIdentity<NamedIdentity> {
    override func name(): String = "identity"
    override func identity(): NamedIdentity = this
}

class ComparableNamed: Object, HasName, HasComparable<ComparableNamed> {
    override func name(): String = "comparable"
    override func compareTo(other: ComparableNamed): int = 0
}

class ComparableIdentity: Object, HasComparable<ComparableIdentity>, HasIdentity<ComparableIdentity> {
    override func compareTo(other: ComparableIdentity): int = 0
    override func identity(): ComparableIdentity = this
}

class ComparableToNamedChild: Object, HasComparable<NamedChild> {
    override func compareTo(other: NamedChild): int = 0
}

class IdentityForNamedChild: Object, HasIdentity<NamedChild> {
    override func identity(): NamedChild = unreachable()
}

ref struct BorrowedName: HasName {
    override func name(): String = "borrowed"
}

class Unrelated { }

func <T> identity(value: T): T = value

func <T: HasName> inlineNamed(value: T): T = value

func <T> whereNamed(value: T): T where T: HasName = value

func <T: HasName> inlineAndWhereNamed(value: T): T where T: HasRank = value

func <T> namedRanked(value: T): T where T: HasName, T: HasRank = value

func <T: HasName> namedAndComparable(value: T): T where T: HasComparable<T> = value

func <T: HasIdentity<T>> recursiveIdentity(value: T): T = value

func <T: Object> referenceIdentity(value: T): T = value

func <T> scopedIdentity(value: T): T where T: allows ref struct = value

func <T: HasName> namedBody(value: T): String = value.name()

func <T: HasName> namedScopedIdentity(value: T): T where T: allows ref struct = value

func <T> forwardingIdentity(value: T): T = identity<T>(value)

func <T: HasName> forwardingNamed(value: T): T where T: HasRank = inlineNamed(value)

func <T: HasName> useNameOrRank(value: T): String = value.name()

func <T, U> pairwise(a: T, b: U): T = a

func <T> explicitPairwise(a: T, b: T): T = a

func <T> expectedResult(): T = unreachable()

func <T: HasName> expectedNamed(): T = unreachable()

func <T: HasRank> expectedRanked(): T = unreachable()

func <T: HasName> traitResult(): T = unreachable()

func <T: HasRank> traitResult(): T = unreachable()

func <T> shadowedInner(value: T): T = value

func <T> shadowedOuter(value: T): T = shadowedInner<T>(value)

func <T: HasName> constrainedCapture(value: T): () where T: allows ref struct {
    val callback = { value.name() }
}

func <T> constrainArray(values: []T): []T where T: allows ref struct = values

func <T> unconstrainedArray(values: []T): []T = values

func <T: HasName> chooseByName(value: T): String = "name"

func <T: HasRank> chooseByRank(value: T): String = "rank"

func <T: HasName> selectTrait(value: T): String = "name"

func <T: HasRank> selectTrait(value: T): String = "rank"

func <T: HasName> pick(value: T): String = "generic"

func pick(value: NamedChild): String = "concrete"

func <T: Object> onlyReference(value: T): T = value

func <T: HasName, U: HasRank> twoIndependentBounds(left: T, right: U): T = left

func <T, U> interdependent(left: T, right: U): T
    where T: HasComparable<U>, U: HasIdentity<T> = left

func <T> returnAndParameterBound(value: T): T where T: HasName = value

func <T> arrayElementBound(values: []T): []T where T: HasName = values

func <T: HasName> nullableBound(value: T?): T? = value

func <T> constructedBound(value: T): T where T: HasIdentity<T> = value

func <T> ordinaryBound(value: T): T where T: HasName, T: HasRank = value

// 1. Inline nominal bounds
// ------------------------
//
// The inline form associates a single bound with the exact function-owned parameter. An argument
// that implements the bound is source-applicable. These user-defined nominal types remain outside
// the first native generic ABI, so their FIR disposition is separate from source applicability.

func inlineAcceptsChild(value: NamedChild): NamedChild {
    // expect: inline.child | Source:Applicable | type-arguments=[NamedChild] | constraint=HasName:satisfied | FIR:Rejected(unsupported-nominal-argument)
    return inlineNamed(value)
}

func inlineAcceptsPeer(value: NamedPeer): NamedPeer {
    // expect: inline.peer | Source:Applicable | type-arguments=[NamedPeer] | constraint=HasName:satisfied | FIR:Rejected(unsupported-nominal-argument)
    return inlineNamed(value)
}

func inlineRejectsRankOnly(value: RankOnly): RankOnly {
    // expect: inline.missing-bound | Source:Rejected(constraint-HasName) | FIR:NoInstance
    return inlineNamed(value)
}

func inlineRejectsUnrelated(value: Unrelated): Unrelated {
    // expect: inline.unrelated | Source:Rejected(constraint-HasName) | FIR:NoInstance
    return inlineNamed(value)
}

func inlineStringDoesNotAcquireName(value: String): String {
    // expect: inline.string | Source:Rejected(constraint-HasName) | FIR:NoInstance
    return inlineNamed(value)
}

func inlineSameSpellingFirst(value: NamedChild): NamedChild {
    // expect: inline.owner-first | Source:Applicable | generic-owner=inlineNamed.ordinal0 | FIR:Rejected(unsupported-nominal-argument)
    return inlineNamed(value)
}

func inlineSameSpellingSecond(value: NamedPeer): NamedPeer {
    // expect: inline.owner-second | Source:Applicable | generic-owner=inlineNamed.ordinal0 | FIR:Rejected(unsupported-nominal-argument)
    return inlineNamed(value)
}

func inlineBoundViaBase(value: NamedChild): NamedBase {
    // expect: inline.inherited-interface | Source:Applicable | proof=NamedChild->NamedBase->HasName | FIR:Rejected(unsupported-nominal-argument)
    return inlineNamed(value)
}

func inlineBoundNoStructuralFallback(value: RankOnly): RankOnly {
    // expect: inline.no-structural-match | Source:Rejected(constraint-HasName) | same-spelling-method=none | FIR:NoInstance
    return inlineNamed(value)
}

func inlineBoundRequiresNominalDeclaration(value: NamedRanked): NamedRanked {
    // expect: inline.nominal-membership | Source:Applicable | method-name=not-used-as-proof | FIR:Rejected(unsupported-nominal-argument)
    return inlineNamed(value)
}

// 2. `where` bounds
// -----------------

func whereAcceptsChild(value: NamedChild): NamedChild {
    // expect: where.child | Source:Applicable | type-arguments=[NamedChild] | constraint=HasName:satisfied | FIR:Rejected(unsupported-nominal-argument)
    return whereNamed(value)
}

func whereRejectsRankOnly(value: RankOnly): RankOnly {
    // expect: where.missing-bound | Source:Rejected(constraint-HasName) | FIR:NoInstance
    return whereNamed(value)
}

func whereRejectsUnrelated(value: Unrelated): Unrelated {
    // expect: where.unrelated | Source:Rejected(constraint-HasName) | FIR:NoInstance
    return whereNamed(value)
}

func whereExplicitStillChecks(value: RankOnly): RankOnly {
    // expect: where.explicit-fails | Source:Rejected(constraint-HasName) | type-arguments=[RankOnly] | FIR:NoInstance
    return whereNamed<RankOnly>(value)
}

func whereExplicitPasses(value: NamedChild): NamedChild {
    // expect: where.explicit-passes | Source:Applicable | type-arguments=[NamedChild] | FIR:Rejected(unsupported-nominal-argument)
    return whereNamed<NamedChild>(value)
}

func whereConstructorResult(value: NamedChild): NamedChild {
    // expect: where.argument-type | Source:Applicable | evidence=argument-exact | FIR:Rejected(unsupported-nominal-argument)
    return whereNamed(value)
}

func whereResultContext(value: NamedChild): NamedChild {
    // expect: where.expected-result | Source:Applicable | type-arguments=[NamedChild] | FIR:Rejected(unsupported-nominal-argument)
    val result: NamedChild = whereNamed(value)
    return result
}

func whereNoExpectedType(value: NamedChild): NamedChild {
    // expect: where.no-context | Source:Applicable | evidence=argument | FIR:Rejected(unsupported-nominal-argument)
    return whereNamed(value)
}

func whereInputProvidesEvidence(value: NamedChild): NamedChild {
    // expect: where.defaults-not-evidence | Source:Applicable | type-arguments=[NamedChild] | evidence=explicit-argument
    return whereNamed(value)
}

func whereBoundUsesExactOwner(value: NamedChild): NamedChild {
    // expect: where.owner-identity | Source:Applicable | bound-owner=whereNamed.ordinal0 | FIR:Rejected(unsupported-nominal-argument)
    return whereNamed(value)
}

// 3. Conjunction of multiple bounds
// ---------------------------------

func bothBoundsAccept(value: NamedRanked): NamedRanked {
    // expect: bounds.both | Source:Applicable | constraints=[HasName,HasRank]:satisfied | FIR:Rejected(unsupported-nominal-argument)
    return inlineAndWhereNamed(value)
}

func bothBoundsRejectNameOnly(value: NamedPeer): NamedPeer {
    // expect: bounds.missing-rank | Source:Rejected(constraint-HasRank) | FIR:NoInstance
    return inlineAndWhereNamed(value)
}

func bothBoundsRejectRankOnly(value: RankOnly): RankOnly {
    // expect: bounds.missing-name | Source:Rejected(constraint-HasName) | FIR:NoInstance
    return inlineAndWhereNamed(value)
}

func whereConjunctionPasses(value: NamedRanked): NamedRanked {
    // expect: bounds.two-where-clauses | Source:Applicable | constraints=[HasName,HasRank]:satisfied | FIR:Rejected(unsupported-nominal-argument)
    return namedRanked(value)
}

func whereConjunctionRejectsFirst(value: RankOnly): RankOnly {
    // expect: bounds.two-where-first-fails | Source:Rejected(constraint-HasName) | FIR:NoInstance
    return namedRanked(value)
}

func whereConjunctionRejectsSecond(value: NamedPeer): NamedPeer {
    // expect: bounds.two-where-second-fails | Source:Rejected(constraint-HasRank) | FIR:NoInstance
    return namedRanked(value)
}

func twoIndependentParametersPass(left: NamedChild, right: RankOnly): NamedChild {
    // expect: bounds.independent-parameters | Source:Applicable | type-arguments=[NamedChild,RankOnly] | FIR:Rejected(unsupported-nominal-argument)
    return twoIndependentBounds(left, right)
}

func twoIndependentParametersRejectLeft(left: RankOnly, right: RankOnly): RankOnly {
    // expect: bounds.independent-left-fails | Source:Rejected(constraint-HasName) | FIR:NoInstance
    return twoIndependentBounds(left, right)
}

func twoIndependentParametersRejectRight(left: NamedPeer, right: NamedPeer): NamedPeer {
    // expect: bounds.independent-right-fails | Source:Rejected(constraint-HasRank) | FIR:NoInstance
    return twoIndependentBounds(left, right)
}

func <T> duplicateNameBound(value: T): T where T: HasName, T: HasName = value

func duplicateEquivalentBound(value: NamedChild): NamedChild {
    // expect: bounds.duplicate-is-not-weaker | Source:Applicable | duplicate=HasName | FIR:Rejected(unsupported-nominal-argument)
    return duplicateNameBound(value)
}

// 4. Inherited and constructed nominal paths
// ------------------------------------------

func inheritedNamedChild(value: NamedChild): NamedChild {
    // expect: inheritance.base-interface | Source:Applicable | path=[NamedChild,NamedBase,HasName] | FIR:Rejected(unsupported-nominal-argument)
    return inlineNamed(value)
}

func directNamedRanked(value: NamedRanked): NamedRanked {
    // expect: inheritance.direct-two-interfaces | Source:Applicable | paths=[HasName,HasRank] | FIR:Rejected(unsupported-nominal-argument)
    return namedRanked(value)
}

func constructedIdentityBound(value: NamedIdentity): NamedIdentity {
    // expect: inheritance.constructed-self | Source:Applicable | bound=HasIdentity<NamedIdentity> | FIR:Rejected(unsupported-nominal-argument)
    return constructedBound(value)
}

func constructedIdentityWrong(value: NamedChild): NamedChild {
    // expect: inheritance.constructed-argument-mismatch | Source:Rejected(constraint-HasIdentity<NamedChild>) | FIR:NoInstance
    return constructedBound(value)
}

func comparableSelfPasses(value: ComparableNamed): ComparableNamed {
    // expect: inheritance.dependent-self-bound | Source:Applicable | bound=HasComparable<ComparableNamed> | FIR:Rejected(unsupported-nominal-argument)
    return namedAndComparable(value)
}

func comparableSelfRejectsOther(value: NamedChild): NamedChild {
    // expect: inheritance.dependent-bound-missing | Source:Rejected(constraint-HasComparable<NamedChild>) | FIR:NoInstance
    return namedAndComparable(value)
}

func inheritedNameCall(value: NamedChild): String {
    // expect: inheritance.member-through-bound | Source:Applicable | selected-member=HasName.name | FIR:Rejected(per-instance-member-rebind)
    return namedBody(value)
}

func inheritedNameCallPeer(value: NamedPeer): String {
    // expect: inheritance.member-through-bound-peer | Source:Applicable | selected-member=HasName.name | FIR:Rejected(per-instance-member-rebind)
    return namedBody(value)
}

func noStructuralInference(value: RankOnly): String {
    // expect: inheritance.no-structural-name | Source:Rejected(constraint-HasName) | FIR:NoInstance
    return namedBody(value)
}

// 5. Interdependent bounds
// ------------------------

func interdependentSelfType(left: ComparableIdentity, right: ComparableIdentity): ComparableIdentity {
    // expect: dependent.self-pair | Source:Applicable | type-arguments=[ComparableIdentity,ComparableIdentity] | constraints:satisfied | FIR:Rejected(unsupported-nominal-argument)
    return interdependent(left, right)
}

func interdependentWrongRight(left: ComparableToNamedChild, right: NamedChild): ComparableToNamedChild {
    // expect: dependent.right-identity-mismatch | Source:Rejected(constraint-HasIdentity<ComparableToNamedChild>) | first-bound=satisfied | FIR:NoInstance
    return interdependent(left, right)
}

func interdependentWrongLeft(left: NamedChild, right: IdentityForNamedChild): NamedChild {
    // expect: dependent.left-comparable-mismatch | Source:Rejected(constraint-HasComparable<IdentityForNamedChild>) | second-bound=satisfied | FIR:NoInstance
    return interdependent(left, right)
}

func dependentBoundsExplicit(left: ComparableIdentity, right: ComparableIdentity): ComparableIdentity {
    // expect: dependent.explicit-types | Source:Applicable | type-arguments=[ComparableIdentity,ComparableIdentity] | constraints:satisfied
    return interdependent<ComparableIdentity, ComparableIdentity>(left, right)
}

func dependentBoundsWrongExplicit(left: ComparableIdentity, right: NamedChild): ComparableIdentity {
    // expect: dependent.explicit-fails | Source:Rejected(constraint) | type-arguments=[ComparableIdentity,NamedChild] | FIR:NoInstance
    return interdependent<ComparableIdentity, NamedChild>(left, right)
}

// 6. `Object` and the no-boxing constraint rule
// --------------------------------------------

func objectConstraintAcceptsString(value: String): String {
    // expect: object.string | Source:Applicable | type-arguments=[String] | boxing=none | FIR:Eligible
    return referenceIdentity(value)
}

func objectConstraintRejectsInt(value: int): int {
    // expect: object.primitive | Source:Rejected(constraint-Object-no-boxing) | FIR:NoInstance
    return referenceIdentity(value)
}

func objectConstraintRejectsByte(value: byte): byte {
    // expect: object.unsigned-primitive | Source:Rejected(constraint-Object-no-boxing) | FIR:NoInstance
    return referenceIdentity(value)
}

func unconstrainedIntIdentity(value: int): int {
    // expect: object.unconstrained-control | Source:Applicable | type-arguments=[int] | FIR:Eligible
    return identity(value)
}

func unconstrainedStringIdentity(value: String): String {
    // expect: object.string-control | Source:Applicable | type-arguments=[String] | FIR:Eligible
    return identity(value)
}

func whereObjectAcceptsString(value: String): String {
    // expect: object.where-string | Source:Applicable | constraint=Object:satisfied | FIR:Eligible
    return onlyReference(value)
}

func whereObjectRejectsInt(value: int): int {
    // expect: object.where-int | Source:Rejected(constraint-Object-no-boxing) | FIR:NoInstance
    return onlyReference(value)
}

func nominalSubclassStillNativeRejected(value: NamedChild): NamedChild {
    // expect: object.nominal-subclass | Source:Applicable | constraint=Object:satisfied | FIR:Rejected(unsupported-nominal-argument)
    return onlyReference(value)
}

func referenceTypeExactArgument(value: String): String {
    // expect: object.no-widening | Source:Applicable | type-arguments=[String] | result=String | FIR:Eligible
    return referenceIdentity<String>(value)
}

func referenceConstraintDoesNotBoxResult(value: int): int {
    // expect: object.no-result-boxing | Source:Rejected(constraint-Object-no-boxing) | FIR:NoInstance
    return referenceIdentity<int>(value)
}

// 7. Ref-like anti-constraints
// ----------------------------

func refLikeAllowedByAntiConstraint(value: BorrowedName): BorrowedName {
    // expect: ref-like.allowed | Source:Applicable | anti-constraint=allows-ref-struct | FIR:Rejected(ref-like-native-abi)
    return scopedIdentity(value)
}

func refLikeRequiresAntiConstraint(value: BorrowedName): BorrowedName {
    // expect: ref-like.missing-anti-constraint | Source:Rejected(requires-allows-ref-struct) | FIR:NoInstance
    return identity(value)
}

func refLikeAlsoNeedsInterface(value: BorrowedName): BorrowedName {
    // expect: ref-like.interface-and-anti | Source:Applicable | constraints=[HasName,allows-ref-struct]:satisfied | FIR:Rejected(ref-like-native-abi)
    return namedScopedIdentity(value)
}

func refLikeArrayRejectedByLanguage(value: []BorrowedName): []BorrowedName {
    // expect: ref-like.array-element | Source:Rejected(ref-like-array-element) | FIR:NoInstance
    return constrainArray(value)
}

func antiConstraintDoesNotCreateNameMember(value: BorrowedName): String {
    // expect: ref-like.no-members-from-anti-constraint | Source:Applicable | member-source=HasName | FIR:Rejected(per-instance-member-rebind)
    return namedBody(value)
}

func antiConstraintRejectsHeapCapture(value: BorrowedName): () {
    // expect: ref-like.capture | Source:Rejected(lifetime-escape) | constraint=allows-ref-struct
    constrainedCapture(value)
}

func antiConstraintDoesNotAllowOrdinaryArray(value: []int): []int {
    // expect: ref-like.anti-does-not-widen-array-abi | Source:Applicable | type-arguments=[int] | FIR:Eligible
    return constrainArray(value)
}

func antiConstraintDoesNotEraseBodySafety(value: BorrowedName): () {
    // expect: ref-like.body-storage | Source:Rejected(ref-like-storage) | FIR:NoInstance
    constrainedCapture(value)
}

func refLikeConstraintOnOrdinaryType(value: NamedChild): NamedChild {
    // expect: ref-like.ordinary-type-allowed | Source:Applicable | anti-constraint=permission-only | FIR:Rejected(unsupported-nominal-argument)
    return scopedIdentity(value)
}


// See [GenericFirConstraintsAdvanced.sol](GenericFirConstraintsAdvanced.sol) for the remaining contract.
