// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; nested nullable generic inference remains under development.
module examples.semantics.nested_nullable_generics

class Box<T>(val value: T)

func <T> wrap(value: T): Box<T> = Box(value)
func <T> takeNullable(value: Box<T?>): T? = value.value
func <T> wrapNullable(value: T?): Box<T?> = Box(value)

func wrappedText(): Box<String> {
    return wrap("text")
}

func nullablePayload(): String? {
    val value: Box<String?> = Box(null)
    return takeNullable(value)
}

func nestedNullable(): Box<String?> {
    val source: String? = null
    return wrapNullable(source)
}

func nonNullPayload(): String? {
    val value: Box<String?> = Box("text")
    return takeNullable(value)
}

func invariantNullableConflict(): () {
    val nonNull: Box<String> = Box("text")
    val nullable: Box<String?> = Box(null)
    // Expected diagnostic: Box<T> is invariant and T!=T? identities conflict.
    compare(nonNull, nullable)
}

func <T> compare(left: Box<T>, right: Box<T>): T = left.value

// Nullable normalization applies inside the constructed Box but does not make Box covariant.
// `T?` can bind from a nullable payload after matching the wrapper; direct T retains a nullable
// argument as T. A call cannot infer a common Box type by lifting the inner String alone.
