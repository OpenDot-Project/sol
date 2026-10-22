// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax and lexical scopes; inference, lazy initialization and flow are partial/planned.
module source.bindings.rules

const val MAX_ATTEMPTS: int = 3
val defaultName: String = "Sol"
var startedCount: int = 0

func localBindings(): int {
    val initial = 40
    var answer: int = initial
    answer += 2
    val pair = (answer, "ready")
    val (value, _) = pair
    value
}

func initialization(flag: bool): int {
    var value: int
    if flag {
        value = 10
    } else {
        value = 20
    }
    value
}

// val prevents rebinding, not mutation through an object reference.
// var supports reassignment. Assignment is a statement, never an expression value.
// A local initializer sees outer bindings; its own declaration becomes visible after
// initialization. Reading unassigned locals requires definite-assignment rejection.
// const val requires bounded compile-time evaluation. Module/object/companion values
// initialize lazily, once, with cycle/failure handling; source path order has no meaning.
// Compile-time evaluation limits are 100000 steps, nesting 128 and 1 MiB literal data.
