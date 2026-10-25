// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: arguments, constructor chains, and initializers have fixed order.
module spec.construction.order

func record(value: int): () {}
func argument(value: int): int { record(value); value }

// Explicit arguments evaluate once in source order before defaults.
class ArgumentOrder(
    val left: int = 0,
    val right: int = 0,
    val tail: int = 0,
) {
    init { record(10) }
}

func explicitArgumentsFirst(): ArgumentOrder {
    ArgumentOrder(left = argument(1), right = argument(2))
}

// A default expression is evaluated only when its parameter is omitted.
func omittedDefaultRuns(): ArgumentOrder {
    ArgumentOrder(left = argument(3), right = argument(4), tail = argument(5))
}

// The class above uses constant defaults; this declaration exposes an effectful default.
class DefaultedArgument(
    val first: int,
    val second: int = defaultSecond(),
) {
    init { record(20) }
}

func defaultSecond(): int { record(21); 22 }

func defaultRunsAfterExplicitArguments(): DefaultedArgument {
    DefaultedArgument(first = argument(23))
}

// An explicit value suppresses only that parameter's default expression.
func suppliedDefaultIsSkipped(): DefaultedArgument {
    DefaultedArgument(first = argument(30), second = argument(31))
}

// Named argument source order remains the explicit evaluation order.
func namedArgsKeepSourceEvaluationOrder(): ArgumentOrder {
    ArgumentOrder(tail = argument(40), left = argument(41), right = argument(42))
}

// The resulting parameter map does not reorder the already evaluated expressions.
func parameterOrderDoesNotRewriteSideEffects(): ArgumentOrder {
    ArgumentOrder(right = argument(50), tail = argument(51), left = argument(52))
}

class BaseOrder {
    constructor() { record(60) }
}

// Base construction completes before any derived initializer runs.
class DerivedOrder: BaseOrder {
    val first: int = initializeFirst()
    init { record(62) }
    val second: int = initializeSecond()
    constructor(): super() { record(64) }
}

func initializeFirst(): int { record(61); 1 }
func initializeSecond(): int { record(63); 2 }

func createDerivedInOrder(): DerivedOrder = DerivedOrder()

// Field initializers and init blocks follow declaration order exactly.
class InterleavedInitializers {
    val a: int = step(70)
    init { step(71) }
    val b: int = step(72)
    init { step(73) }
    val c: int = step(74)
    constructor() { record(75) }
}

func step(value: int): int { record(value); value }

func instantiateInterleaved(): InterleavedInitializers = InterleavedInitializers()

// The constructor body runs after all field initializers and init blocks.
class BodyAfterInitializers {
    val field: int = step(80)
    init { step(81) }
    constructor() { record(82) }
}

// A secondary constructor delegates once, then executes its own body.
class SecondaryOrder(val value: int) {
    init { record(90) }
    constructor(): this(secondaryValue()) { record(93) }
}

func secondaryValue(): int { record(91); 92 }

func constructSecondary(): SecondaryOrder = SecondaryOrder()

// A secondary constructor's explicit argument is evaluated before delegation starts.
class SecondaryArgumentOrder(val value: int) {
    constructor(value: int, marker: int): this(combineArgs(value, marker)) {
        record(103)
    }
}

func combineArgs(left: int, right: int): int {
    record(100)
    left + right
}

func useSecondaryArgumentOrder(): SecondaryArgumentOrder {
    SecondaryArgumentOrder(argument(101), argument(102))
}

// A this-chain cycle is invalid and cannot be resolved by declaration order.
func selectDelegatedValue(flag: bool): int {
    if flag { return 1 }
    return 2
}

class CyclicDelegationShape(val value: int) {
    // Negative design vector: secondary constructor cycle must be rejected.
    constructor(flag: bool): this(selectDelegatedValue(flag)) {}
}

// A derived constructor cannot run its body before its explicit base constructor.
class BaseEffectOrder {
    constructor() { record(110) }
}

class DerivedEffectOrder: BaseEffectOrder {
    val field: int = step(111)
    constructor(): super() { record(112) }
}

// Base argument effects precede base body, then derived field initialization.
class BaseWithArgument(val value: int) {
    init { record(120) }
}

func baseArg(): int { record(119); 1 }

class DerivedWithBaseArgument: BaseWithArgument {
    val field: int = step(121)
    constructor(): super(baseArg()) { record(122) }
}

// Default values for a selected base constructor are evaluated before the base body.
class BaseWithDefault(val value: int = defaultBaseValue()) {
    init { record(130) }
}

func defaultBaseValue(): int { record(129); 2 }

