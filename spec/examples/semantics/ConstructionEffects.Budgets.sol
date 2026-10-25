// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: implicit construction operations contribute checked effects.
module spec.construction.budgets

class ReadFailure: Exception {}
class NetworkFailure: Exception {}
class TimeoutFailure: NetworkFailure {}
class ParseFailure: Exception {}
class BoundsFailure: RuntimeException {}

func record(value: int): () {}

// A primary constructor publishes its checked budget at the source construction boundary.
class CheckedResource() throws ReadFailure {}

func constructWithExactBudget(): CheckedResource throws ReadFailure {
    CheckedResource()
}

// A supertype budget covers the constructor's subtype effect.
func constructWithBaseBudget(): CheckedResource throws Exception {
    CheckedResource()
}

// Negative: an unrelated checked type does not cover the constructor effect.
func constructWithSiblingBudget(): CheckedResource throws ParseFailure {
    CheckedResource()
}

// Negative: no throws clause does not mean the constructor is pure.
func constructWithoutBudget(): CheckedResource {
    CheckedResource()
}

// Unchecked RuntimeException effects do not require checked budget entries.
class RuntimeFailingResource() {
    init { throw BoundsFailure() }
}

func constructRuntimeFailure(): RuntimeFailingResource = RuntimeFailingResource()

// Declaring an unchecked effect does not convert a checked effect into an unchecked one.
func checkedStillNeedsItsBudget(): CheckedResource throws BoundsFailure {
    CheckedResource()
}

// A constructor's field initializer contributes to the same constructor contract.
func loadRemote(): int throws ReadFailure { throw ReadFailure() }

class FieldEffectResource {
    val value: int = loadRemote()
    constructor() throws ReadFailure {}
}

func constructFieldEffect(): FieldEffectResource throws ReadFailure {
    FieldEffectResource()
}

// An init block adds its checked effect to the constructor's declared set.
func verifyRemote(): () throws NetworkFailure {}

class InitBlockEffectResource {
    init throws NetworkFailure { verifyRemote() }
    constructor() throws NetworkFailure {}
}

func constructInitBlockEffect(): InitBlockEffectResource throws NetworkFailure {
    InitBlockEffectResource()
}

// Negative: an initializer's effect cannot be omitted from the constructor contract.
class MissingInitBlockBudget {
    init throws NetworkFailure { verifyRemote() }
    constructor() {}
}

// The selected base constructor's effects flow through the derived constructor.
open class CheckedBase() throws NetworkFailure {}

class CheckedDerived: CheckedBase {
    constructor() throws NetworkFailure: super() {}
}

func constructCheckedDerived(): CheckedDerived throws NetworkFailure {
    CheckedDerived()
}

// Negative: a derived declaration cannot erase the selected base constructor effect.
class DerivedDropsBaseEffect: CheckedBase {
    constructor(): super() {}
}

// A wider base budget can be handled by a declared supertype in the derived contract.
open class BroadCheckedBase() throws Throwable {}

class BroadCheckedDerived: BroadCheckedBase {
    constructor() throws Exception: super() {}
}

// Negative: Exception does not cover a possible direct Throwable outside its subtree.
func broadDerivedCall(): BroadCheckedDerived throws Exception {
    BroadCheckedDerived()
}

// The explicit argument effect contributes to construction even with a pure constructor.
class PureShell(val value: int)

func effectfulArgument(): int throws ReadFailure { throw ReadFailure() }

func argumentEffectAtCallSite(): PureShell throws ReadFailure {
    PureShell(effectfulArgument())
}

// A failure in an earlier argument prevents later argument expressions from running.
func sourceOrderedArgumentEffects(): PureShell throws ReadFailure, ParseFailure {
    PureShell(left = effectfulArgument(), right = parseArgument())
}

class TwoValues(val left: int, val right: int)

func parseArgument(): int throws ParseFailure { throw ParseFailure() }

// If the first argument fails, ParseFailure is not part of that particular execution path.
func firstArgumentFails(): TwoValues throws ReadFailure {
    TwoValues(left = effectfulArgument(), right = parseArgument())
}

// If the first argument succeeds, the second argument's effect remains possible.
func secondArgumentMayFail(): TwoValues throws ParseFailure {
    TwoValues(left = 1, right = parseArgument())
}

// Omitted constructor defaults contribute only when selected.
class DefaultedValues(val left: int, val right: int = effectfulDefault())

func effectfulDefault(): int throws NetworkFailure { 1 }

func omittedDefaultBudget(): DefaultedValues throws NetworkFailure {
    DefaultedValues(1)
}

// An explicit right argument suppresses that default's effect.
func explicitDefaultSuppression(): DefaultedValues {
    DefaultedValues(left = 1, right = 2)
}

// A default for one parameter does not suppress an unrelated omitted default.
class TwoDefaults(val first: int = pureDefault(), val second: int = effectfulDefault())

func pureDefault(): int = 0

func selectedOmittedDefaultOnly(): TwoDefaults throws NetworkFailure {
    TwoDefaults(first = 1)
}

// All omitted default expressions evaluate in declaration order after explicit expressions.
func bothDefaultsContribute(): TwoDefaults throws NetworkFailure {
    TwoDefaults()
}

