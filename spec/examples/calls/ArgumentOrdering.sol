// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: contextual lambda typing and exact-ABI noncapturing function values are implemented.
// Native default evaluation and vararg packing still gate the calls shown here.
module demonstrations.calls

func stamp(value: int): int = value
func defaults(a: int = stamp(1), b: int = a, c: int = stamp(3)): int = a + b + c
func consume(head: int = 0, vararg values: int, complete: bool): () {}
func usingBody(head: int = 0, vararg values: int, body: () -> int): int = body()

func demonstrate(items: []int): () {
    val first = defaults(a = stamp(10), stamp(20), c = stamp(30))
    val second = defaults(c = stamp(40))
    consume(1, 2, *items, 3, complete = true)
    consume(values = items, complete = true)
    consume(values = *items, complete = true)
    consume(complete = true)
    val third = usingBody { 42 }
    val fourth = usingBody(head = 1) { 43 }
}

// first evaluates stamp(10), stamp(20), stamp(30) in that source order, with no defaults.
// second evaluates stamp(40), then the omitted a default stamp(1), then b's read of a.
// No default runs at declaration time. Named values is a whole []int, not one int.
// The outside lambda fills body, leaving head defaulted and values empty when omitted.
