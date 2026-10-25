// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: cleanup writes and exceptional edges update flow state precisely.
module spec.cleanup.stateedges

class StateFailure: Exception {}

func record(value: int): () {}
func failStateCleanup(): () throws StateFailure { throw StateFailure() }

// An out parameter assigned by a cleanup is assigned after normal function completion.
func outAssignedAtFallthrough(out result: int): () {
    defer { result = 1 }
}

// A pending return also waits for cleanup assignment before returning normally.
func outAssignedAtReturn(out result: int): () {
    defer { result = 2 }
    return
}

// Every normally exiting path must run an assignment cleanup.
func branchOutAssignment(flag: bool, out result: int): () {
    if flag {
        defer { result = 3 }
        return
    }
    defer { result = 4 }
}

// A path that skips the registration does not satisfy the out promise.
func missingConditionalOutAssignment(flag: bool, out result: int): () {
    if flag {
        defer { result = 5 }
    }
    // Negative: false path reaches a normal exit without assigning result.
}

// Assignment is counted only after the cleanup body writes the exact out storage.
func outAssignmentUsesLocation(out result: int): () {
    defer { result = 6 }
}

// A cleanup read before its write is not justified by the caller's out storage.
func outReadBeforeCleanupWrite(out result: int): () {
    defer {
        record(result)
        result = 7
    }
}

// A normal assignment before return is visible even if cleanup reads the same location.
func outAssignedBeforeAndDuringCleanup(out result: int): () {
    result = 8
    defer { record(result); result = 9 }
    return
}

// A throw from the protected body does not promise caller assignment on the exceptional edge.
func outOnThrowPath(out result: int): ! throws StateFailure {
    defer { result = 10 }
    throw StateFailure()
}

// Cleanup may write the location on unwind; this is not a normal-return out guarantee.
func outCleanupThenThrow(out result: int): ! throws StateFailure {
    defer { result = 11 }
    throw StateFailure()
}

// If cleanup itself throws after assigning, normal return is still absent.
func outCleanupWritesThenFails(out result: int): () throws StateFailure {
    defer { result = 12; failStateCleanup() }
}

// A later cleanup failure does not restore normal completion.
func multipleOutCleanupsOneFails(out result: int): () throws StateFailure {
    defer { result = 13 }
    defer { failStateCleanup() }
}

// The assignment action runs before the older failure action under LIFO.
func assignmentBeforeOlderFailure(out result: int): () throws StateFailure {
    defer { failStateCleanup() }
    defer { result = 14 }
}

// A cleanup assignment can complete a pending return, but not a pending throw.
func pendingReturnOut(out result: int): () {
    defer { result = 15 }
    return
}

// Ref parameters start assigned and cleanup writes through the caller alias.
func refUpdatedByCleanup(ref value: int): () {
    defer { value += 1 }
}

// The alias is shared; subsequent reads observe the cleanup mutation.
func readRefAfterCall(): int {
    var value = 16
    refUpdatedByCleanup(ref value)
    value
}

// A pending return value is evaluated before the cleanup mutates its source local.
func returnSnapshotBeforeCleanup(): int {
    var value = 17
    defer { value = 18 }
    return value
}

// A tail expression is likewise saved before cleanup runs.
func tailSnapshotBeforeCleanup(): int {
    var value = 19
    defer { value = 20 }
    value
}

// The mutation is visible after a nested scope exits normally.
func nestedWriteVisible(): int {
    var value = 21
    {
        defer { value = 22 }
    }
    value
}

// A nested write occurs before outer cleanup reads the value.
func innerWriteBeforeOuterRead(): () {
    var value = 23
    defer { record(value) }
    {
        defer { value = 24 }
    }
}

// An exceptional edge observes assignments completed before the throwing operation.
func assignmentBeforeThrow(): int throws StateFailure {
    var value = 25
    try {
        value = 26
        throw StateFailure()
    } catch (error: StateFailure) {
        return value
    }
}

// An assignment after the throw is unreachable and cannot flow to the handler.
func assignmentAfterThrow(): int throws StateFailure {
    var value: int
    try {
        throw StateFailure()
        value = 27
    } catch (error: StateFailure) {
        return value
    }
}

