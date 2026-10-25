// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: constructor delegation is explicit, ordered, and acyclic.
module spec.construction.chaining

class ChainFailure: Exception {}
class BaseFailure: Exception {}
class BodyFailure: Exception {}

func record(value: int): () {}
func mark(value: int): int { record(value); value }

// The primary constructor defines the single initialization path.
class Primary(val value: int) {
    init { record(1) }
}

func callPrimary(): Primary = Primary(2)

// A secondary constructor delegates to the primary exactly once.
class Secondary(val value: int) {
    init { record(10) }
    constructor(): this(mark(11)) { record(12) }
}

func callSecondary(): Secondary = Secondary()

// The delegated constructor's chain completes before the secondary body.
class MultiSecondary(val first: int, val second: int) {
    init { record(20) }
    constructor(value: int): this(mark(21), mark(22)) {
        record(23)
    }
    constructor(): this(mark(24)) {
        record(25)
    }
}

func callMultiSecondary(): MultiSecondary = MultiSecondary()

// A class with a base uses one selected explicit base-constructor call.
open class RootBase(val root: int) {
    init { record(30) }
}

class OneBase: RootBase {
    val child: int = mark(32)
    constructor(): super(mark(31)) { record(33) }
}

func callOneBase(): OneBase = OneBase()

// Base completion precedes field initialization and constructor body.
class OrderingBase(val code: int) {
    init { record(40) }
}

class OrderingDerived: OrderingBase {
    val value: int = mark(42)
    init { record(43) }
    constructor(): super(mark(41)) { record(44) }
}

// A derived secondary constructor delegates through the derived primary chain.
class DerivedSecondary: OrderingBase {
    val value: int = mark(50)
    constructor(code: int): super(mark(code)) { record(51) }
    constructor(): this(mark(52)) { record(53) }
}

// The secondary this-chain cannot also invoke the base constructor directly.
// Negative design shape: `constructor(): this(1), super(2) {}` is rejected.
func multipleDelegationTargetIsNotConstructible(): () {}

// A constructor chain cycle is a declaration error, not a runtime recursion.
class CycleA(val value: int) {
    // Negative design shape: constructor(flag: bool): this() {}
    // Negative design shape: constructor(): this(true) {}
}

// A this-delegation target must resolve to one constructor.
class AmbiguousThisTarget(val value: int) {
    constructor(value: short): this(value.to_int()) {}
    constructor(value: long): this(value.to_int()) {}
    constructor(): this(1) {}
}

// If overload ranking cannot choose one target, declaration order is not a tie-breaker.
func ambiguousDelegationMustFail(): () {}

// A base type with only an argument-taking constructor has no implicit empty call.
class RequiredBase(val code: int)

class MissingBaseCall: RequiredBase {
    // Negative design shape: no accessible zero-argument base constructor exists.
    constructor() { record(60) }
}

// An explicit base call uses normal source argument order and checked effects.
class EffectfulBase(val code: int) throws BaseFailure {
    init { verifyBase(code) }
}

func verifyBase(code: int): () throws BaseFailure {
    if code < 0 { throw BaseFailure() }
}

class DerivedFromEffectfulBase: EffectfulBase {
    val value: int = mark(62)
    constructor(code: int): super(mark(code)) throws BaseFailure {
        record(63)
    }
}

func effectfulBaseCallSite(): DerivedFromEffectfulBase throws BaseFailure {
    DerivedFromEffectfulBase(61)
}

// The derived contract cannot omit a checked base effect.
class MissingBaseEffectBudget: EffectfulBase {
    constructor(code: int): super(code) {
        record(70)
    }
}

// A base failure prevents every derived initializer and body statement.
func baseFailureSkipsDerivedWork(): DerivedFromEffectfulBase throws BaseFailure {
    DerivedFromEffectfulBase(-1)
}

// A field failure after successful base completion preserves base side effects but prevents
// publication of the derived instance.
class DerivedFieldFailure: EffectfulBase {
    val value: int = checkedField()
    constructor(code: int): super(code) throws BaseFailure, BodyFailure {
        record(80)
    }
}

func checkedField(): int throws BodyFailure { throw BodyFailure() }

// A constructor-body failure occurs after required initialization and still prevents return.
class BodyFailureAfterInit {
    val value: int = mark(90)
    constructor() throws BodyFailure {
        throw BodyFailure()
    }
}

// A secondary body failure does not re-run the delegated primary constructor.
class SecondaryBodyFailure(val value: int) {
    init { record(100) }
    constructor(value: int, fail: bool): this(mark(value)) throws BodyFailure {
        if fail { throw BodyFailure() }
    }
}

func secondaryBodyThrows(): SecondaryBodyFailure throws BodyFailure {
    SecondaryBodyFailure(101, true)
}

// Explicit arguments to a secondary constructor are evaluated before the delegation call.
func secondaryArgumentOrder(): SecondaryBodyFailure throws BodyFailure {
    SecondaryBodyFailure(mark(110), false)
}

