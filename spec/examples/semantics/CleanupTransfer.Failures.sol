// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: cleanup failures preserve the primary Throwable and order.
module spec.cleanup.failures

class PrimaryFailure: Exception {}
class FirstCleanupFailure: Exception {}
class SecondCleanupFailure: Exception {}
class ThirdCleanupFailure: Exception {}
class RuntimeCleanupFailure: RuntimeException {}

func record(value: int): () {}
func failFirst(): () throws FirstCleanupFailure { throw FirstCleanupFailure() }
func failSecond(): () throws SecondCleanupFailure { throw SecondCleanupFailure() }
func failThird(): () throws ThirdCleanupFailure { throw ThirdCleanupFailure() }
func failRuntimeCleanup(): () { throw RuntimeCleanupFailure() }

// A cleanup failure on normal exit becomes the outgoing primary.
func normalExitHasCleanupFailure(): () throws FirstCleanupFailure {
    defer { failFirst() }
    record(1)
}

// A protected failure remains primary when a defer also fails.
func operationFailureRemainsPrimary(): () throws PrimaryFailure {
    defer { failFirst() }
    throw PrimaryFailure()
}

// A later-registered defer runs first and creates the primary on a normal exit.
func newestFailureIsInitialPrimary(): () throws FirstCleanupFailure {
    defer { failFirst() }
    defer { failSecond() }
}

// The older failure is suppressed after the newer cleanup failure has become primary.
func olderFailureIsSuppressed(): () throws SecondCleanupFailure {
    defer { failFirst() }
    defer { failSecond() }
}

// Two cleanup failures under an operation failure are both suppressed in execution order.
func multipleFailuresUnderPrimary(): () throws PrimaryFailure {
    defer { failFirst() }
    defer { failSecond() }
    throw PrimaryFailure()
}

// Registration order differs from execution and suppression order.
func registrationAndExecutionOrder(): () throws PrimaryFailure {
    defer { failFirst() }
    defer { failSecond() }
    defer { failThird() }
    throw PrimaryFailure()
}

// A successful newest action does not prevent an older action from becoming primary.
func successfulActionBeforeFailure(): () throws FirstCleanupFailure {
    defer { failFirst() }
    defer { record(10) }
}

// A failing action is attempted once; remaining actions still execute.
func failureDoesNotSkipRemainingCleanup(): () throws SecondCleanupFailure {
    defer { record(20) }
    defer { failFirst() }
    defer { failSecond() }
}

// With a pending operation failure, successful cleanups leave it unchanged.
func successfulCleanupPreservesPending(): ! throws PrimaryFailure {
    defer { record(30) }
    throw PrimaryFailure()
}

// Runtime cleanup failure still participates in suppression despite being unchecked.
func runtimeFailureIsStillSuppressed(): ! throws PrimaryFailure {
    defer { failRuntimeCleanup() }
    throw PrimaryFailure()
}

// Runtime cleanup failure can become primary on a normal exit without a checked budget.
func uncheckedNormalCleanup(): () {
    defer { failRuntimeCleanup() }
}

// Catch observes the primary after the protected cleanup chain finishes.
func catchReceivesOriginalPrimary(): () {
    try {
        defer { failFirst() }
        throw PrimaryFailure()
    } catch (error: PrimaryFailure) {
        record(40)
    }
}

// A cleanup-only failure can be caught by a handler surrounding the scope.
func catchCleanupOnlyFailure(): () {
    try {
        defer { failFirst() }
        record(41)
    } catch (error: FirstCleanupFailure) {
        record(42)
    }
}

// A handler inside the region cannot catch a failure created by an outer defer.
func innerCatchCannotSeeLaterCleanup(): () {
    defer { failFirst() }
    try {
        record(50)
    } catch (error: FirstCleanupFailure) {
        record(51)
    }
}

// A cleanup failure in a catch body propagates to the enclosing caller.
func catchBodyCleanupFailure(): () throws FirstCleanupFailure {
    try {
        throw PrimaryFailure()
    } catch (error: PrimaryFailure) {
        defer { failFirst() }
        record(60)
    }
}

// Rethrown primary remains primary when handler cleanup fails.
func rethrowWithCleanupFailure(): ! throws PrimaryFailure {
    try {
        throw PrimaryFailure()
    } catch (error: PrimaryFailure) {
        defer { failFirst() }
        throw error
    }
}

// A finally cleanup failure replaces normal completion.
func finallyFailureOnNormalExit(): () throws FirstCleanupFailure {
    try {
        record(70)
    } finally {
        failFirst()
    }
}

// A finally cleanup failure is suppressed under a pending protected failure.
func finallyFailureOnExceptionalExit(): ! throws PrimaryFailure {
    try {
        throw PrimaryFailure()
    } finally {
        failFirst()
    }
}

// The defer in the try block fails before finally is entered; finally is still attempted.
func tryDeferFailureThenFinallyFailure(): () throws FirstCleanupFailure {
    try {
        defer { failFirst() }
        record(80)
    } finally {
        failSecond()
    }
}

// The first failure remains primary while later nested scopes finish their cleanup.
func innerFailureThenOuterFailure(): () throws SecondCleanupFailure {
    defer { failFirst() }
    {
        defer { failSecond() }
    }
}

// The inner cleanup failure becomes primary before outer cleanup starts.
func outerFailureIsSuppressed(): () throws SecondCleanupFailure {
    defer { failFirst() }
    {
        defer { failSecond() }
    }
}

// A pending failure enters the inner and outer stacks in strict order.
func primaryAcrossNestedScopes(): ! throws PrimaryFailure {
    defer { failFirst() }
    {
        defer { failSecond() }
        throw PrimaryFailure()
    }
}

