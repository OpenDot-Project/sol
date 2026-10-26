// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: nested scopes drain from inner to outer.
module spec.cleanup.scopenesting

func record(value: int): () {}

// The block action runs before the next outer statement.
func ordinaryNestedBlock(): () {
    defer { record(1) }
    {
        defer { record(2) }
        record(3)
    }
    record(4)
}

// Nested cleanup can mutate a value observed by an outer cleanup.
func innerMutationVisibleOutside(): () {
    var state = 10
    defer { record(state) }
    {
        defer { state = 11 }
        state = 12
    }
}

// The outer action is not drained when only the inner block exits.
func outerLifetimeContinues(): () {
    defer { record(20) }
    {
        defer { record(21) }
    }
    record(22)
}

// Several nested levels drain one level at a time.
func threeLevels(): () {
    defer { record(30) }
    {
        defer { record(31) }
        {
            defer { record(32) }
            record(33)
        }
        record(34)
    }
    record(35)
}

// Actions in a nested block use LIFO within that block before outer LIFO resumes.
func localOrderThenOuterOrder(): () {
    defer { record(40) }
    defer { record(41) }
    {
        defer { record(42) }
        defer { record(43) }
        record(44)
    }
    record(45)
}

// Each if arm is an independent block for cleanup lifetime.
func ifArmScope(flag: bool): () {
    defer { record(50) }
    if flag {
        defer { record(51) }
        record(52)
    } else {
        defer { record(53) }
        record(54)
    }
    record(55)
}

// Nested if exits the inner arm before the outer arm continues.
func nestedIfScopes(outer: bool, inner: bool): () {
    if outer {
        defer { record(60) }
        if inner {
            defer { record(61) }
            record(62)
        }
        record(63)
    }
}

// A local in the inner scope remains available until its cleanup completes.
func innerLocalLifetime(): () {
    {
        val value = 70
        defer { record(value) }
    }
}

// The outer scope local remains available to both inner and outer cleanup.
func outerLocalLifetime(): () {
    val value = 71
    defer { record(value) }
    {
        defer { record(value + 1) }
    }
}

// A shadowed local has distinct storage identity for its action.
func shadowedCleanupNames(): () {
    val value = 80
    defer { record(value) }
    {
        val value = 81
        defer { record(value) }
    }
}

// Exiting a switch case drains that case before dispatch returns.
func switchCaseScope(code: int): () {
    defer { record(90) }
    switch code {
        case 0:
            defer { record(91) }
            record(92)
        case 1:
            defer { record(93) }
            record(94)
        default:
            record(95)
    }
    record(96)
}

// A return in one case drains the case and every enclosing scope it leaves.
func returnFromCaseDrainsEnclosing(code: int): int {
    defer { record(100) }
    switch code {
        case 0:
            defer { record(101) }
            return 102
        default:
            return 103
    }
}

// A catch handler has its own cleanup scope nested within the callable.
class ScopeFailure: Exception {}

func catchHasNestedOwner(): () throws ScopeFailure {
    defer { record(110) }
    try {
        throw ScopeFailure()
    } catch (error: ScopeFailure) {
        defer { record(111) }
        record(112)
    }
    record(113)
}

// A throw from a handler drains handler cleanup before reaching the outer callable cleanup.
func throwFromHandlerDrainsOutward(): () throws ScopeFailure {
    defer { record(120) }
    try {
        throw ScopeFailure()
    } catch (error: ScopeFailure) {
        defer { record(121) }
        throw ScopeFailure()
    }
}

// A nested try has a cleanup region that ends before the outer body continues.
func nestedTryScopes(): () throws ScopeFailure {
    defer { record(130) }
    try {
        try {
            defer { record(131) }
            throw ScopeFailure()
        } catch (error: ScopeFailure) {
            defer { record(132) }
        }
        record(133)
    } catch (error: ScopeFailure) {
        record(134)
    }
}

// The defer in the try body drains before the associated finally body.
func tryBodyBeforeFinally(): () {
    try {
        defer { record(140) }
        record(141)
    } finally {
        record(142)
    }
}

// An outer defer remains pending until finally has completed.
func outerScopeAfterFinally(): () {
    defer { record(150) }
    try {
        defer { record(151) }
        record(152)
    } finally {
        record(153)
    }
    record(154)
}

// A defer inside the finally body is nested in that body's own scope.
func finallyNestedDefer(): () {
    try {
        record(160)
    } finally {
        defer { record(161) }
        record(162)
    }
}

