// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative try/cleanup fixture. Syntax validation does not verify exception edges.
module spec.flow.trycleanup

class Failure: Exception {}
class OtherFailure: Exception {}

func mayFail(flag: bool): int throws Failure {
    if flag {
        throw Failure()
    }
    10
}

func catchesFailure(flag: bool): int {
    try {
        mayFail(flag)
    } catch (failure: Failure) {
        20
    }
}

func catchReceivesPreTryState(flag: bool): int {
    var value: int
    try {
        mayFail(flag)
        value = 1
    } catch (failure: Failure) {
        // Expected: value is not definitely assigned on the throw edge.
        return value
    }
    value
}

func earlierWriteReachesCatch(flag: bool): int {
    var value: int
    value = 0
    try {
        mayFail(flag)
        value = 1
    } catch (failure: Failure) {
        // Expected: the write before the call dominates the catch edge.
        return value
    }
    value
}

func handlerEffectEscapes(flag: bool): int throws OtherFailure {
    try {
        mayFail(flag)
    } catch (failure: Failure) {
        throw OtherFailure()
    }
}

func handlerEffectNeedsCoverage(flag: bool): int {
    try {
        mayFail(flag)
    } catch (failure: Failure) {
        // Expected: the handler's OtherFailure is a new outgoing effect.
        throw OtherFailure()
    }
}

func subtypeCatchRemovesContainedEffect(flag: bool): int throws Failure {
    try {
        mayFail(flag)
    } catch (failure: Failure) {
        0
    }
}

func subtypeCatchDoesNotRemoveOtherType(flag: bool): int throws OtherFailure {
    try {
        mayFail(flag)
        throw OtherFailure()
    } catch (failure: Failure) {
        0
    }
}

func supertypeCatchBeforeSubtypeIsInvalid(flag: bool): int {
    try {
        mayFail(flag)
    } catch (failure: Exception) {
        0
    } catch (specific: Failure) {
        // Expected: the earlier catch already covers Failure.
        1
    }
}

func tryValueJoinsOnlyNormalResults(flag: bool): int {
    try {
        if flag {
            throw Failure()
        }
        10
    } catch (failure: Failure) {
        20
    } finally {
        record()
    }
}

func allTryPathsThrow(): ! throws Failure {
    try {
        throw Failure()
    } catch (failure: Failure) {
        throw failure
    }
}

func finallyAssignmentPrecedesReturn(out value: int): () {
    try {
        return
    } finally {
        value = 1
    }
}

func finallyCannotChangeReturn(): int {
    try {
        return 1
    } finally {
        // Expected: control transfer from cleanup is rejected.
        return 2
    }
}

func finallyEffectEscapes(): () throws Failure {
    try {
        record()
    } finally {
        fail()
    }
}

func finallyEffectCannotBeDropped(): () {
    try {
        record()
    } finally {
        // Expected: checked cleanup effect contributes to the enclosing budget.
        fail()
    }
}

func fail(): () throws Failure {}
func record(): () {}

func deferLifo(): () {
    defer { recordOrder(1) }
    defer { recordOrder(2) }
    recordOrder(3)
}

func nestedScopeCleanup(flag: bool): () {
    defer { recordOrder(1) }
    for {
        defer { recordOrder(2) }
        if flag {
            break
        }
        continue
    }
}

func recordOrder(value: int): () {}

func conditionalCleanup(flag: bool): () {
    if flag {
        defer { recordOrder(1) }
    }
    recordOrder(2)
}

func cleanupTransferIsInvalid(flag: bool): () {
    for {
        defer {
            if flag {
                // Expected: a cleanup body cannot break out to this loop.
                break
            }
        }
        break
    }
}

// Finally/defer are scope transfer actions, not ordinary one-time source statements.
// They execute on the applicable normal and exceptional exits in deterministic LIFO order.
// A cleanup that throws while another failure is primary records a suppressed failure without
// replacing the primary. Collection iteration cleanup is separate from arbitrary user defer.
