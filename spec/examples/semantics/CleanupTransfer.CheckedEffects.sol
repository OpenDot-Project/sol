// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: cleanup effect sets follow the reachable ordered edges.
module spec.cleanup.checkedeffects

class FileFailure: Exception {}
class NetworkFailure: Exception {}
class TimeoutFailure: NetworkFailure {}
class ParseFailure: Exception {}
class BoundsFailure: RuntimeException {}
class BroadException: Exception {}

func closeFile(): () throws FileFailure {}
func flushNetwork(): () throws NetworkFailure {}
func parseTail(): () throws ParseFailure {}
func record(value: int): () {}
func raiseFileFailure(): ! throws FileFailure { throw FileFailure() }
func raiseBoundsFailure(): () { throw BoundsFailure() }
func raiseBroadFailure(): () throws BroadException { throw BroadException() }
func <E: Throwable> raiseGeneric(error: E): () throws E { throw error }

// A single cleanup checked effect is part of the callable budget.
func oneCleanupEffect(): () throws FileFailure {
    defer { closeFile() }
}

// A supertype budget covers the selected cleanup effect.
func cleanupEffectCoveredByException(): () throws Exception {
    defer { closeFile() }
}

// A sibling checked type does not cover the cleanup effect.
func cleanupSiblingBudget(): () throws ParseFailure {
    defer { closeFile() }
}

// Omitting the effect budget is invalid even if the action is not on the main expression path.
func cleanupEffectCannotDisappear(): () {
    defer { closeFile() }
}

// Multiple cleanups union their escaping checked effects.
func multipleCleanupEffects(): () throws FileFailure, NetworkFailure {
    defer { closeFile() }
    defer { flushNetwork() }
}

// Failure order does not change the static effect set.
func sameEffectsDifferentOrder(): () throws FileFailure, NetworkFailure {
    defer { flushNetwork() }
    defer { closeFile() }
}

// A conditional registration contributes on its active path.
func conditionalCleanupEffect(flag: bool): () throws FileFailure {
    if flag {
        defer { closeFile() }
    }
}

// A proven skipped branch has no escaping cleanup effect on that path.
func effectOnOnlyFalsePath(flag: bool): () throws FileFailure {
    if !flag {
        defer { closeFile() }
    }
    if flag { record(1) }
}

// A loop-body cleanup effect is possible if any iteration reaches the declaration.
func loopCleanupEffect(count: int): () throws NetworkFailure {
    for var index: int = 0; index < count; index += 1 {
        defer { flushNetwork() }
    }
}

// A loop proven to execute zero times contributes no body cleanup operation.
func zeroLoopHasNoBodyEffect(): () {
    for false {
        defer { closeFile() }
    }
}

// The cleanup's effect budget is not multiplied by loop iteration count.
func repeatedLoopEffectSet(count: int): () throws NetworkFailure {
    for var index: int = 0; index < count; index += 1 {
        defer { flushNetwork() }
    }
}

// Nested scopes compose each effect set at the point each owner exits.
func nestedCleanupEffectSet(): () throws FileFailure, NetworkFailure {
    defer { closeFile() }
    {
        defer { flushNetwork() }
    }
}

// A catch around the entire cleanup region can handle a cleanup-only failure.
func catchCleanupFailure(): () {
    try {
        defer { closeFile() }
        record(10)
    } catch (error: FileFailure) {
        record(11)
    }
}

// Catching a protected operation does not automatically catch a later outer cleanup.
func handlerTooNarrowForOuterCleanup(): () throws FileFailure {
    defer { closeFile() }
    try {
        record(12)
    } catch (error: NetworkFailure) {
        record(13)
    }
}

// A cleanup inside a protected block runs before its surrounding catch can complete.
func outerCatchSeesCleanupEffect(): () {
    try {
        {
            defer { closeFile() }
            record(20)
        }
    } catch (error: FileFailure) {
        record(21)
    }
}

// An effect created by a catch handler is not removed by the same handler.
func handlerCleanupAddsEffect(): () throws NetworkFailure {
    try {
        throw FileFailure()
    } catch (error: FileFailure) {
        defer { flushNetwork() }
        record(30)
    }
}

// A cleanup checked effect may be suppressed dynamically but remains statically declared.
func suppressedCleanupStillChecked(): ! throws FileFailure, NetworkFailure {
    defer { flushNetwork() }
    throw FileFailure()
}

// A cleanup on a normal return can become the primary checked failure.
func cleanupReplacesNormalReturn(): int throws NetworkFailure {
    defer { flushNetwork() }
    return 31
}

// A cleanup on a pending exception does not replace the exception, but retains its effect origin.
func cleanupUnderPrimary(): ! throws FileFailure, NetworkFailure {
    defer { flushNetwork() }
    throw FileFailure()
}

