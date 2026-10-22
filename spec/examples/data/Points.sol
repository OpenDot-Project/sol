// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: value declaration syntax; layout/copy/arithmetic execution are planned.
module demonstrations.points

struct Point {
    var x: int
    var y: int
    constructor(x: int, y: int) { this.x = x; this.y = y }
}

func translated(original: Point, dx: int, dy: int): Point {
    var result = original
    result.x += dx
    result.y += dy
    result
}

func example(): int {
    val original = Point(10, 20)
    val result = translated(original, 1, 2)
    original.x
}

// Intended: result is (11,22), original remains (10,20), example returns 10.
// If a struct contains object references, copying those fields remains shallow.
