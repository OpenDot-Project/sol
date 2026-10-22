// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax only; std.collections implementation and span escape checking are planned.
module demonstrations.collections

import std.collections

func build(): []int {
    val values = Vector<int>(capacity = 4)
    values.add(10)
    values.add(20)
    values.insert(index = 1, value = 15)
    values.removeAt(0)
    values.toArray()
}

func borrow(values: Vector<int>): int {
    val view: ReadOnlySpan<int> = values.asReadOnlySpan()
    view.length
}

// build's intended result is a separate array [15,20]. Reference elements would
// remain shallow copies. A span cannot outlive or survive invalidation of its owner
// storage; the compiler/runtime borrow contract must be implemented before execution.
