// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative runtime matrix: polling occurs on normal and continue backedges, with bounded caveats.
module spec.arrayiteration.gcprogress

func emptyLoopHasNoBackedgePoll(values: []int): () {
    for val item in values { consume(item) }
}

func singleItemLoopPollsAfterBody(values: []int): () {
    for val item in values { consume(item) }
}

func multipleItemLoopPollsBetweenBodies(values: []int): () {
    for val item in values { consume(item) }
}

func continuePollsBeforeNextTest(values: []int): () {
    for val item in values { continue }
}

func continueAfterWorkPollsAfterCleanup(values: []int): () {
    for val item in values {
        defer { releaseIteration(item) }
        doWork(item)
        continue
    }
}

func normalCompletionPollsAfterCleanup(values: []int): () {
    for val item in values {
        defer { releaseIteration(item) }
        doWork(item)
    }
}

func breakDoesNotPollBeforeExit(values: []int): () {
    for val item in values { break }
}

func returnDoesNotPollAfterReturn(values: []int): int {
    for val item in values { return item }
    0
}

func throwDoesNotPollAfterThrow(values: []int): () throws Failure {
    for val item in values { throw Failure("stop") }
}

func longBodyCanExceedPollInterval(values: []int): () {
    for val item in values {
        oneLongUninterruptibleOperation(item)
    }
}

func nestedLoopsHaveIndependentBackedges(rows: [][]int): () {
    for val row in rows {
        for val item in row { consume(item) }
    }
}

func innerContinuePollsInnerLoop(rows: [][]int): () {
    for val row in rows {
        for val item in row { continue }
    }
}

func innerBreakDoesNotPollOuterBackedge(rows: [][]int): () {
    for val row in rows {
        for val item in row { break }
    }
}

func outerContinueSkipsInnerPoll(rows: [][]int): () {
    for val row in rows {
        if row.length == 0 { continue }
        for val item in row { consume(item) }
    }
}

func guestPollIsNotHostCollection(values: []String): () {
    for val item in values {
        solGcPoll()
        consume(item)
    }
}

func hostRuntimeAndGuestRuntimeAreSeparated(values: []String): () {
    for val item in values { consume(item) }
}

func pollBoundDependsOnBodyWork(values: []int): () {
    for val item in values { boundedBody(item) }
}

func pollDoesNotPromiseRealTimeDeadline(values: []int): () {
    for val item in values { schedulerDependentWork(item) }
}

func pollAtEveryBackedgeDoesNotMeanStopTheWorld(values: []int): () {
    for val item in values { allocateGuestObjects(item) }
}

func collectRequestInsideBodyWaitsForConcurrentCycle(values: []int): () {
    for val item in values {
        collectGuestHeap()
        consume(item)
    }
}

func loopRootAndPollAreSeparateObligations(values: []Cell): () {
    for val item in values {
        collectGuestHeap()
        consume(item.number)
    }
}

func nestedLoopPollDoesNotForgetOuterRoot(rows: [][]Cell): () {
    for val row in rows {
        for val item in row {
            collectGuestHeap()
            consume(item.number)
        }
    }
}

func noPollOnPathThatCannotReachBackedge(values: []int): () {
    for val item in values { return }
}

class Failure: Exception {}
class Cell { val number: int }

// Polling is a cooperation point after a completed iteration. It bounds inter-iteration work only;
// the runtime's scheduler and an individual body operation prevent a hard real-time guarantee.
