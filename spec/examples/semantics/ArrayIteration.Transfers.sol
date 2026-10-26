// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: body transfers own separate continue, break, return, and exceptional edges.
module spec.arrayiteration.transfers

func completeNormally(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

func continueSkipsRemainder(values: []int): int {
    var total = 0
    for val item in values {
        if item < 0 { continue }
        total += item
        recordBodyTail(item)
    }
    total
}

func continueStillAdvancesIndex(values: []int): int {
    var count = 0
    for val item in values {
        if item == 0 { continue }
        count += 1
    }
    count
}

func multipleContinuesHaveOneTarget(values: []int): int {
    var total = 0
    for val item in values {
        if item < 0 { continue }
        if item == 0 { continue }
        total += item
    }
    total
}

func breakSkipsRemainingElements(values: []int, sentinel: int): int {
    var total = 0
    for val item in values {
        if item == sentinel { break }
        total += item
        recordBodyTail(item)
    }
    total
}

func breakDoesNotRunArrayStep(values: []int): int {
    var total = 0
    for val item in values {
        if item < 0 { break }
        total += item
    }
    total
}

func returnLeavesLoopAndFunction(values: []int): int {
    for val item in values {
        if item > 0 { return item }
    }
    0
}

func returnDoesNotReadFollowingItem(values: []int): int {
    for val item in values {
        return item
    }
    -1
}

func throwLeavesThroughExceptionalEdge(values: []int): int throws Failure {
    var total = 0
    for val item in values {
        if item == 0 { throw Failure("zero") }
        total += item
    }
    total
}

func throwDoesNotRunNextElementLoad(values: []int): () throws Failure {
    for val item in values {
        throw Failure("stop")
    }
}

func bottomCallHasNoBackedge(values: []int): () {
    for val item in values {
        terminateProcess()
        recordBodyTail(item)
    }
}

func breakOnlyAffectsNearestLoop(values: [][]int): int {
    var total = 0
    for val row in values {
        for val item in row {
            if item == 0 { break }
            total += item
        }
        total += row.length
    }
    total
}

func continueOnlyAffectsNearestLoop(values: [][]int): int {
    var total = 0
    for val row in values {
        for val item in row {
            if item == 0 { continue }
            total += item
        }
        total += 1
    }
    total
}

func innerBreakDoesNotExitOuter(values: [][]int): int {
    var rows = 0
    for val row in values {
        for val item in row {
            if item < 0 { break }
        }
        rows += 1
    }
    rows
}

func innerContinueDoesNotSkipOuterTail(values: [][]int): int {
    var rows = 0
    for val row in values {
        for val item in row {
            if item < 0 { continue }
            consume(item)
        }
        rows += 1
    }
    rows
}

func innerReturnExitsEveryActiveLoop(values: [][]int): int {
    for val row in values {
        for val item in row {
            if item != 0 { return item }
        }
    }
    0
}

func emptySourceSkipsBreak(values: []int): int {
    var count = 0
    for val item in values { count += 1; break }
    count
}

func emptySourceSkipsReturn(values: []int): int {
    for val item in values { return item }
    7
}

func emptySourceSkipsThrow(values: []int): () throws Failure {
    for val item in values { throw Failure("unreached") }
}

func continueAfterArrayWrite(values: []int): int {
    var index = 0
    for val item in values {
        if item < 0 {
            values[index] = 0
            index += 1
            continue
        }
        index += 1
    }
    index
}

func breakAfterArrayWrite(values: []int): int {
    var index = 0
    for val item in values {
        if item == 0 {
            values[index] = 1
            break
        }
        index += 1
    }
    index
}

func returnAfterArrayWrite(values: []int): int {
    var index = 0
    for val item in values {
        values[index] = item + 1
        return values[index]
    }
    -1
}

func exceptionalBodyHasNoNormalTail(values: []int): int throws Failure {
    var total = 0
    for val item in values {
        if item < 0 { throw Failure("negative") }
        total += item
    }
    total
}

func breakStateJoinsOnlyAtLoopExit(values: []int): int {
    var total = 3
    for val item in values {
        if item == 0 { total = 9; break }
        total += item
    }
    total
}

func nestedBreakThenOuterContinue(values: [][]int): int {
    var total = 0
    for val row in values {
        for val item in row {
            if item == 0 { break }
            if item < 0 { continue }
            total += item
        }
        continue
    }
    total
}

func zeroTripStillReachesAfter(values: []int): int {
    var result = 12
    for val item in values { result = item; return result }
    result
}

class Failure: Exception {}

// Every transfer is attached to its own graph edge. A compiler cannot model continue as a normal
// fallthrough or translate an inner break to an outer-loop exit.
