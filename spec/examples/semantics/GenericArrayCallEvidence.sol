// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/semantic contract fixture; complete array common-type inference is pending.
module examples.semantics.generic_array_call_evidence

open class Animal { }
class Cat: Animal { }
class Dog: Animal { }

class Box<T>(val value: T)

func <T> first(values: []T): T = values[0]
func <T> preserve(value: T): T = value
func <T> fixed(values: [2]T): T = values[0]
func <T> spread(vararg values: T): []T = values
func <T> namedSpread(vararg values: T): []T = values

func directArrayEvidence(): int {
    return first([1, 2, 3])
}

func wholeArrayEvidence(): []String {
    return preserve(["north", "south"])
}

func nestedArrayEvidence(): [][]int {
    return preserve([[1, 2], [3, 4]])
}

func fixedArrayEvidence(): int {
    return fixed([7, 8])
}

func wrongFixedLength(): () {
    // Expected diagnostic: [3]int cannot satisfy [2]T.
    val invalid = fixed([1, 2, 3])
}

func contextualEmptyArray(): []String {
    val value: []String = first([])
    return value
}

func emptyArrayWithoutEvidence(): () {
    // Expected diagnostic: the empty literal supplies no element type for T.
    val invalid = first([])
}

func nullElementContext(): []String? {
    val values: []String? = first(["ready", null])
    return values
}

func nullOnlyElement(): () {
    // Expected diagnostic: null does not select a non-null generic element type.
    val invalid = first([null])
}

func differentBaseElements(): []Animal {
    return preserve([Cat(), Dog()])
}

func typedNumericVariablesDoNotWiden(): () {
    val small: byte = 1 as byte
    val wide: long = 2
    // Expected inference result: no numeric widening is invented for the typed storage values.
    val invalid = preserve([small, wide])
}

func invariantNestedApplications(): () {
    val values: []Box<Cat> = [Box(Cat())]
    // Expected diagnostic: Box<Cat> does not become Box<Animal> by changing its inner argument.
    val invalid: []Box<Animal> = values
}

func arrayAsOneVarargElement(): [][]int {
    return spread([1, 2])
}

func arraySpread(): []String {
    val source: []String = ["a", "b"]
    return spread(*source)
}

func namedArrayArgument(): []String {
    val source: []String = ["a", "b"]
    return namedSpread(values = source)
}

func namedLiteralArrayArgument(): []String {
    return namedSpread(values = ["a", "b"])
}

func emptyVarargIsNotEvidence(): () {
    // Expected diagnostic: an omitted vararg cannot choose T.
    val invalid = spread()
}

func genericCallInsideArray(): []String {
    return preserve([first(["a"]), first(["b"])])
}

func nullableArrayContainer(): ([]String)? = null

func nullableElementsRemainSeparate(): []String? {
    return ["a", null]
}

// The fixture distinguishes a direct `[]T` formal, an unconstrained T receiving a whole array,
// fixed-array length, ordinary vararg elements, spread arrays, and named vararg arrays. Each
// candidate sees element facts without executing them. The selected call constructs/evaluates the
// array once in source order.
//
// A non-empty literal used as a named vararg array needs one unique standalone element common type,
// then an exact array-element match. Ordinary vararg elements instead contribute individual input
// bounds. Empty literals and omitted varargs contribute no T evidence. A typed array source is not
// inspected at runtime to infer a narrower element type.
//
// These examples are parser/source contracts. They do not claim native array allocation, Yellow
// Tea descriptors, write barriers, fixed-array ABI layout, or span conversion is implemented.
