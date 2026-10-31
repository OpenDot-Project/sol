// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: cleanup is tied to each dynamic loop-body scope.
module spec.cleanup.loops

func record(value: int): () {}

// Each body entry creates and drains one action.
func actionPerIteration(count: int): () {
    for var index: int = 0; index < count; index += 1 {
        defer { record(index) }
        record(index + 1)
    }
}

// The body action is drained before the counted-loop update executes.
func cleanupBeforeUpdate(count: int): () {
    for var index: int = 0; index < count; index += observeUpdate(index) {
        defer { record(index) }
        record(index + 10)
    }
}

func observeUpdate(index: int): int = 1

// Continue leaves the body, drains the action, then performs the update.
func continueRunsCleanupThenUpdate(count: int): int {
    var total = 0
    for var index: int = 0; index < count; index += 1 {
        defer { record(index + 20) }
        if index % 2 == 0 {
            continue
        }
        total += index
    }
    total
}

// Break drains the iteration body and skips the update expression.
func breakRunsCleanupWithoutUpdate(count: int): int {
    var total = 0
    for var index: int = 0; index < count; index += observeUpdate(index) {
        defer { record(index + 30) }
        if index == 2 { break }
        total += index
    }
    total
}

// A condition-tested loop may execute zero times and registers nothing on that path.
func zeroIterationsNoAction(run: bool): () {
    for run {
        defer { record(40) }
        run = false
    }
}

// A loop-level action remains active across all iterations.
func loopOwnerAction(count: int): () {
    defer { record(50) }
    for var index: int = 0; index < count; index += 1 {
        record(index + 51)
    }
    record(52)
}

// A loop body action is not promoted to the surrounding function scope.
func bodyDoesNotBecomeLoopOwner(count: int): () {
    for var index: int = 0; index < count; index += 1 {
        defer { record(index + 60) }
    }
    record(61)
}

// A nested block drains before the rest of its current iteration.
func nestedBlockPerIteration(count: int): () {
    for var index: int = 0; index < count; index += 1 {
        defer { record(index + 70) }
        {
            defer { record(index + 71) }
            record(index + 72)
        }
        record(index + 73)
    }
}

// Continue exits both nested block and iteration scope before the update.
func continueLeavesTwoScopes(count: int): () {
    for var index: int = 0; index < count; index += 1 {
        defer { record(index + 80) }
        {
            defer { record(index + 81) }
            if index == 1 { continue }
        }
        record(index + 82)
    }
}

// Break in the inner loop does not exit the outer iteration scope.
func innerBreakRetainsOuterAction(count: int): () {
    for var outer: int = 0; outer < count; outer += 1 {
        defer { record(outer + 90) }
        for {
            defer { record(outer + 91) }
            break
        }
        record(outer + 92)
    }
}

// Continue in the inner loop drains only inner iteration actions.
func innerContinueKeepsOuterAction(count: int): () {
    for var outer: int = 0; outer < count; outer += 1 {
        defer { record(outer + 100) }
        for var inner: int = 0; inner < 2; inner += 1 {
            defer { record(outer * 10 + inner + 101) }
            if inner == 0 { continue }
            record(inner + 102)
        }
        record(outer + 103)
    }
}

// A labelled loop transfer is not available; the resolved nearest loop controls cleanup.
func nearestLoopTarget(count: int): () {
    for var outer: int = 0; outer < count; outer += 1 {
        defer { record(outer + 110) }
        for var inner: int = 0; inner < 2; inner += 1 {
            defer { record(inner + 111) }
            if inner == 1 { break }
        }
    }
}

// A return leaves every active iteration and enclosing scope.
func returnFromLoop(count: int): int {
    defer { record(120) }
    for var index: int = 0; index < count; index += 1 {
        defer { record(index + 121) }
        if index == 2 { return index }
    }
    -1
}

class LoopFailure: Exception {}
func failLoopCleanup(): () throws LoopFailure { throw LoopFailure() }

// Throw drains body cleanup and the function cleanup before reaching a caller.
func throwFromLoop(count: int): () throws LoopFailure {
    defer { record(130) }
    for var index: int = 0; index < count; index += 1 {
        defer { record(index + 131) }
        if index == 2 { throw LoopFailure() }
    }
}

// A loop can register zero, one, or many actions over its lifetime.
func registrationCardinality(count: int): () {
    for var index: int = 0; index < count; index += 1 {
        if index % 3 == 0 {
            defer { record(index + 140) }
        }
    }
}

// Only the current iteration's conditional action runs at its own exit.
func conditionalRegistrationEachIteration(count: int): () {
    for var index: int = 0; index < count; index += 1 {
        if index % 2 == 0 {
            defer { record(index + 150) }
        }
        record(index + 151)
    }
}

// The else path has no defer and cannot inherit one from an earlier iteration.
func alternatingRegistration(count: int): () {
    for var index: int = 0; index < count; index += 1 {
        if index % 2 == 0 {
            defer { record(index + 160) }
        } else {
            record(index + 161)
        }
    }
}

// An iteration action that throws cancels the pending continue and skips the update.
func failedCleanupCancelsContinue(count: int): () throws LoopFailure {
    for var index: int = 0; index < count; index += observeUpdate(index) {
        defer { if index == 0 { failLoopCleanup() } }
        continue
    }
}

// A cleanup failure cancels break; the caller observes the failure instead of loop exit.
func failedCleanupCancelsBreak(): () throws LoopFailure {
    for {
        defer { failLoopCleanup() }
        break
    }
}