// Unchecked cleanup failures still affect control flow and suppression.
func uncheckedCleanupFailure(): () {
    defer { raiseBoundsFailure() }
}

// RuntimeException descendants do not need a checked budget entry.
func runtimeSubtypeNoBudget(): () {
    defer { raiseBoundsFailure() }
}

// A declared unchecked entry cannot cover a checked sibling.
func uncheckedEntryDoesNotCoverChecked(): () throws BoundsFailure {
    defer { closeFile() }
}

// Only the selected call contributes its effect to a cleanup body.
func closeSelectedFile(): () throws FileFailure {
    closeFile()
}

func alternateCleanup(): () throws NetworkFailure {}

// A branch within cleanup contributes its reachable effects.
func conditionalOperationsInsideCleanup(flag: bool): () throws FileFailure, NetworkFailure {
    defer {
        if flag { closeFile() } else { flushNetwork() }
    }
}

// A branch that terminates before a call does not contribute that unreachable call.
func bottomBeforeEffect(flag: bool): () throws FileFailure, NetworkFailure {
    defer {
        if flag { raiseFileFailure() }
        flushNetwork()
    }
}

// Both possible effects remain when each branch can complete exceptionally.
func unionOfCleanupBranches(flag: bool): () throws FileFailure, NetworkFailure {
    defer {
        if flag { closeFile() } else { flushNetwork() }
    }
}

// Nested defer inside the action has an independent nested effect edge.
func nestedActionEffects(): () throws FileFailure, NetworkFailure {
    defer {
        defer { closeFile() }
        flushNetwork()
    }
}

// An inner action's checked effect is included even if the outer action later also fails.
func nestedActionsBothFail(): () throws FileFailure, NetworkFailure {
    defer { closeFile() }
    defer {
        defer { flushNetwork() }
        record(40)
    }
}

// Cleanup body expressions have normal argument-order effect analysis.
func effectfulArgumentsInsideCleanup(): () throws FileFailure, ParseFailure {
    defer { consume(closeThenParse()) }
}

func closeThenParse(): int throws FileFailure, ParseFailure {
    closeFile()
    parseTail()
    1
}

func consume(value: int): () {}

// A failure in an earlier evaluation step prevents later cleanup expressions.
func leftEffectBeforeRightEffect(): () throws FileFailure, ParseFailure {
    defer { combine(closeValue(), parseValue()) }
}

func closeValue(): int throws FileFailure { 1 }
func parseValue(): int throws ParseFailure { 2 }
func combine(left: int, right: int): () {}

// The declaration's static type determines whether the effect is checked.
func throwBroadEffect(): ! throws BroadException, FileFailure {
    defer { raiseBroadFailure() }
    throw FileFailure()
}

// A RuntimeException thrown from cleanup is still catchable but is not checked.
func catchRuntimeCleanup(): () {
    try {
        defer { raiseBoundsFailure() }
    } catch (error: BoundsFailure) {
        record(50)
    }
}

// A generic effect preserves its type parameter through cleanup invocation.
func <E: Throwable> deferGeneric(error: E): () throws E {
    defer { raiseGeneric(error) }
}

// A generic effect cannot be erased by widening an unrelated call site.
func useGenericEffect(): () throws FileFailure {
    deferGeneric(NetworkFailure())
}

// Function values retain cleanup effects when the deferred body invokes them.
func invokeChecked(callback: () -> () throws FileFailure): () throws FileFailure {
    callback()
}

func deferCheckedCallback(callback: () -> () throws FileFailure): () throws FileFailure {
    defer { invokeChecked(callback) }
}

// A callback body is not executed at function-value construction time.
func createCallback(): () -> () throws FileFailure {
    { throw FileFailure() }
}

func callbackOnlyThrowsWhenDeferred(): () throws FileFailure {
    val callback = createCallback()
    defer { callback() }
}

// Candidate-local effect selection prevents rejected overloads from tainting cleanup.
class CandidateCleaner {
    func close(value: int): () {}
    func close(value: String): () throws FileFailure {}
}

func selectedPureCleanup(cleaner: CandidateCleaner): () {
    defer { cleaner.close(1) }
}

// Cleanup effect contracts survive public metadata serialization.
func exportedCleanupContract(): () throws FileFailure {
    defer { closeFile() }
}

// Missing old metadata is unknown, never an empty cleanup effect set.
func incompleteCleanupMetadata(): () throws FileFailure {
    defer { closeFile() }
}

// Specification note: control-flow reachability selects which cleanup effects can escape.
// Specification note: effect sets are unions of nominal checked types, not printed names.
// Specification note: suppression affects primary selection, not static effect checking.
// Specification note: catching an operation does not remove an effect created later by cleanup.
// Specification note: a verified FIR body must retain each cleanup exceptional successor.