class DerivedWithDefaultBase: BaseWithDefault {
    val field: int = step(131)
    constructor(): super() { record(132) }
}

class OrderFailure: Exception {}

func mayFailAt(value: int): int throws OrderFailure {
    record(value)
    if value == 140 { throw OrderFailure() }
    value
}

// If the first explicit argument fails, later explicit arguments and defaults do not run.
class FailingArgument(val first: int, val second: int = defaultSecond()) {}

func failInFirstArgument(): FailingArgument throws OrderFailure {
    FailingArgument(first = mayFailAt(140), second = argument(141))
}

// If a later explicit argument fails, earlier side effects remain completed.
func failInSecondArgument(): FailingArgument throws OrderFailure {
    FailingArgument(first = argument(142), second = mayFailAt(140))
}

// A default failure prevents allocation-visible field initialization and body execution.
class FailingDefault(val value: int = failingDefault()) {
    val field: int = step(150)
    constructor() { record(151) }
}

func failingDefault(): int throws OrderFailure {
    record(152)
    throw OrderFailure()
}

func createWithFailingDefault(): FailingDefault throws OrderFailure {
    FailingDefault()
}

// A base constructor failure prevents derived initializers from running.
class FailingBase {
    constructor() throws OrderFailure {
        throw OrderFailure()
    }
}

class DerivedFromFailingBase: FailingBase {
    val field: int = step(160)
    constructor(): super() { record(161) }
}

func baseFailureStopsDerivedInit(): DerivedFromFailingBase throws OrderFailure {
    DerivedFromFailingBase()
}

// A field initializer failure prevents later declarations and constructor body statements.
class FailingFieldOrder {
    val first: int = step(170)
    val failed: int = mayFailAt(140)
    val later: int = step(171)
    constructor() { record(172) }
}

func fieldFailureStopsLaterInitialization(): FailingFieldOrder throws OrderFailure {
    FailingFieldOrder()
}

// An init-block failure stops later fields and constructor body execution.
class FailingInitBlock {
    val first: int = step(180)
    init { mayFailAt(140) }
    val later: int = step(181)
    constructor() { record(182) }
}

func initBlockFailureStopsBody(): FailingInitBlock throws OrderFailure {
    FailingInitBlock()
}

// Constructor-only parameters are not automatically retained as fields.
class ConstructorOnlyParameter(value: int) {
    val stored: int = value
    constructor() : this(argument(190)) { record(191) }
}

// A val parameter creates its declared property under the object contract.
class StoredConstructorParameter(val value: int) {
    init { record(value) }
}

// A var constructor property is mutable storage initialized by the construction plan.
class MutableConstructorParameter(var value: int) {
    init { record(value) }
}

// A normal call observing the object runs only after the constructor returns.
func observeConstructed(value: BodyAfterInitializers): int = value.field

func constructionThenObservation(): int {
    val instance = BodyAfterInitializers()
    observeConstructed(instance)
}

// A failed constructor does not return a reference for a later call to observe.
func noObservationAfterFailure(): int throws OrderFailure {
    val instance = FailingFieldOrder()
    instance.later
}

// Construction arguments may themselves create other objects before this allocation begins.
class NestedArgument(val value: int) {}

class OuterFromNestedArgument(val inner: NestedArgument) {
    init { record(200) }
}

func nestedConstructionOrder(): OuterFromNestedArgument {
    OuterFromNestedArgument(inner = NestedArgument(argument(201)))
}

// A nested construction failure prevents the outer constructor from starting.
func nestedArgumentFailure(): OuterFromNestedArgument throws OrderFailure {
    OuterFromNestedArgument(inner = NestedArgument(mayFailAt(140)))
}

// An initializer that reads an earlier field observes its initialized value.
class ReadsEarlierField {
    val first: int = step(210)
    val second: int = deriveFrom(first)
    constructor() { record(212) }
}

func deriveFrom(value: int): int { record(value + 1); value + 1 }

// A read of a later required field is invalid before its declaration initializes it.
class ReadsLaterFieldShape {
    val first: int = readLater(this)
    val later: int = step(220)
    constructor() { record(221) }
}

func readLater(value: ReadsLaterFieldShape): int = 0

// Unsupported: arbitrary source-file ordering cannot alter the declaration sequence.
// Unsupported: an ambiguous base constructor is not selected by source order.
// Unsupported: a failed initializer does not run later initializers for cleanup convenience.
// Supported contract: earlier side effects remain observable when a later operation fails.
// Supported contract: successful construction publishes only after fields and body complete.
// Supported contract: every construction edge carries argument, default, and constructor effects.