// A nested lambda is a separate callable scope and owns its defer.
func lambdaOwnsItsCleanup(): () {
    val action: () -> int = {
        defer { record(170) }
        171
    }
    action()
}

// Returning a closure does not transfer the enclosing scope's defer to the closure.
func closureDoesNotOwnOuterCleanup(): () -> int {
    defer { record(180) }
    { -> 181 }
}

// A defer declared inside the lambda runs when that lambda invocation ends.
func closureHasInvocationCleanup(): () -> int {
    { ->
        defer { record(190) }
        191
    }
}

// Invoking the returned lambda creates a new dynamic cleanup scope on every call.
func invokeRepeatedly(): () {
    val action = closureHasInvocationCleanup()
    action()
    action()
}

// A nested ordinary block can be exited by return; its cleanup precedes the outer cleanup.
func returnLeavesNestedBlock(): int {
    defer { record(200) }
    {
        defer { record(201) }
        return 202
    }
}

// A nested block can be exited by a throw; every exited scope participates.
func throwLeavesNestedBlock(): () throws ScopeFailure {
    defer { record(210) }
    {
        defer { record(211) }
        throw ScopeFailure()
    }
}

// Nested scopes do not share a single unordered action collection.
func visibleMutationOrder(): () {
    var step = 220
    defer { record(step) }
    {
        defer { step = 221 }
        {
            defer { step = 222 }
        }
        record(step)
    }
}

// Sibling blocks finish independently; the first block's action is already consumed.
func siblingScopesDoNotOverlap(): () {
    {
        defer { record(230) }
        record(231)
    }
    {
        defer { record(232) }
        record(233)
    }
}

// A later sibling cannot alter whether a previous sibling was registered.
func siblingActivationIsLocal(flag: bool): () {
    if flag {
        defer { record(240) }
    }
    if !flag {
        defer { record(241) }
    }
}

// Inner cleanup may update a local read by a later statement and outer cleanup.
func writeThenReadAcrossScope(): () {
    var value = 250
    defer { record(value) }
    {
        defer { value += 1 }
    }
    record(value)
}

// The outer action is still active when control enters a nested loop scope.
func loopNestedInOuterScope(count: int): () {
    defer { record(260) }
    for var index: int = 0; index < count; index += 1 {
        defer { record(index) }
    }
    record(261)
}

// A block nested in one iteration drains before the iteration body's action.
func nestedBlockInsideIteration(count: int): () {
    for var index: int = 0; index < count; index += 1 {
        defer { record(index + 270) }
        {
            defer { record(index + 280) }
        }
        record(index + 290)
    }
}

// An inner switch scope does not end the surrounding loop-body scope.
func switchInsideLoop(count: int): () {
    for var index: int = 0; index < count; index += 1 {
        defer { record(index + 300) }
        switch index {
            case 0:
                defer { record(index + 310) }
            default:
                record(index + 320)
        }
        record(index + 330)
    }
}

// The catch scope completes before statements after the try/catch.
func catchScopeBeforeContinuation(flag: bool): () throws ScopeFailure {
    try {
        if flag { throw ScopeFailure() }
    } catch (error: ScopeFailure) {
        defer { record(340) }
    }
    record(341)
}

// The try body and catch body are separate owners, not one shared defer stack.
func tryAndCatchStacks(flag: bool): () throws ScopeFailure {
    defer { record(350) }
    try {
        defer { record(351) }
        if flag { throw ScopeFailure() }
    } catch (error: ScopeFailure) {
        defer { record(352) }
        record(353)
    }
}

// Each nested scope is drained in order even if a later statement is not reached.
func earlyReturnFromInner(flag: bool): int {
    defer { record(360) }
    {
        defer { record(361) }
        if flag { return 362 }
        record(363)
    }
    364
}

// Each path must retain exact ownership when a branch completes normally or returns.
func branchJoinAfterInnerReturn(flag: bool): int {
    defer { record(370) }
    if flag {
        defer { record(371) }
        return 372
    }
    373
}

// Specification note: each lexical owner drains only actions registered in its instance.
// Specification note: a completed inner stack cannot run again when an outer scope later exits.
// Specification note: a transfer leaving several owners drains them in strict inner-to-outer order.
// Specification note: a continuation inside an owner leaves that owner's registrations active.
// Specification note: an action can read an outer local while that local is still in lifetime.
// Specification note: a sibling block has a fresh cleanup stack even at the same source depth.
// Specification note: try, catch, finally, lambda, loop, branch, and ordinary block scopes compose.
