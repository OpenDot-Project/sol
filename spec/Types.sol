// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax and written type identities; numeric execution/layout remain planned.
module source.types.rules

func primitiveDomains(
    signed8: sbyte, signed16: short, signed32: int, signed64: long,
    unsigned8: byte, unsigned32: uint, unsigned64: ulong,
    binary16: half, binary32: single, binary64: double, binary128: quad,
    condition: bool,
): () {}

// sbyte is signed and byte is unsigned. There is no additional unsigned-byte name.
// Numeric widths do not depend on pointer size. Numeric variables never implicitly
// widen; literals can be range-checked against an expected numeric type.
// Integer arithmetic is checked; IEEE floating behavior is preserved.
// half rounding and quad software lowering require implementation/qualification.

func containers(
    managed: []int,
    inline: [4]int,
    nested: [][]int,
    pair: (int, String),
    singleton: (int,),
    empty: (),
    callback: (ref int, out int) -> bool,
): () {}

func values(): () {
    val managed = [1, 2, 3]
    val inline: [3]int = [1, 2, 3]
    val empty: []String = []
    val pair = (1, "one")
    val singleton = (1,)
    val unitValue = ()
    val (number, name) = pair
}

// () is the empty tuple type and value; it is not a named special return type.
// []T is a fixed-length managed array reference; [N]T is inline value storage.
// Empty [] needs an expected element type. Destructuring requires exact arity.
// Tuple item_1/item_2/... fields are readonly compiler-provided names.

func fail(): ! {
    throw RuntimeException("No normal completion")
}

func nullValue(): !? = null

// ! has no values and no normal completion. It is a subtype of every value type.
// !? has exactly null. Bottom does not remove checked exception obligations.
// []! may be empty; there is no value that can be inserted as its element.
