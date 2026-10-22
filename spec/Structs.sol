// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/type identities; layout/defaultability/escape verification remain planned.
module source.values.rules

struct Point {
    var x: int
    var y: int
    constructor(x: int, y: int) { this.x = x; this.y = y }
}

func copying(): int {
    val original = Point(1, 2)
    var copy = original
    copy.x = 9
    original.x
}

struct Labelled {
    val text: String
    constructor(text: String) { this.text = text }
}

ref struct Slice<T> {
    private val owner: []T
    private val offset: int
    val length: int
    constructor(owner: []T, offset: int, length: int) {
        this.owner = owner
        this.offset = offset
        this.length = length
    }
}

func borrow(ref value: int): ref int { return ref value }

// Structs copy by value; reference fields are copied shallowly. There is no struct
// inheritance. Interfaces may be implemented; ordinary interface storage boxes values.
// default<Point>() is legal only after zero-valid layout has been proven. A non-null
// String field makes Labelled nonzero-defaultable; default<Labelled>() must be rejected.
// Calling Point(...) uses its constructor; default<T>() does not call constructors.
// Ref structs cannot be boxed, placed in managed arrays/ordinary heap fields, or captured
// by escaping callbacks. Generic acceptance requires allows ref struct and body safety.
// Returning a ref preserves caller/heap owner lifetime; returning a ref to a local is illegal.
// Readonly receiver defensive copying and ref readonly locations need later flow/lifetime work.
