// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative loop fixed-point fixture. The body is not assumed to execute exactly once.
module spec.flow.loopfixedpoint

class Item {
    val value: int = 1
}

func conditionMayBeFalseInitially(flag: bool): int {
    var item: Item?
    for flag {
        item = Item()
        break
    }
    // Expected: unassigned because item has no initializer before the zero-iteration edge.
    item.value
}

func assignmentBeforeLoopIsStable(flag: bool): int {
    var item: Item?
    item = Item()
    for flag {
        item = Item()
    }
    // Expected: definitely assigned, but the declared type remains nullable.
    item.value
}

func conditionProofAppliesOnlyToBody(item: Item?): int {
    for item != null {
        return item.value
    }
    0
}

func conditionProofDoesNotSurviveWrite(item: Item?): int {
    var current = item
    for current != null {
        current = null
        break
    }
    // Expected: the loop can mutate current; do not reuse the header condition's refinement.
    current.value
}

func updateStateFeedsNextCondition(limit: int): int {
    for var index: int = 0; index < limit; index += 1 {
        if index == 2 {
            continue
        }
    }
    // The update runs after continue and before the next condition.
    index
}

func writeOnBackedgeNotDefinite(limit: int): int {
    var item: Item?
    for var index: int = 0; index < limit; index += 1 {
        item = Item()
    }
    // Expected: body may execute zero times.
    item.value
}

func breakStatesMustJoin(flag: bool): int {
    var result: int
    for {
        if flag {
            result = 1
            break
        }
        result = 2
        break
    }
    // Both break edges assign result.
    result
}

func oneBreakStateMissesAssignment(flag: bool): int {
    var result: int
    for {
        if flag {
            result = 1
            break
        }
        break
    }
    // Expected: the second break edge leaves result unassigned.
    result
}

func nestedHeaderStateIsIndependent(flag: bool): int {
    var value = 0
    for {
        for flag {
            value = 1
            break
        }
        if flag {
            break
        }
        break
    }
    value
}

func continueRunsRegisteredCleanup(limit: int): int {
    var total = 0
    for var index: int = 0; index < limit; index += 1 {
        defer { record(index) }
        if index < 2 {
            continue
        }
        total += index
    }
    total
}

func record(value: int): () {}

// A loop fixed point joins the incoming header with every reachable backedge after the update.
// Break edges are collected for the current loop, while return/throw edges leave the enclosing
// body. Implementations with a finite convergence budget expose a resource/deferred outcome.