// A cleanup write on the exceptional edge occurs before the enclosing handler receives it.
func catchSeesCleanupWrite(): int {
    var value = 28
    try {
        defer { value = 29 }
        throw StateFailure()
    } catch (error: StateFailure) {
        return value
    }
}

// If cleanup also throws, its write remains completed before the catch receives primary.
func catchSeesWriteBeforeCleanupFailure(): int {
    var value = 30
    try {
        defer { value = 31; failStateCleanup() }
        throw StateFailure()
    } catch (error: StateFailure) {
        return value
    }
}

// A cleanup operation that throws before a later write does not establish that later value.
func catchDoesNotSeeAfterCleanupThrow(): int {
    var value: int
    try {
        defer { failStateCleanup(); value = 32 }
        throw StateFailure()
    } catch (error: StateFailure) {
        return value
    }
}

// The analyzer must represent the exact order of writes inside cleanup.
func cleanupWriteSequence(): int {
    var value = 33
    defer { value = 34; record(value); value = 35 }
    value
}

// A conditional cleanup assignment is definite only if every normal exit registers it.
func allBranchEdgesAssign(flag: bool, out result: int): () {
    if flag {
        defer { result = 36 }
    } else {
        defer { result = 37 }
    }
}

// A conditional statement inside one action may leave a path without assignment.
func actionBranchMissingAssignment(flag: bool, out result: int): () {
    defer {
        if flag { result = 38 }
    }
}

// A loop body cleanup cannot establish an out value if the loop may execute zero times.
func loopMaySkipOut(count: int, out result: int): () {
    for var index: int = 0; index < count; index += 1 {
        defer { result = index }
    }
}

// A reachable break edge still leaves through the active iteration cleanup.
func breakAssignsOut(out result: int): () {
    for {
        defer { result = 39 }
        break
    }
}

// A continue edge drains the iteration cleanup before reaching the next condition.
func continueAssignsOut(count: int, out result: int): () {
    for var index: int = 0; index < count; index += 1 {
        defer { result = index }
        continue
    }
}

// Zero iterations prevent the function-level out proof despite body assignments.
func noIterationMeansUnassigned(count: int, out result: int): () {
    for var index: int = 0; index < count; index += 1 {
        defer { result = index }
    }
}

// Refined local facts are invalidated when cleanup writes the local.
func cleanupKillsNonNullFact(value: Object?): int {
    var current = value
    if current == null { return 0 }
    defer { current = null }
    current.hashCode()
}

// A cleanup that may run later cannot be treated as having executed at registration time.
func refinementBeforeCleanup(value: Object?): int {
    var current = value
    if current == null { return 0 }
    defer { current = null }
    current.hashCode()
}

// A closure capturing a mutable local shares its cell with a deferred action.
func sharedMutableCapture(): int {
    var current = 40
    val increment = { current += 1 }
    defer { record(current) }
    increment()
    current
}

// Reassignment in a defer is not visible before the scope exit.
func cleanupWriteIsDelayed(): int {
    var value = 41
    defer { value = 42 }
    value
}

// Assignment order inside a return expression is completed before cleanup starts.
func sideEffectingReturnValue(): int {
    var value = 43
    defer { record(value) }
    value = 44
    value
}

// Cleanup effects and assignment facts are associated with the same active action identity.
func effectAndWriteTogether(out value: int): () throws StateFailure {
    defer { value = 45; failStateCleanup() }
}

// This function has no normal exit, so an out guarantee is not established for callers.
func throwOnlyOut(out value: int): ! throws StateFailure {
    defer { value = 46 }
    throw StateFailure()
}

// A later exceptional operation does not erase earlier successful cleanup writes.
func nestedWriteThenThrow(): int {
    var value = 47
    try {
        {
            defer { value = 48 }
            throw StateFailure()
        }
    } catch (error: StateFailure) {
        return value
    }
}

// Specification note: normal-return promises and exceptional-edge writes are separate facts.
// Specification note: cleanup assignments occur at the cleanup point, not the registration point.
// Specification note: return expressions are saved before cleanup can mutate their source locals.
// Specification note: cleanup reads/writes use the exact local or parameter storage identity.
// Specification note: effect failure does not roll back earlier cleanup side effects.