// A return expression failure becomes primary before cleanup starts.
func failedReturnExpressionWithCleanup(): int throws PrimaryFailure, FirstCleanupFailure {
    defer { failFirst() }
    return failValue()
}

func failValue(): int throws PrimaryFailure { throw PrimaryFailure() }

// Cleanup cannot return a value in place of the selected return.
func returnCannotBeReplaced(): int throws FirstCleanupFailure {
    defer { failFirst() }
    return 100
}

// A successful action leaves a pending return untouched.
func returnSurvivesSuccessfulCleanup(): int {
    defer { record(101) }
    return 102
}

// Break is cancelled if cleanup throws; the loop exit is not reached.
func breakCancelledByCleanup(): () throws FirstCleanupFailure {
    for {
        defer { failFirst() }
        break
    }
    record(110)
}

// Continue is cancelled if cleanup throws; the loop update does not execute.
func continueCancelledByCleanup(): () throws FirstCleanupFailure {
    for var index: int = 0; index < 2; index += 1 {
        defer { failFirst() }
        continue
    }
}

// Cleanup failure during an inner break unwinds enclosing loop-body cleanup too.
func innerBreakCleanupPropagatesOutward(): () throws FirstCleanupFailure {
    for {
        defer { record(120) }
        for {
            defer { failFirst() }
            break
        }
    }
}

// The suppression order follows actual execution, including nested scopes.
func suppressionAcrossNestedOrder(): ! throws PrimaryFailure {
    defer { failFirst() }
    {
        defer { failSecond() }
        {
            defer { failThird() }
            throw PrimaryFailure()
        }
    }
}

// Exception identity is preserved; the primary is not recreated from its message.
func preservePrimaryIdentity(primary: PrimaryFailure): ! throws PrimaryFailure {
    defer { failFirst() }
    throw primary
}

// A broad catch matches the primary, not an arbitrary suppressed object.
func catchBroadPrimary(): () {
    try {
        defer { failFirst() }
        throw PrimaryFailure()
    } catch (error: Throwable) {
        record(130)
    }
}

// A narrow cleanup effect remains visible even if a broad primary is thrown.
func effectBudgetIncludesSuppressed(): ! throws PrimaryFailure {
    defer { failFirst() }
    throw PrimaryFailure()
}

// Negative: omitting FirstCleanupFailure from the throws budget is invalid on a normal exit.
func insufficientCleanupBudget(): () {
    defer { failFirst() }
}

// Negative: declaring the operation failure alone does not cover a cleanup sibling.
func siblingBudgetDoesNotCoverCleanup(): ! throws PrimaryFailure {
    defer { failFirst() }
    throw PrimaryFailure()
}

// An enclosing catch can handle cleanup failure after the try's body has completed.
func outerCatchHandlesCleanupOnly(): () {
    try {
        {
            defer { failFirst() }
            record(140)
        }
    } catch (error: FirstCleanupFailure) {
        record(141)
    }
}

// An enclosing catch sees the original thrown object after suppression is recorded.
func outerCatchHandlesPrimary(): () {
    try {
        {
            defer { failFirst() }
            throw PrimaryFailure()
        }
    } catch (error: PrimaryFailure) {
        record(150)
    }
}

// Branch-specific cleanup contributes only to its branch's escaping set.
func conditionalEffect(flag: bool): () throws FirstCleanupFailure {
    if flag {
        defer { failFirst() }
    }
}

// The false path does not execute the throwing cleanup.
func conditionalEffectFalsePath(flag: bool): () {
    if flag {
        defer { failFirst() }
    }
    if !flag { record(160) }
}

// A failure in a later action cannot erase side effects of an earlier successful action.
func sideEffectsBeforeCleanupFailure(): () throws FirstCleanupFailure {
    defer { failFirst() }
    defer { record(170) }
    record(171)
}

// A failure in an action cannot prevent older cleanup from being attempted.
func oldActionRunsAfterFailure(): () throws FirstCleanupFailure {
    defer { record(180) }
    defer { failFirst() }
}

// Suppressed collection order is deterministic and does not depend on container iteration.
func deterministicSuppressedSequence(): ! throws PrimaryFailure {
    defer { failFirst() }
    defer { failSecond() }
    defer { failThird() }
    throw PrimaryFailure()
}

// A nested defer registered while a cleanup body runs belongs only to its nested block.
func nestedActionInsideCleanup(): () {
    defer {
        defer { record(190) }
        record(191)
    }
}

// Negative semantic form: adding an action to a scope whose drain has begun is rejected.
func registrationDuringOwnerDrainShape(): () {
    defer {
        // Rejected if this action targets the already-draining owner.
        record(200)
    }
}

// Low-memory recording must preserve the primary even when a suppression slot is unavailable.
func boundedSuppressionFallback(): ! throws PrimaryFailure {
    defer { failFirst() }
    throw PrimaryFailure()
}

// GC must retain the primary while cleanup allocates and throws.
func primaryRootedAcrossCleanup(): ! throws PrimaryFailure {
    defer { failFirst() }
    throw PrimaryFailure()
}

// Specification note: static effects include cleanup failures even when runtime suppression is bounded.
// Specification note: the original primary Throwable identity is never replaced by a later failure.
// Specification note: every earlier action is attempted after a cleanup action throws.
// Specification note: nested scopes compose their primary state before moving to the next owner.
// Specification note: foreign exception objects require an explicit boundary conversion.
// Specification note: process abort and heap corruption do not promise recoverable cleanup.
