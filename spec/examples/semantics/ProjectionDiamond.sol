// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; constructed path identity and method merging are planned.
module examples.semantics.projection_diamond

interface Root<T> {
    func root(): T
}

interface Left<T>: Root<T> { }
interface Right<T>: Root<T> { }

open class Shared<T>: Left<T>, Right<T> {
    open override func root(): T = default<T>()
}

class Leaf<U>: Shared<U> { }

func <T> rootValue(value: Root<T>): T = value.root()

func uniqueRepeatedPath(): String {
    val value: Leaf<String> = Leaf()
    return rootValue(value)
}

interface NumericRoot: Root<int> { }
interface TextRoot: Root<String> { }

class ConflictingLeaf: NumericRoot, TextRoot { }

func conflictingPath(): () {
    // Expected declaration/member diagnostic: Root<int> and Root<String> are distinct contracts.
    val value = rootValue(ConflictingLeaf())
}

func closedOwner(): []String {
    val value: Leaf<String> = Leaf()
    return value.root().split("")
}

func openOwner<T>(value: Leaf<T>): T {
    return value.root()
}

// Left<T> and Right<T> reach the same Root<T> identity. The lookup must collapse the duplicate
// constructed owner and keep Shared.root as one override. The conflicting path intentionally
// reaches two Root applications and must not be normalized to a single Root by symbol ID.
//
// Generic inference and member resolution use the same edge-by-edge substitution but retain
// separate result records: the first answers which declarations exist; the second solves T for a
// particular call candidate.
