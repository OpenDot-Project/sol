// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: registration activates only when control reaches defer.
module spec.cleanup.registration

func record(value: int): () {}
func persist(text: String): () {}

// The call inside the action executes after ordinary body statements.
func registrationDoesNotExecuteImmediately(): () {
    defer { record(1) }
    record(2)
    record(3)
}

// Expected order is 2, 3, 1. Registration records a future action.
func bodyOrderPrecedesAction(): () {
    defer { record(1) }
    record(2)
    record(3)
}

// An action declared in the true arm belongs to that arm's scope.
func trueArmRegistersOnlyWhenEntered(flag: bool): () {
    if flag {
        defer { record(10) }
        record(11)
    }
    record(12)
}

// The else-arm registration is absent from the true edge.
func falseArmHasIndependentRegistration(flag: bool): () {
    if flag {
        record(20)
    } else {
        defer { record(21) }
        record(22)
    }
    record(23)
}

// Both branches register a distinct action, but only one action is active.
func exclusiveRegistrationIdentities(flag: bool): () {
    if flag {
        defer { record(30) }
    } else {
        defer { record(31) }
    }
    record(32)
}

// The branch-local action runs before execution reaches the post-branch statement.
func branchActionPrecedesJoin(flag: bool): () {
    if flag {
        defer { record(40) }
        record(41)
    }
    record(42)
}

// A nested branch has its own registration point within the arm scope.
func nestedConditionSkipsInnerAction(outer: bool, inner: bool): () {
    if outer {
        if inner {
            defer { record(50) }
            record(51)
        }
        record(52)
    }
    record(53)
}

// Reaching the outer declaration does not activate a declaration in a skipped branch.
func outerAndInnerRegistrations(outer: bool, inner: bool): () {
    defer { record(60) }
    if outer {
        defer { record(61) }
        if inner {
            defer { record(62) }
            record(63)
        }
        record(64)
    }
    record(65)
}

// A return before the registration exits without the later action.
func returnBeforeRegistration(stop: bool): int {
    if stop {
        return 70
    }
    defer { record(71) }
    return 72
}

// A throw before the registration cannot run it.
class RegistrationFailure: Exception {}

func throwBeforeRegistration(stop: bool): int throws RegistrationFailure {
    if stop {
        throw RegistrationFailure()
    }
    defer { record(73) }
    74
}

// A defer following a potentially throwing call is active only if that call completes.
func callBeforeRegistration(flag: bool): () throws RegistrationFailure {
    mayFail(flag)
    defer { record(80) }
    record(81)
}

func mayFail(flag: bool): () throws RegistrationFailure {
    if flag { throw RegistrationFailure() }
}

// Registration in the true branch is not hoisted before the condition.
func conditionSideEffectsPrecedeRegistration(flag: bool): () {
    if inspect(flag) {
        defer { record(90) }
        record(91)
    }
}

func inspect(flag: bool): bool = flag

// Registration is a statement and produces no expression result.
func statementHasNoValue(): () {
    defer { record(100) }
}

// An empty cleanup is valid and does not change the function result.
func emptyAction(): int {
    defer {}
    101
}

// The action body reads the live local when cleanup runs.
func observesCurrentValue(): () {
    var state = 110
    defer { record(state) }
    state = 111
}

// An explicit immutable local provides a snapshot at the chosen point.
func observesSnapshot(): () {
    var state = 120
    val snapshot = state
    defer { record(snapshot) }
    state = 121
}

// Reassigning after an explicit snapshot does not mutate that snapshot.
func snapshotBeforeMutation(): () {
    var state = 130
    val before = state
    state = 131
    defer { record(before) }
}

// A snapshot made after mutation records the later value.
func snapshotAfterMutation(): () {
    var state = 140
    state = 141
    val after = state
    defer { record(after) }
}

// Registration order, not local declaration names, determines the stack.
func twoReachedDeclarations(): () {
    defer { record(150) }
    defer { record(151) }
    record(152)
}

// Each path has its own active suffix after the shared prefix.
func sharedPrefixDistinctSuffix(flag: bool): () {
    defer { record(160) }
    if flag {
        defer { record(161) }
    } else {
        defer { record(162) }
    }
    record(163)
}

// A second branch can return before its registration point.
func branchReturnSkipsLaterAction(flag: bool): () {
    if flag {
        defer { record(170) }
        return
    }
    return
}

// The first path activates the action; the second path reaches the join without it.
func registrationJoinIsPathSensitive(flag: bool): () {
    if flag {
        defer { record(180) }
        record(181)
    }
    record(182)
}

// A catch body owns its cleanup independently from the try body.
func catchRegistration(fail: bool): () throws RegistrationFailure {
    try {
        if fail { throw RegistrationFailure() }
        record(190)
    } catch (error: RegistrationFailure) {
        defer { record(191) }
        record(192)
    }
}

// A registration in one switch case cannot leak into another case.
func caseRegistration(code: int): () {
    switch code {
        case 0:
            defer { record(200) }
            record(201)
        case 1:
            defer { record(202) }
            record(203)
        default:
            record(204)
    }
}

// A switch whose case returns runs only that case's registered action.
func returnFromCase(code: int): int {
    switch code {
        case 0:
            defer { record(210) }
            return 0
        case 1:
            defer { record(211) }
            return 1
        default:
            return -1
    }
}

// A cleanup may mutate a local before a subsequent outer cleanup reads it.
func mutationVisibleToLaterCleanup(): () {
    var value = 220
    defer { record(value) }
    defer { value = 221 }
}

