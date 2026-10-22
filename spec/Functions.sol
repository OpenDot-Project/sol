// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: argument mapping/default metadata implemented; full typing/evaluation remain incomplete.
module source.functions.rules

func add(a, b: int) = a + b
func addExplicit(a, b: int): int { a + b }
func record(a: int, b: int = a, c: int = 3): () {}
func gather(head: int = 0, vararg values: int, finished: bool): () {}
func arrayDefault(): []int = [1, 2, 3]
func withVarargDefault(vararg values: int = arrayDefault()): () {}
func withBody(level: int = 1, vararg values: int, body: () -> int): () {}

func argumentForms(items: []int): () {
    record(a = 1, 2, c = 3)
    record(1, b = 2, 3)
    record(c = 3, a = 1)
    gather(10, 20, *items, 30, finished = true)
    gather(values = items, finished = true)
    gather(values = *items, finished = true)
    gather(finished = true)
    withVarargDefault()
    withVarargDefault(values = items)
    withBody { 42 }
    withBody(level = 2) { 42 }
}

// Grouped a,b:int creates two independent parameters. Defaults and varargs require
// single-name groups. A callable has at most one vararg, at any parameter position.
// Parameters following vararg are named; an outside lambda may fill the final
// function parameter. A parenthesized lambda is not automatically moved past defaults.
// Ordered named/positional mixing is legal. After a named argument skips or reorders
// parameters, later ordinary arguments must be named. Each parameter is filled once.
// Rejected: record(c = 3, 1). Rejected: gather(values = 1, finished = true).
// Named vararg takes []T, never a scalar T. Expanded elements and *array may mix.
// An explicit empty array suppresses the default just like any other explicit value.
// An omitted vararg uses its []T default, or an empty []T if no default exists.

// Explicit arguments evaluate once, in source order. Missing defaults then evaluate
// once in declaration order, in the declaring contract's parameter scope. Defaults
// can read earlier parameters, but not their own or later parameter values.
// Defaults are evaluated per call, never eagerly shared at function declaration time.
// Override declarations omit defaults and inherit their original declaration origins.
// The statically selected signature supplies defaults; dispatch chooses the body.

func swap(ref left: int, ref right: int): () {
    val previous = left
    left = right
    right = previous
}

func readNumber(out value: int): bool {
    value = 42
    true
}

func locations(): () {
    var left = 1
    var right = 2
    swap(left = ref left, right = ref right)
    readNumber(out var result)
}

// Ref/out require explicit markers and exact writable storage, not a temporary
// conversion, boxing, ordinary getter/setter property or captured escaping location.
// Out must be assigned on all normal callee returns. Ref must already be assigned.
// No default is permitted for ref/out. Checked effects and escape are separate checks.
