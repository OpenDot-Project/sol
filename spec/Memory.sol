// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: intended memory semantics; Yellow Tea, roots/barriers and lifetime proof are planned.
module source.memory.rules

class Node(val value: int) {
    var next: Node? = null
}

func cycle(): Node {
    val first = Node(1)
    val second = Node(2)
    first.next = second
    second.next = first
    first
}

func counter(): () -> int {
    var value = 0
    { value += 1; value }
}

// Managed references keep reachable objects alive, including cycles. An unreachable
// cycle is collectible. Yellow Tea is precise non-moving concurrent mark/sweep, with
// short phase/root handshakes; full-heap STW is not the intended collection strategy.
// No pause/throughput guarantee exists before implementation and measurement.
// Live roots publish at safepoints; managed writes need appropriate concurrent barriers.
// Ref/out interior locations retain base-owner roots and offsets, not naked addresses.
// A var captured by closures shares one managed cell; separate invocations do not
// create independent copies of that variable. Captured val values are shallow copies.
// Ref structs/borrows cannot escape to heap, closures or another thread without proof.
// Ordinary shared scalar/reference operations have defined relaxed semantics; multi-field
// struct snapshots are not atomic. Mutex/Atomic APIs establish explicit synchronization.
// CLR tool heap, Sol native heap and native foreign ownership are separate lifetimes.
