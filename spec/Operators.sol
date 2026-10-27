// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/semantic contract. Body analysis retains selected operator candidates, including
// candidate-local generic and expected-result inference. FIR currently admits only direct,
// nongeneric, non-nullable exact-type calls whose operands and result have a verified native layout.
// Nominal value layouts, nullable lifting, conversions, and full user-defined lowering remain planned.
module source.operators.rules

struct Point {
    val x: int
    val y: int
    constructor(x: int, y: int) { this.x = x; this.y = y }

    companion object {
        operator func +(left: Point, right: Point): Point {
            Point(left.x + right.x, left.y + right.y)
        }
        operator func ==(left: Point, right: Point): bool {
            left.x == right.x && left.y == right.y
        }
        operator func !=(left: Point, right: Point): bool {
            !(left == right)
        }
    }
}

// Token and conversion operators live in the owner companion. At least one operand
// belongs to that owner. Arity and result rules are checked; comparison returns bool.
// Required pairs are ==/!=, </>, <=/>= and true/false. ===/!== identity is intrinsic.
// Assignment, call, member selection, casts/tests and null-safety syntax are not overloaded.
// Compound assignment reads, operates and stores with one receiver/index evaluation.
// ++/-- returns the new owner value; postfix yields the previous value before storing.
// checked token variants can be preferred in checked contexts. Ordinary numeric arithmetic
// inside any operator body still follows checked numeric rules.
// Implicit conversions chain at most one user conversion; ref/out never uses them.
// Nullable lifting does not pass null operands into user-defined non-null operator bodies.
//
// Generic operator type arguments use operand evidence and, when the enclosing expression has an
// expected result type, expected-result evidence through the declared return pattern. The result
// context may close a type argument that is absent from operand types, but it does not change the
// operand conversion ranking rule. Constraints are checked after inference. If evidence is missing
// or contradictory, the operator expression remains deferred/invalid rather than guessing a type.
