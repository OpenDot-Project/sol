// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: every source exit drains exactly the scopes it leaves.
module spec.cleanup.exitforms

func record(value: int): () {}

class ExitFailure: Exception {}
func failExitCleanup(): () throws ExitFailure { throw ExitFailure() }

// An ordinary call that raises is a valid cleanup operation.
func allowedCallThatRaises(): () throws ExitFailure {
    defer { failExitCleanup() }
}

// Negative syntax vector: an escaping explicit throw in defer/finally is SOL2254.
func forbiddenEscapingCleanupThrow(): () {
    defer {
        // Rejected if uncommented: throw ExitFailure()
        record(0)
    }
    try {
        record(1)
    } finally {
        // Rejected if uncommented: throw ExitFailure()
        record(2)
    }
}

// Ordinary function fallthrough drains the callable scope.
func normalFallthrough(): () {
    defer { record(1) }
    record(2)
}

// Empty normal completion also drains a registered action.
func emptyTail(): () {
    defer { record(10) }
}

// Return expression is evaluated before the action.
func returnValueAfterAction(): int {
    defer { record(20) }
    return compute(21)
}

func compute(value: int): int = value

// A tail expression is saved before cleanup and delivered after cleanup.
func tailResultAfterCleanup(): int {
    defer { record(30) }
    compute(31)
}

// A nested return drains the inner scope and then the callable scope.
func returnFromNestedScope(): int {
    defer { record(40) }
    {
        defer { record(41) }
        return 42
    }
}

// Return in a branch follows the same rule for only the branch path.
func conditionalReturn(flag: bool): int {
    defer { record(50) }
    if flag {
        defer { record(51) }
        return 52
    }
    53
}

// Throw selects the primary exception before cleanup.
func explicitThrow(): ! throws ExitFailure {
    defer { record(60) }
    throw ExitFailure()
}

// A throw from a nested block drains both nested and enclosing owners.
func nestedThrow(): ! throws ExitFailure {
    defer { record(70) }
    {
        defer { record(71) }
        throw ExitFailure()
    }
}

// A return statement does not run after a thrown cleanup failure.
func cleanupCancelsReturn(): int throws ExitFailure {
    defer { failExitCleanup() }
    return 80
}

// A successful cleanup leaves the saved return value intact.
func cleanupPreservesReturn(): int {
    defer { record(90) }
    return 91
}

// A finally block completes before a pending return is observable.
func finallyBeforeReturn(): int {
    try {
        return 100
    } finally {
        record(101)
    }
}

// A throw through finally preserves the original primary if finally completes.
func throwThroughFinally(): ! throws ExitFailure {
    try {
        throw ExitFailure()
    } finally {
        record(110)
    }
}

// A call that fails inside finally is suppressed under the operation's primary failure.
func throwAndFailInFinally(): ! throws ExitFailure {
    try {
        failExitCleanup()
    } finally {
        throw ExitFailure()
    }
}

// A catch can recover, after which the containing scope exits normally.
func caughtThrowThenFallthrough(): () {
    defer { record(120) }
    try {
        throw ExitFailure()
    } catch (error: ExitFailure) {
        record(121)
    }
    record(122)
}

// A throw in a catch body leaves its handler and enclosing callable scopes.
func throwFromCatch(): () throws ExitFailure {
    defer { record(130) }
    try {
        throw ExitFailure()
    } catch (error: ExitFailure) {
        defer { record(131) }
        throw ExitFailure()
    }
}

// Break leaves the loop-body scope and continues after the loop.
func breakTransfer(): () {
    for {
        defer { record(140) }
        break
    }
    record(141)
}

// Continue leaves the current iteration, then returns to the loop condition.
func continueTransfer(count: int): () {
    for var index: int = 0; index < count; index += 1 {
        defer { record(index + 150) }
        continue
    }
}

// A return from a loop leaves the iteration, loop, and callable scopes as applicable.
func returnFromIteration(): int {
    defer { record(160) }
    for var index: int = 0; index < 2; index += 1 {
        defer { record(index + 161) }
        return index
    }
    -1
}

// Break from an inner loop leaves only that inner iteration and loop body.
func innerBreakOnly(): () {
    defer { record(170) }
    for {
        defer { record(171) }
        for {
            defer { record(172) }
            break
        }
        record(173)
        break
    }
}

// Continue from an inner loop leaves its own iteration but keeps the outer iteration active.
func innerContinueOnly(): () {
    for var outer: int = 0; outer < 2; outer += 1 {
        defer { record(180) }
        for var inner: int = 0; inner < 2; inner += 1 {
            defer { record(181) }
            continue
        }
        record(182)
    }
}

