// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; complete nullable inference and runtime safe-call lowering are pending.
module examples.semantics.nullable_inference

class User(val name: String)

func <T> identity(value: T): T = value
func <T> lift(value: T): T? = value
func <T> acceptNullable(value: T?): T? = value
func <T> defaultValue(): T? = null

func nonNullArgument(): User {
    val user: User = User("Ada")
    return identity(user)
}

func nullableArgument(): User? {
    val maybe: User? = null
    return identity(maybe)
}

func nullableParameter(): User? {
    val maybe: User? = null
    return acceptNullable(maybe)
}

func expectedEmpty(): User? {
    return defaultValue()
}

func nullOnly(): () {
    // Expected diagnostic: null alone does not identify a non-null T.
    val unresolved = acceptNullable(null)
}

func arraysRemainDistinct(): () {
    val nonNullArray: []String? = ["one", null]
    val nullableArray: ([]String)? = null
    val first: String? = nonNullArray[0]
    val count: int? = nullableArray?.length
}

// A direct T occurrence can infer T = User? from a nullable argument. A T? occurrence structurally
// matches its nullable payload and normalizes one nullable wrapper. Expected result evidence can
// determine T when the generic parameter otherwise has no input. A null bottom value by itself is
// not a fallback to Object.
//
// Array nullability is part of the source type identity. Nullable elements in a non-null array and
// a nullable array container must not collapse to the same generic argument.
