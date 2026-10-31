// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; complete generic constructor inference is pending.
module examples.semantics.generic_constructor_inference

class Bucket<T>(val value: T) {
    func get(): T = value
}

class PairBucket<A, B>(val first: A, val second: B) { }
class EmptyBucket<T> { }

func inferredBucket(): Bucket<String> {
    return Bucket("text")
}

func explicitBucket(): Bucket<int> {
    return Bucket<int>(1)
}

func inferredPair(): PairBucket<String, int> {
    return PairBucket("left", 2)
}

func resultContext(): Bucket<String> {
    val result: Bucket<String> = Bucket("context")
    return result
}

func missingConstructorEvidence(): () {
    // Expected diagnostic: a zero-argument generic type has no argument evidence for T.
    val unresolved = EmptyBucket()
}

func nestedConstructor(): Bucket<Bucket<int>> {
    return Bucket(Bucket(3))
}

// A generic construction selects a type application and then a constructor. Constructor argument
// types may supply owner type evidence; explicit owner arguments fix that evidence first. Nested
// construction forms a finite local inference chain. It does not require project-wide inference.
//
// Constraints on Bucket<T> are checked after owner inference and before its value is published.
// The constructor's parameter/result/effect metadata uses the same constructed T. A failed bound
// rejects that constructor candidate rather than creating a partially typed Bucket.
