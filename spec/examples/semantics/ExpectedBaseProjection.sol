// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; expected-output projection needs complete semantic verification.
module examples.semantics.expected_base_projection

interface Result<T> { func result(): T }

open class BaseResult<T>(val payload: T): Result<T> {
    override func result(): T = payload
}

class DerivedResult<T>(payload: T): BaseResult<T>(payload) { }

func <T> create(value: T): DerivedResult<T> = DerivedResult(value)

func <T> createFrom(value: T): BaseResult<T> {
    return create(value)
}

func expectedInterface(): Result<String> {
    val result: Result<String> = create("body")
    return result
}

func expectedBase(): BaseResult<int> {
    return create(3)
}

func expectedWrongType(): () {
    // Expected diagnostic: DerivedResult<String> cannot satisfy Result<int>.
    val invalid: Result<int> = create("text")
}

func nestedExpected(): Result<String> {
    return createFrom("nested")
}

// For a generic function returning DerivedResult<T>, an expected Result<String> may be projected
// backward through DerivedResult<T> -> BaseResult<T> -> Result<T>. This supplies evidence T =
// String, but the compiler still verifies the specialized concrete return type converts to the
// expected Result<String>. It must not set T to the expected interface itself.
//
// The same output inference can flow through an immediately selected outer call. It cannot flow
// from unrelated later statements or from an arbitrary source-wide use of the declaration.