// A nested local loop inside cleanup has transfers local to that cleanup body.
func localLoopInCleanup(): () {
    defer {
        for {
            defer { record(190) }
            break
        }
    }
}

// Negative semantic form: a break in cleanup cannot target the enclosing loop.
func forbiddenBreakShape(): () {
    for {
        defer {
            // Rejected if uncommented: break
            record(200)
        }
        break
    }
}

// Negative semantic form: a continue in cleanup cannot replace the pending return.
func forbiddenContinueShape(): int {
    for {
        defer {
            // Rejected if uncommented: continue
            record(210)
        }
        return 211
    }
}

// Negative semantic form: cleanup cannot return from the owning callable.
func forbiddenReturnShape(): () {
    defer {
        // Rejected if uncommented: return
        record(220)
    }
}

// A bottom-valued throw operation has no normal continuation after unwind.
func bottomThrowEdge(): ! throws ExitFailure {
    defer { record(230) }
    throw ExitFailure()
}

// A call returning bottom can have an exceptional cleanup path.
func bottomCallEdge(): ! throws ExitFailure {
    defer { record(240) }
    terminate()
}

func terminate(): ! throws ExitFailure { throw ExitFailure() }

// All paths return, but each active branch action runs before the return reaches its caller.
func exhaustiveReturns(flag: bool): int {
    if flag {
        defer { record(250) }
        return 251
    } else {
        defer { record(252) }
        return 253
    }
}

// A non-completing branch contributes no normal post-branch predecessor.
func throwOrReturn(flag: bool): int throws ExitFailure {
    defer { record(260) }
    if flag {
        throw ExitFailure()
    }
    return 261
}

// A transfer that exits several nested blocks drains each block in order.
func returnThroughSeveralBlocks(): int {
    defer { record(270) }
    {
        defer { record(271) }
        {
            defer { record(272) }
            return 273
        }
    }
}

// A transfer that stays inside a block does not drain that block's action.
func localContinuation(): () {
    defer { record(280) }
    if false {
        record(281)
    }
    record(282)
}

// An exception caught within the same scope is handled before the scope's action.
func localCatchDoesNotExitOwner(): () {
    defer { record(290) }
    try {
        throw ExitFailure()
    } catch (error: ExitFailure) {
        record(291)
    }
    record(292)
}

// A rethrow exits the catch and all surrounding scopes that are no longer active.
func catchRethrow(): () throws ExitFailure {
    defer { record(300) }
    try {
        throw ExitFailure()
    } catch (error: ExitFailure) {
        defer { record(301) }
        throw error
    }
}

// A safe normal cleanup allows the original return to proceed.
func ordinaryCleanupOnValue(): String {
    defer { record(310) }
    "value"
}

// Return expression side effects occur before the pending return enters cleanup.
func returnExpressionBeforeCleanup(): int {
    defer { record(320) }
    return recordAndGet()
}

func recordAndGet(): int {
    record(321)
    322
}

// If return-expression evaluation throws, the function cleanup still runs.
func failingReturnExpression(): int throws ExitFailure {
    defer { record(330) }
    return failBeforeValue()
}

func failBeforeValue(): int throws ExitFailure { throw ExitFailure() }

// The action is not run if execution never enters the nested block containing it.
func skippedNestedOwner(enter: bool): () {
    if enter {
        defer { record(340) }
    }
}

// Pending return values are rooted through all outer cleanup scopes.
func referenceReturnRooted(): String {
    defer { record(350) }
    {
        defer { record(351) }
        return createText()
    }
}

func createText(): String = "rooted"

// Exception identity is not reconstructed from its type name during cleanup.
func primaryObjectIdentity(): ! throws ExitFailure {
    val primary = ExitFailure()
    defer { record(360) }
    throw primary
}

// The exit kind and target are resolved before selecting cleanup scopes.
func targetLoopResolution(stop: bool): int {
    defer { record(370) }
    for {
        defer { record(371) }
        if stop { break }
        return 372
    }
    373
}

// Specification note: return, throw, break, and continue are distinct transfer kinds.
// Specification note: each transfer drains only scopes between its source and resolved target.
// Specification note: cleanup completion can preserve or cancel a pending normal transfer.
// Specification note: cleanup cannot synthesize a return value or retarget a loop transfer.
// Specification note: `!` excludes normal flow but does not erase an exceptional edge.
