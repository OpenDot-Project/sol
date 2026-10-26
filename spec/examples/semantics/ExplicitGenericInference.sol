// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; generic inference and diagnostic details remain incomplete.
module examples.semantics.explicit_generic_inference

class Box<T>(val value: T)

func <T> identity(value: T): T = value
func <T> empty(): T? = null
func <T> accept(box: Box<T>, replacement: T): Box<T> = Box(replacement)

func explicitIdentity(): String {
    return identity<String>("explicit")
}

func explicitExpected(): String? {
    return empty<String>()
}

func explicitOwner(): Box<int> {
    val original: Box<int> = Box<int>(1)
    return accept<int>(original, 2)
}

func explicitArityMismatch(): () {
    // Expected diagnostic: explicit generic arity must match the selected declaration.
    val invalid = identity<int, String>(1)
}

func explicitArgumentMismatch(): () {
    // Expected diagnostic: T=int does not make a String argument convertible to int.
    val invalid = identity<int>("text")
}

func explicitAndExpected(): String {
    val value: String = identity<String>("same")
    return value
}

// Explicit generic arguments fix the listed parameter identities and remove them from inference.
// The compiler still validates owner/method constraints, final parameter conversions, nullable
// rules, and exact ref/out locations. An explicit type argument is not an unchecked cast.