// A loop update failure occurs after normal iteration cleanup has completed.
func updateFailureFollowsCleanup(count: int): () throws LoopFailure {
    for var index: int = 0; index < count; index += updateMayFail(index) {
        defer { record(index + 170) }
    }
}

func updateMayFail(index: int): int throws LoopFailure {
    if index == 1 { throw LoopFailure() }
    1
}

// A condition failure occurs after the prior iteration has already drained.
func conditionFailureAfterPreviousCleanup(): () throws LoopFailure {
    var keepGoing = true
    for keepGoing {
        defer { record(180) }
        keepGoing = false
    }
}

// Empty collection iteration registers no body action.
func iterableLoopMayBeEmpty(items: []int): () {
    for val item in items {
        defer { record(item + 190) }
    }
}

// Collection loop continues only after each iteration's cleanup completes.
func iterableContinue(items: []int): () {
    for val item in items {
        defer { record(item + 200) }
        if item < 0 { continue }
        record(item + 201)
    }
}

// Collection break drains current iteration and does not fetch the next element.
func iterableBreak(items: []int): () {
    for val item in items {
        defer { record(item + 210) }
        if item == 4 { break }
    }
}

// A nested loop has a new body scope each time the outer loop body enters it.
func nestedLoopRegistrations(count: int): () {
    for var outer: int = 0; outer < count; outer += 1 {
        defer { record(outer + 220) }
        for var inner: int = 0; inner < 2; inner += 1 {
            defer { record(outer * 10 + inner + 221) }
        }
    }
}

// Continue of the outer loop is reached only after nested loops have completed.
func outerContinueAfterInnerLoop(count: int): () {
    for var outer: int = 0; outer < count; outer += 1 {
        defer { record(outer + 230) }
        for {
            defer { record(outer + 231) }
            break
        }
        if outer == 1 { continue }
        record(outer + 232)
    }
}

// Inner break does not run an outer counted-loop update; outer iteration continues normally.
func innerBreakNotOuterBreak(count: int): int {
    var updates = 0
    for var outer: int = 0; outer < count; outer += 1 {
        defer { record(outer + 240) }
        for {
            defer { record(outer + 241) }
            break
        }
        updates += 1
    }
    updates
}

// A nested scope in a continue path drains before the enclosing loop-body action.
func nestedContinueDrain(count: int): () {
    for var index: int = 0; index < count; index += 1 {
        defer { record(index + 250) }
        {
            defer { record(index + 251) }
            if index == 2 { continue }
        }
        record(index + 252)
    }
}

// A nested scope in a break path drains before the loop body exits.
func nestedBreakDrain(): () {
    for {
        defer { record(260) }
        {
            defer { record(261) }
            break
        }
    }
}

// An ordinary loop condition is checked before any body registration.
func conditionFalseSkipsBody(): () {
    for false {
        defer { record(270) }
    }
}

// A counted-loop initializer is outside the per-iteration body owner.
func initializerNotBodyCleanup(): () {
    for var index: int = initialize(); index < 0; index += 1 {
        defer { record(index + 280) }
    }
}

func initialize(): int = 0

// An exception from the iterable expression occurs before the body scope exists.
func iterableExpressionBeforeBody(): () throws LoopFailure {
    for val item in loadItems() {
        defer { record(item + 290) }
    }
}

func loadItems(): []int throws LoopFailure = []

// Cleanup in each nested iteration can fail independently; later outer work is then skipped.
func nestedIterationFailure(count: int): () throws LoopFailure {
    for var outer: int = 0; outer < count; outer += 1 {
        defer { record(outer + 300) }
        for var inner: int = 0; inner < 2; inner += 1 {
            defer { if inner == 1 { failLoopCleanup() } }
        }
        record(outer + 301)
    }
}

// The outer iteration cleanup executes while unwinding an inner cleanup failure.
func nestedFailureDrainsOuterIteration(): () throws LoopFailure {
    for {
        defer { record(310) }
        for {
            defer { failLoopCleanup() }
            break
        }
    }
}

// A loop scoped action is not repeated each time the body backedge reaches the header.
func enclosingScopeActionRunsOnce(count: int): () {
    defer { record(320) }
    for var index: int = 0; index < count; index += 1 {
        record(index + 321)
    }
}

// Multiple actions registered during one iteration execute in reverse order before update.
func perIterationLifoBeforeUpdate(count: int): () {
    for var index: int = 0; index < count; index += 1 {
        defer { record(index + 330) }
        defer { record(index + 331) }
        record(index + 332)
    }
}

// A loop with no reachable break has no ordinary post-loop edge, but throwing cleanup still
// contributes an exceptional edge on every completed iteration.
func infiniteLoopWithCleanup(): ! {
    for {
        defer { record(340) }
        continue
    }
}

// A reachable break contributes the only post-loop normal edge.
func breakIsOnlyNormalExit(stop: bool): int {
    for {
        defer { record(350) }
        if stop { break }
    }
    351
}

// A return from an inner loop body skips both loop updates and unwinds active iteration scopes.
func returnFromNestedLoops(): int {
    for var outer: int = 0; outer < 2; outer += 1 {
        defer { record(360) }
        for var inner: int = 0; inner < 2; inner += 1 {
            defer { record(361) }
            return outer + inner
        }
    }
    -1
}

// Specification note: per-iteration registrations never accumulate until the function exits.
// Specification note: break and continue select resolved loop targets before cleanup planning.
// Specification note: cleanup failure suppresses the pending transfer and skips its next step.
// Specification note: loop updates and collection cursor moves occur only after cleanup succeeds.
// Specification note: empty loops create no body registration and no cleanup call.
// Specification note: inner-loop transfers preserve actions belonging to still-active outer scopes.