// A selected default for the target constructor is evaluated before its body.
class BaseWithDefault(val code: int = defaultCode()) {
    init { record(120) }
}

func defaultCode(): int { record(121); 122 }

class DerivedUsingBaseDefault: BaseWithDefault {
    constructor(): super() { record(123) }
}

// Explicitly supplying the base argument suppresses that default.
class DerivedSupplyingBaseArgument: BaseWithDefault {
    constructor(): super(mark(130)) { record(131) }
}

// A class constructor can delegate through a second constructor, but the base chain runs once.
open class RootOnce {
    constructor() { record(140) }
}

class MiddleOnce: RootOnce {
    constructor(): super() { record(141) }
}

class LeafOnce: MiddleOnce {
    constructor(): super() { record(142) }
}

func callLeafOnce(): LeafOnce = LeafOnce()

// A repeated base construction is never implied by multiple this-delegation edges.
class DerivedDelegationPath: RootOnce {
    val value: int
    constructor(value: int): super() { this.value = value }
    constructor(): this(mark(150)) {}
}

// Base and this chains are both explicit graph edges, with acyclic resolution required.
func oneBaseInvocationInChain(): DerivedDelegationPath = DerivedDelegationPath()

// A root class does not synthesize a hidden external base constructor call.
class RootWithoutUserBase(val value: int) {
    constructor(value: int) { record(value) }
}

// Struct constructor chaining follows value initialization rules and has no class base.
struct ValueChain {
    val left: int
    val right: int
    constructor(left: int, right: int) {
        this.left = left
        this.right = right
    }
    constructor(value: int): this(value, value) {}
}

func valueChainCall(): ValueChain = ValueChain(151)

// A ref struct remains byref-like across constructor delegation.
ref struct BorrowedChain {
    val value: int
    constructor(value: int) { this.value = value }
    constructor(): this(mark(160)) {}
}

// Constructor chain metadata includes the selected target identity and argument mapping.
func inspectChainIdentity(value: Secondary): int = value.value

// A constructor cannot run a field initializer before the base call.
class BaseMustPrecedeFields: OrderingBase {
    val value: int = mark(170)
    constructor(): super(mark(171)) { record(172) }
}

// Invalid source form: base invocation after the body is not a delegation expression.
class LateBaseCallShape: OrderingBase {
    // Negative design shape: constructor() { super(); record(180) }
}

// Invalid source form: a constructor must not call an unrelated base more than once.
class DuplicateBaseCallShape: OrderingBase {
    // Negative design shape: constructor(): super(181): super(182) {}
}

// A constructor's parameter mapping uses normal named/default rules.
class NamedBase(val left: int, val right: int = defaultCode()) {
    init { record(left + right) }
}

class NamedDerived: NamedBase {
    constructor(): super(right = mark(190), left = mark(191)) { record(192) }
}

// The syntactic argument order determines the two mark calls above.
func namedBaseArgumentEvaluation(): NamedDerived = NamedDerived()

// A checked effect from a this target remains in the outer constructor contract.
class EffectfulTarget(val value: int) throws ChainFailure {
    init { if value < 0 { throw ChainFailure() } }
}

class DelegatesToEffectfulTarget: EffectfulTarget {
    constructor(value: int): super(value) throws ChainFailure {}
    constructor(): this(mark(200)) throws ChainFailure {}
}

func effectfulTargetCall(): DelegatesToEffectfulTarget throws ChainFailure {
    DelegatesToEffectfulTarget()
}

// Constructor overloads that differ only by throws do not create a selectable effect overload.
func throwsIsNotConstructorIdentity(): () {}

// A failed argument prevents entry into both this and base constructor bodies.
func failureBeforeChainEntry(): DerivedFromEffectfulBase throws BaseFailure {
    DerivedFromEffectfulBase(code = verifyAndReturn())
}

func verifyAndReturn(): int throws BaseFailure {
    throw BaseFailure()
}

// A failed this-delegation argument prevents target initialization and secondary body execution.
class FailureBeforeThisTarget(val value: int) {
    constructor(value: int, flag: bool): this(chainArgument(flag)) { record(210) }
}

func chainArgument(flag: bool): int throws ChainFailure {
    if flag { throw ChainFailure() }
    1
}

// A chain failure is checked at the allocation site, not delayed until first field access.
func catchChainFailure(flag: bool): () {
    try {
        FailureBeforeThisTarget(1, flag)
    } catch (error: ChainFailure) {
        record(220)
    }
}

// Specification note: exactly one target is selected for every constructor delegation edge.
// Specification note: the inheritance chain is established before derived fields and body.
// Specification note: failed delegation prevents later constructor work and publication.
// Specification note: constructor effect budgets include transitive selected base/this targets.
// Specification note: source or manifest order is never a constructor-target tie-breaker.
// Specification note: arbitrary foreign exceptions cannot jump across a partially built chain.
