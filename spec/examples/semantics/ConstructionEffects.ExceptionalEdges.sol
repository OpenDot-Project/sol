// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: construction failures retain completed prefix state only.
module spec.construction.exceptionaledges

class ArgumentFailure: Exception {}
class DefaultFailure: Exception {}
class BaseFailure: Exception {}
class FieldFailure: Exception {}
class BodyFailure: Exception {}

func record(value: int): () {}
func argument(value: int): int { record(value); value }

func firstArgument(): int throws ArgumentFailure { throw ArgumentFailure() }
func secondArgument(): int throws DefaultFailure { throw DefaultFailure() }
func defaultArgument(): int throws DefaultFailure { throw DefaultFailure() }

class TwoArgumentResource(val left: int, val right: int = defaultArgument())

// A first-argument throw prevents the second explicit argument and default expression.
func firstArgumentExceptionalPrefix(): TwoArgumentResource throws ArgumentFailure {
    TwoArgumentResource(left = firstArgument(), right = secondArgument())
}

// If the first argument succeeds, a second-argument throw preserves its completed side effect.
func secondArgumentExceptionalPrefix(): TwoArgumentResource throws DefaultFailure {
    TwoArgumentResource(left = argument(1), right = secondArgument())
}

// When right is omitted, the default executes after all explicit arguments complete.
func omittedDefaultExceptionalEdge(): TwoArgumentResource throws DefaultFailure {
    TwoArgumentResource(left = argument(2))
}

// A supplied argument suppresses a default that would otherwise throw.
func explicitValueAvoidsDefaultFailure(): TwoArgumentResource {
    TwoArgumentResource(left = 3, right = 4)
}

// If an explicit argument fails, no default from the selected constructor executes.
func explicitFailureBeforeDefault(): TwoArgumentResource throws ArgumentFailure {
    TwoArgumentResource(left = firstArgument())
}

// Base construction is an exceptional prefix before derived initialization.
class ThrowingBase {
    constructor() throws BaseFailure { throw BaseFailure() }
}

class DerivedAfterBase: ThrowingBase {
    val value: int = recordAndReturn(10)
    constructor(): super() throws BaseFailure { record(11) }
}

func baseExceptionalEdge(): DerivedAfterBase throws BaseFailure {
    DerivedAfterBase()
}

func recordAndReturn(value: int): int { record(value); value }

// A base failure means the derived field initializer and body have no execution edge.
func noDerivedWorkOnBaseFailure(): DerivedAfterBase throws BaseFailure {
    DerivedAfterBase()
}

// A field initializer failure occurs after a successful base call, but before later fields.
class FieldPrefixResource {
    val first: int = recordAndReturn(20)
    val failed: int = failField()
    val later: int = recordAndReturn(21)
    constructor() throws FieldFailure { record(22) }
}

func failField(): int throws FieldFailure { throw FieldFailure() }

func fieldExceptionalPrefix(): FieldPrefixResource throws FieldFailure {
    FieldPrefixResource()
}

// Earlier field state remains private and rooted while later initialization fails.
func earlierReferenceRetainedDuringFailure(): FailedReferenceResource throws FieldFailure {
    FailedReferenceResource()
}

class FailedReferenceResource {
    val first: String = createString()
    val second: String = failString()
    constructor() throws FieldFailure {}
}

func createString(): String { record(30); "first" }
func failString(): String throws FieldFailure { throw FieldFailure() }

// A GC safepoint between field initializers must retain only already valid reference slots.
func rootMapOnPartialInitialization(): FailedReferenceResource throws FieldFailure {
    FailedReferenceResource()
}

// An init block can fail after prior declarations have completed.
class InitBlockPrefix {
    val first: int = recordAndReturn(40)
    init { failInitBlock() }
    val later: int = recordAndReturn(41)
    constructor() throws FieldFailure { record(42) }
}

func failInitBlock(): () throws FieldFailure { throw FieldFailure() }

func initBlockExceptionalEdge(): InitBlockPrefix throws FieldFailure {
    InitBlockPrefix()
}

// A constructor body may fail after all required fields are valid, but still does not return an object.
class BodyPrefixResource {
    val value: int = recordAndReturn(50)
    constructor() throws BodyFailure { throw BodyFailure() }
}

func bodyExceptionalEdge(): BodyPrefixResource throws BodyFailure {
    BodyPrefixResource()
}

// Any temporary resource acquired during the constructor has its own cleanup on failure.
class TemporaryResource {
    constructor() {}
    func close(): () {}
}

func acquireTemporary(): TemporaryResource { TemporaryResource() }

class ConstructorWithTemporary {
    val value: int
    constructor() throws FieldFailure {
        val temporary = acquireTemporary()
        defer { temporary.close() }
        value = failField()
    }
}

func temporaryReleasedOnFieldFailure(): ConstructorWithTemporary throws FieldFailure {
    ConstructorWithTemporary()
}

// A constructor's active defer runs on a later body throw.
class ConstructorBodyThrowWithCleanup {
    val value: int
    constructor() throws BodyFailure {
        val temporary = acquireTemporary()
        defer { temporary.close() }
        value = 60
        throw BodyFailure()
    }
}

func bodyFailureRunsCleanup(): ConstructorBodyThrowWithCleanup throws BodyFailure {
    ConstructorBodyThrowWithCleanup()
}