// Actions are not ordered by numeric payload; registration order alone controls them.
func valuesDoNotSortActions(): () {
    defer { record(9) }
    defer { record(1) }
    defer { record(5) }
}

// Two equal bodies at different source positions remain different registrations.
func identicalBodyOccurrences(flag: bool): () {
    defer { record(230) }
    if flag {
        defer { record(230) }
    }
}

// Repeated execution of a branch creates a fresh branch-scope action per entry.
func repeatedlyEnteredBranch(count: int): () {
    for var index: int = 0; index < count; index += 1 {
        if index % 2 == 0 {
            defer { record(index) }
            record(index + 240)
        }
    }
}

// A branch with no registration must not consume an action from another branch.
func noPhantomAction(flag: bool): () {
    if flag {
        defer { record(250) }
    } else {
        record(251)
    }
}

// Registration after a nested block belongs to the enclosing function scope.
func registrationAfterNestedScope(flag: bool): () {
    if flag {
        defer { record(260) }
    }
    defer { record(261) }
    record(262)
}

// Registration before the nested scope remains active after that scope drains.
func registrationBeforeNestedScope(flag: bool): () {
    defer { record(270) }
    if flag {
        defer { record(271) }
    }
    record(272)
}

// A defer in an expression-producing branch still belongs to the branch scope.
func tryBranchRegistration(flag: bool): int {
    val result = if flag {
        defer { record(280) }
        281
    } else {
        282
    }
    result
}

// Action registration does not execute or cache the value of this call.
func callIsDelayed(): () {
    defer { persist(createMessage()) }
    record(290)
}

func createMessage(): String = "cleanup"

// An explicit value computed earlier can be used when an eager snapshot is required.
func eagerMessageSnapshot(): () {
    val message = createMessage()
    defer { persist(message) }
}

// An action in a finally body executes as part of that cleanup body's own scope.
func finallyOwnsNestedRegistration(): () {
    try {
        record(300)
    } finally {
        defer { record(301) }
        record(302)
    }
}

// An outer defer is still pending while the try/finally region executes.
func outerRegistrationSurvivesFinally(): () {
    defer { record(310) }
    try {
        record(311)
    } finally {
        record(312)
    }
}

// A defer reached after a catch joins the containing callable scope.
func afterCatchRegistration(fail: bool): () throws RegistrationFailure {
    try {
        if fail { throw RegistrationFailure() }
    } catch (error: RegistrationFailure) {
        record(320)
    }
    defer { record(321) }
}

// A local nested loop may have its own registration without altering its caller's owner.
func loopInsideActionBody(count: int): () {
    defer {
        for var index: int = 0; index < count; index += 1 {
            defer { record(index) }
        }
    }
}

// Captured mutable state is shared according to ordinary closure rules.
func mutableCaptureUsesSharedCell(): () {
    var total = 330
    val increment = { total += 1 }
    defer { record(total) }
    increment()
}

// Immutable captures preserve their bound value.
func immutableCaptureKeepsValue(): () {
    val fixed = 340
    defer { record(fixed) }
}

// The loop parameter/local remains in its lexical lifetime through iteration cleanup.
func iterationLocalAvailableAtCleanup(count: int): () {
    for var index: int = 0; index < count; index += 1 {
        defer { record(index) }
    }
}

// Negative semantic shape: a defer after an unconditional return is unreachable and cannot
// register. The body below is intentionally expressed as a comment, not invalid fixture syntax.
func unreachableRegistrationShape(): int {
    return 350
    // Rejected if uncommented: defer { record(351) }
}

// Negative semantic shape: a deferred action cannot be activated by a skipped branch.
func skippedBranchExpectation(flag: bool): () {
    if flag {
        defer { record(360) }
    }
    // Expected on false: no call to record(360).
}

// A cleanup body has no value that can be consumed by its owner.
func actionResultIsDiscarded(): int {
    defer {
        val ignored = 370
        record(ignored)
    }
    371
}

// Registration does not turn the surrounding callable into a closure.
func ownerStillReturnsNormally(): String {
    defer { record(380) }
    "owner"
}

// Ordered action identities survive a branch after a common registration.
func branchStackSuffix(flag: bool): () {
    defer { record(390) }
    if flag {
        defer { record(391) }
    }
    record(392)
}

// Two possible registration sites with common exit have distinct active states.
func chooseRegistration(flag: bool): () {
    if flag {
        defer { record(400) }
        record(401)
    } else {
        defer { record(402) }
        record(403)
    }
    record(404)
}

// A nested branch's cleanup is drained before its containing branch continues.
func nestedBranchDrain(order: bool): () {
    if order {
        defer { record(410) }
        if order {
            defer { record(411) }
            record(412)
        }
        record(413)
    }
}

// When the nested condition is false, only the containing action is active.
func nestedBranchSkip(order: bool, inner: bool): () {
    if order {
        defer { record(420) }
        if inner {
            defer { record(421) }
        }
        record(422)
    }
}

// Specification note: path joins preserve registration identity, not only a count.
// Specification note: path joins preserve the stack order on each reachable edge.
// Specification note: a dead registration site contributes no action to runtime state.
// Specification note: cleanup bodies use normal checked-effect analysis at execution time.
// Specification note: a body that reads a local must satisfy definite assignment at that point.
// Specification note: no collector finalizer participates in the source cleanup schedule.
// Specification note: filesystem order cannot change which branch registered an action.