// The selected constructor, not every candidate, determines the effect budget.
class OverloadedConstruction {
    val value: int
    constructor(value: int) { this.value = value }
    constructor(value: String) throws ReadFailure { this.value = loadRemote() }
}

func selectPureOverload(): OverloadedConstruction {
    OverloadedConstruction(2)
}

func selectCheckedOverload(): OverloadedConstruction throws ReadFailure {
    OverloadedConstruction("remote")
}

// Effects from a rejected overload do not leak into the selected pure constructor call.
func rejectedCandidateEffectsAreDiscarded(): OverloadedConstruction {
    OverloadedConstruction(3)
}

// Constructor `throws` lists do not participate in overload identity.
class ThrowsOnlyCollision {
    constructor(value: int) {}
    // Negative: same signature with another throws list is a duplicate, not an overload.
    // constructor(value: int) throws ReadFailure {}
}

// Catching a subtype removes only effects covered by that subtype.
func catchNetworkOnly(): CheckedDerived throws NetworkFailure {
    try {
        CheckedDerived()
    } catch (error: TimeoutFailure) {
        CheckedDerived()
    }
}

// Catching Exception does not erase a direct Throwable construction effect.
func directThrowableEffect(): BroadCheckedDerived throws Throwable {
    try {
        BroadCheckedDerived()
    } catch (error: Exception) {
        throw error
    }
}

// A catch body introduces its own construction effects.
func handlerConstructionEffect(): () throws ReadFailure {
    try {
        CheckedResource()
    } catch (error: ReadFailure) {
        OverloadedConstruction("handler")
    }
}

// A handler that fully handles the selected constructor effect has no residual effect.
func handlerRemovesConstructorEffect(): () {
    try {
        CheckedResource()
    } catch (error: ReadFailure) {
        record(10)
    }
}

// Cleanup inside construction handling contributes after incoming effects are caught.
func catchThenCleanupEffect(): () throws NetworkFailure {
    try {
        CheckedResource()
    } catch (error: ReadFailure) {
        defer { verifyRemote() }
        record(11)
    }
}

// A cleanup failure can become the escaping primary on the caught path.
func caughtConstructionThenFailCleanup(): () throws NetworkFailure {
    try {
        CheckedResource()
    } catch (error: ReadFailure) {
        defer { verifyRemote() }
    }
}

// Constructor effects remain on the API even after an instance was created elsewhere.
func everyCallSiteChecksConstruction(): CheckedResource throws ReadFailure {
    CheckedResource()
}

// Class type metadata is not proof that construction is effect-free.
func valueUseDoesNotRepeatConstructor(): int throws ReadFailure {
    val resource = CheckedResource()
    1
}

// Once a reference is available, ordinary member access uses that member's own effects.
class ReadableResource() throws ReadFailure {
    val value: int { get throws NetworkFailure = loadNetwork() }
}

func loadNetwork(): int throws NetworkFailure { 0 }

func constructionAndReadEffects(): int throws ReadFailure, NetworkFailure {
    ReadableResource().value
}

// A property read effect is not hidden by the constructor budget.
func missingReadEffect(): int throws ReadFailure {
    ReadableResource().value
}

// Object initialization effects are checked at every possible access site.
object LazyResource {
    val current: int = loadRemote()
}

func objectInitializationEffect(): int throws ReadFailure {
    LazyResource.current
}

// The effect remains declared even if the value is already Ready at runtime.
func cachedObjectReadStillChecked(): int throws ReadFailure {
    LazyResource.current
}

// Initialization failure is cached and is not treated as a successful read later.
func failedObjectReadContract(): int throws ReadFailure {
    LazyResource.current
}

// Accessing an unrelated member does not introduce this object's initializer effect.
object PureResource {
    val current: int = 1
}

func unrelatedObjectHasNoReadFailure(): int = PureResource.current

// Metadata without a constructor effect list is incomplete, not evidence of purity.
func legacyMetadataCannotDropEffect(): CheckedResource throws ReadFailure {
    CheckedResource()
}

// Generic substitution preserves the selected constructor effect type.
func <E: Throwable> constructAndThrow(value: E): ! throws E {
    throw value
}

func genericConstructorEffect(): ! throws ReadFailure {
    constructAndThrow(ReadFailure())
}

// A generic effect variable must be substituted before comparing the caller's budget.
func genericEffectNeedsCoverage(): ! throws ParseFailure {
    constructAndThrow(ReadFailure())
}

// Overrides do not apply to constructor selection; base effects remain statically declared.
open class EffectBase() throws ReadFailure {}

class EffectDerived: EffectBase {
    constructor() throws ReadFailure: super() {}
}

func staticConstructorContract(): EffectDerived throws ReadFailure {
    EffectDerived()
}

// Foreign constructors must adapt external failures at the boundary before they become Sol effects.
func foreignConstructionAdapter(): PureShell throws ReadFailure {
    PureShell(adaptForeignValue())
}

func adaptForeignValue(): int throws ReadFailure { 1 }

// Specification note: effect identity is nominal and includes the resolved module/type symbol.
// Specification note: every selected argument, default, constructor, and initializer edge contributes.
// Specification note: unchecked failures remain runtime-visible and participate in cleanup.
// Specification note: a Ready cache state does not erase a declared lazy initialization effect.
// Specification note: incomplete source or binary metadata is never interpreted as an empty effect set.