// A constructor cleanup failure is combined with the constructor's primary failure.
func constructorPrimaryAndCleanupFailure(): ConstructorBodyThrowWithCleanup throws BodyFailure, FieldFailure {
    ConstructorBodyThrowWithCleanup()
}

class ConstructorCleanupFailure {
    val value: int
    constructor() throws BodyFailure, FieldFailure {
        defer { failField() }
        value = 61
        throw BodyFailure()
    }
}

func invokeConstructorWithSuppression(): ConstructorCleanupFailure throws BodyFailure, FieldFailure {
    ConstructorCleanupFailure()
}

// A surrounding catch sees the primary construction failure after constructor cleanup.
func catchAfterConstructorCleanup(): () {
    try {
        ConstructorCleanupFailure()
    } catch (error: BodyFailure) {
        record(70)
    }
}

// A catch for the cleanup failure alone does not match when a different primary is pending.
func catchCannotSelectSuppressedAsPrimary(): () throws BodyFailure {
    try {
        ConstructorCleanupFailure()
    } catch (error: FieldFailure) {
        record(71)
    }
}

// An exception object from a constructor argument remains the same primary during outer cleanup.
func outerCleanupAroundFailedConstruction(): TwoArgumentResource throws ArgumentFailure {
    defer { record(80) }
    TwoArgumentResource(left = firstArgument())
}

// An outer cleanup failure is suppressed under the constructor argument failure.
func outerCleanupFailureUnderArgumentFailure(): TwoArgumentResource throws ArgumentFailure, FieldFailure {
    defer { failField() }
    TwoArgumentResource(left = firstArgument())
}

// The constructor call itself is not entered when receiver or argument evaluation fails.
func argumentFailurePreventsConstructorEntry(): BodyPrefixResource throws ArgumentFailure {
    BodyPrefixResourceWithArgument(firstArgument())
}

class BodyPrefixResourceWithArgument(val value: int) {
    init { record(90) }
}

// A value assigned before a later throw remains visible in the handler's local flow state.
func localPrefixBeforeConstruction(value: int): int throws FieldFailure {
    var result = value
    try {
        FieldPrefixResource()
        result = 91
    } catch (error: FieldFailure) {
        return result
    }
    result
}

// Assignment after a construction that throws does not reach its catch state.
func localWriteAfterConstruction(value: int): int throws FieldFailure {
    var result = value
    try {
        FieldPrefixResource()
        result = 92
    } catch (error: FieldFailure) {
        return result
    }
    result
}

// A construction failure does not assign an out parameter by returning a partial reference.
func outNotCompletedByConstructor(out value: BodyPrefixResource): () throws BodyFailure {
    value = BodyPrefixResource()
}

// On the throw edge, the caller has no normal-return out guarantee.
func callerOutOnConstructionThrow(): () throws BodyFailure {
    var value: BodyPrefixResource
    outNotCompletedByConstructor(out value)
}

// An exception thrown by an init accessor leaves its target field unassigned.
class AccessorExceptionalPrefix {
    val value: String { init throws FieldFailure { throw FieldFailure() } }
    constructor(text: String) throws FieldFailure { this.value = text }
}

func accessorFailureDoesNotPublish(): AccessorExceptionalPrefix throws FieldFailure {
    AccessorExceptionalPrefix("text")
}

// An explicit catch can convert a failure only by constructing a new declared exception.
class WrappedConstructionFailure: Exception {}

func catchAndWrapConstruction(): BodyPrefixResource throws WrappedConstructionFailure {
    try {
        BodyPrefixResource()
    } catch (error: BodyFailure) {
        throw WrappedConstructionFailure()
    }
}

// A wrapper preserves cause explicitly; no implicit checked-effect erasure occurs.
func catchAndRecoverConstruction(): int {
    try {
        BodyPrefixResource()
    } catch (error: BodyFailure) {
        100
    }
}

// A handler's own failure is a new outgoing effect, not removed by its active catch.
func catchAndFailAgain(): int throws ParseFailure {
    try {
        BodyPrefixResource()
    } catch (error: BodyFailure) {
        throw ParseFailure()
    }
}

class ParseFailure: Exception {}

// Construction effects include only operations whose evaluation prefix is reachable.
func effectPrefixFromNamedArguments(): TwoArgumentResource throws ArgumentFailure, DefaultFailure {
    TwoArgumentResource(right = secondArgument(), left = firstArgument())
}

// Source argument order makes the right expression the first possible failure above.
func effectPrefixInSourceOrder(): TwoArgumentResource throws DefaultFailure {
    TwoArgumentResource(right = secondArgument(), left = firstArgument())
}

// The later left argument's effect is unreachable if the first source argument throws.
func omittedArgumentEffectAfterSourceFailure(): TwoArgumentResource throws DefaultFailure {
    TwoArgumentResource(right = secondArgument(), left = 1)
}

// Specification note: each exceptional edge sees exactly the completed evaluation prefix.
// Specification note: construction failure does not publish partial class or struct values.
// Specification note: local cleanup runs while a constructor failure propagates outward.
// Specification note: a failed cleanup never hides the constructor's original primary failure.
// Specification note: root maps distinguish initialized reference fields from zeroed storage.
