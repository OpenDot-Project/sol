// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: catches and finally clauses receive the exceptional state at each iteration edge.
module spec.arrayiteration.trycatchedges

class SourceFailure: Exception {}
class BodyFailure: Exception {}
class CleanupFailure: Exception {}

func sourceFailureCaughtOutside(values: []int): int {
    try {
        for val item in acquireOrThrow(values) { consume(item) }
    } catch (error: SourceFailure) {
        return -1
    }
    0
}

func bodyFailureCaughtInside(values: []int): int {
    var total = 0
    for val item in values {
        try { total += bodyMayThrow(item) }
        catch (error: BodyFailure) { total -= 1 }
    }
    total
}

func bodyFailureCaughtOutside(values: []int): int {
    try {
        for val item in values { bodyMayThrow(item) }
    } catch (error: BodyFailure) {
        return -1
    }
    0
}

func catchStateIncludesEarlierIterationWrites(values: []int): int {
    var total = 0
    try {
        for val item in values {
            total += item
            bodyMayThrow(item)
        }
    } catch (error: BodyFailure) {
        return total
    }
    total
}

func catchStateDoesNotIncludeLaterIterationWrites(values: []int): int {
    var total = 0
    try {
        for val item in values {
            if item < 0 { bodyMayThrow(item) }
            total += item
        }
    } catch (error: BodyFailure) { return total }
    total
}

func catchTypeMustCoverSourceFailure(values: []int): () throws SourceFailure {
    try {
        for val item in acquireOrThrow(values) { consume(item) }
    } catch (error: BodyFailure) { consume(error) }
}

func catchesAreOrderedForSourceAndBody(values: []int): int {
    try {
        for val item in acquireAndProcess(values) { bodyMayThrow(item) }
    } catch (error: SourceFailure) { return -1 }
      catch (error: BodyFailure) { return -2 }
    0
}

func finallyRunsAfterNormalExhaustion(values: []int): int {
    var total = 0
    try { for val item in values { total += item } }
    finally { recordFinally() }
    total
}

func finallyRunsAfterBreak(values: []int): () {
    try { for val item in values { break } }
    finally { recordFinally() }
}

func finallyRunsAfterReturn(values: []int): int {
    try { for val item in values { return item } }
    finally { recordFinally() }
    0
}

func finallyRunsAfterBodyThrow(values: []int): () throws BodyFailure {
    try { for val item in values { throw BodyFailure("body") } }
    finally { recordFinally() }
}

func finallyRunsAfterSourceThrow(values: []int): () throws SourceFailure {
    try { for val item in acquireOrThrow(values) { consume(item) } }
    finally { recordFinally() }
}

func finallyFailureOnNormalExitEscapes(values: []int): () throws CleanupFailure {
    try { for val item in values { consume(item) } }
    finally { cleanupMayThrow() }
}

func finallyFailureOnExistingExceptionIsSuppressed(values: []int): ()
    throws BodyFailure, CleanupFailure {
    try { for val item in values { throw BodyFailure("primary") } }
    finally { cleanupMayThrow() }
}

func innerCatchDoesNotCatchOuterCursorClose(source: CursorSource<int>): ()
    throws CloseFailure {
    for val item in source {
        try { consume(item) }
        catch (error: BodyFailure) { consume(error) }
    }
}

func outerCatchCanCatchCursorClose(source: CursorSource<int>): () {
    try { for val item in source { consume(item) } }
    catch (error: CloseFailure) { handle(error) }
}

func finallyRunsAfterCursorClose(source: ResourceCursorSource<int>): ()
    throws CloseFailure, CleanupFailure {
    try { for val item in source { consume(item) } }
    finally { cleanupMayThrow() }
}

func finallyStateUsesActualExitPath(values: []int): int {
    var state = 0
    try {
        for val item in values { state = item; if item > 0 { break } }
    } finally { observe(state) }
    state
}

func bodyThrowStateIsNotNormalExitState(values: []int): () throws BodyFailure {
    var state = 0
    try {
        for val item in values { state = item; throw BodyFailure("stop") }
    } finally { observe(state) }
}

func catchFinallyCompositionPreservesSourceEdges(values: []int): int {
    var state = 0
    try {
        for val item in acquireAndProcess(values) { state = item; bodyMayThrow(item) }
    } catch (error: BodyFailure) {
        state = -1
    } finally {
        observe(state)
    }
    state
}

func zeroTripStillRunsEnclosingFinally(values: []int): () {
    try { for val item in values { consume(item) } }
    finally { recordFinally() }
}

func zeroTripDoesNotRunIterationDefer(): () {
    val empty: []int = []
    try {
        for val item in empty { defer { cleanupMayThrow() } }
    } finally { recordFinally() }
}

// Source failure occurs before any item slot is written. Body failure occurs after the current
// item is assigned. Catch/finally analysis preserves those snapshots and does not merge them into
// a state that appears to have completed all remaining iterations.
