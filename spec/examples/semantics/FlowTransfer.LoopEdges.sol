// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative loop fixture. Syntax parsing does not run the loops or prove their transfer facts.
module spec.flow.loopedges

func zeroIteration(flag: bool): int {
    var result: int
    for flag {
        result = 1
    }
    // Expected: unassigned-local diagnostic because the condition can be false initially.
    result
}

func conditionLoopMaySkip(flag: bool): int {
    var count = 0
    for flag {
        count += 1
    }
    // Expected: count remains assigned; its value may be unchanged.
    count
}

func initializedBeforeCondition(limit: int): int {
    for var index: int = 0; index < limit; index += 1 {
        if index == 10 {
            break
        }
    }
    // Expected: valid if loop-declaration scope includes the loop's continuation.
    index
}

func updateRunsAfterContinue(limit: int): int {
    var total = 0
    for var index: int = 0; index < limit; index += 1 {
        if index == 2 {
            continue
        }
        total += index
    }
    // Expected: continue reaches the update expression before the next condition.
    total
}

func updateDoesNotRunAfterBreak(limit: int): int {
    var result = 0
    for var index: int = 0; index < limit; index += 1 {
        if index == 1 {
            break
        }
        result = index
    }
    // Expected: the break edge leaves without executing the update expression.
    result
}

func bodyAssignmentIsNotDefinite(flag: bool): int {
    var result: int
    for var index: int = 0; index < 2; index += 1 {
        result = index
        if flag {
            break
        }
    }
    // Expected: first condition may be false; result is not definitely assigned.
    result
}

func breakMakesInfiniteLoopComplete(flag: bool): int {
    for {
        if flag {
            break
        }
        continue
    }
    // Expected: normal continuation exists through the break edge.
    1
}

func noBreakHasNoNormalSuccessor(): ! {
    for {
        continue
    }
}

func breakOnOneArmAndReturnOnTheOther(flag: bool): int {
    for {
        if flag {
            break
        }
        return 5
    }
    // Expected: the only continuing path is the break edge.
    6
}

func nestedLoopBreakIsInner(flag: bool): int {
    var outerCount = 0
    for {
        for {
            if flag {
                break
            }
            break
        }
        outerCount += 1
        if flag {
            break
        }
    }
    outerCount
}

func collectionLoopZeroPath(items: []int): int {
    var last = 0
    for val item in items {
        last = item
    }
    // Expected: last is assigned before the loop and remains valid for empty input.
    last
}

func collectionLoopDoesNotAssign(items: []int): int {
    var last: int
    for val item in items {
        last = item
    }
    // Expected: unassigned-local diagnostic when items is empty.
    last
}

func nullableLoopFactIsNotInvariant(flag: bool, vehicle: Vehicle?): int {
    var current = vehicle
    if current == null {
        return 0
    }
    for flag {
        current = null
        break
    }
    // Expected: a mutable refinement cannot survive a backedge or a break that writes null.
    current.wheels
}

class Vehicle {
    val wheels: int = 4
}

// Transfer sets distinguish normal fallthrough, backedge, continue, break, return, and throw.
// A loop analyzer must not merge an inner break with the outer loop's break list.
// Fixed-point iteration is bounded and cancellation-aware; a bound hit is Deferred or a
// resource diagnostic, never an optimistic proof based on the last visited body.
