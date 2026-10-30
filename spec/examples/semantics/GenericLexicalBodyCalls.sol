// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: bounded source body proofs; generic owners/local closures have no native admission.
module source.spec.examples.semantics.generic_lexical_body_calls

// SourceBound below means a closed source proof, not AOT/ORC permission.
class Box<T> {
    func run(value: T): T {
        val captured = value
        func <U> local(item: U): T = captured
        local<int>(1)
    }
}

func integerBox(owner: Box<int>, value: int): int = owner.run(value)
func stringBox(owner: Box<String>, value: String): String = owner.run(value)

// expect: separate-owners | SourceBound; captured storage/result is int or String in distinct body instances.

class Nested<T> {
    func <V> run(value: T, other: V): T {
        func <U> middle(item: U): T {
            func <W> local(ignored: W): T = value
            local<U>(item)
        }
        middle<V>(other)
    }
}

func nested(owner: Nested<int>, value: int, other: String): int = owner.run<String>(value, other)

// expect: nested-vectors | SourceBound; owner T=int, enclosing V=String, middle U=String, local W=String remain distinct.

class Assignment<T> {
    func valid(value: T): T {
        var captured: T
        func <U> local(item: U): T = captured
        captured = value
        local<int>(1)
    }

    func invalid(value: T): T {
        var captured: T
        func <U> local(item: U): T = captured
        local<int>(1)
    }
}

func assigned(owner: Assignment<int>, value: int): int = owner.valid(value)
func unassigned(owner: Assignment<int>, value: int): int = owner.invalid(value)

// expect: captured-assignment | valid is SourceBound; invalid reports SOL2240 at the original captured read.

class Recursive<T> {
    func run(value: T): T {
        func <U> local(item: U): T = local<U>(item)
        local<int>(1)
    }
}

func recursive(owner: Recursive<int>, value: int): int = owner.run(value)

// expect: finite-recursion | SourceBound dependency group with a recursive instance reference; no termination claim.
// All examples keep SOL2400 as the independent generic owner/member and local generic FIR boundary.
