// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/lexical capture candidates; expected typing and escape checking remain planned.
module source.lambdas.rules

val named: (int, int) -> int = { x, y -> x + y }
val positional: (int, int) -> int = { $0 + $1 }
val typed = { x: int, y: int -> x + y }
val constant = { 42 }
val explicitEmpty: () -> int = { -> 42 }
val ignoreFirst: (int, int) -> int = { _, second -> second }

func apply(value: int, operation: (int) -> int): int = operation(value)

func nestedAndLabelled(): () {
    val result = apply(21) { $0 * 2 }
    val callback: (int) -> int = done@ { value ->
        if value < 0 { return@done 0 }
        value
    }
    val outer: (int) -> () -> int = { saved -> { saved } }
    val anonymous = func(value: int): int { return value + 1 }
    val byReference: (ref int, out int) -> () = { ref input: int, out output: int ->
        output = input
    }
}

// There is no implicit it. Named headers and $n cannot mix in the same lambda.
// Positional references require an expected function type; the highest index must
// fit its arity. $00 is not canonical. Nested lambdas have independent parameter scopes.
// A headerless lambda without $n is parameterless, even when it ignores all arguments.
// Bare return cannot jump out of a lambda. return@label is local to its resolved target.
// The last expression supplies the result; assignment/declaration/loop has no value.
// Captured val copies its value; captured mutable var uses a shared cell. Capturing
// ref/out/ref-like/borrowed storage requires a proven non-escaping use, not a guess.
