// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: written identity/substitution/nominal constraints; body specialization remains planned.
module source.generics.rules

interface Named { func name(): String }
interface Ordered<T> { func compare(other: T): int }
class Box<T>(val value: T)

func <T> identity(value: T): T = value
func <T: Named> nameOf(value: T): String = value.name()
func <T> constrained(value: T): T where T: Named, T: Ordered<T> = value
func <T> scopedIdentity(value: T): T where T: allows ref struct = value

func usages(box: Box<int>): () {
    val value = identity(42)
    val explicitValue = identity<long>(42)
    val nested: Box<Box<int>> = Box(box)
}

// Function parameters follow func; nominal parameters follow the type name.
// Generic applications are invariant. Owner+ordinal identifies a parameter; unrelated
// declarations' T names do not make them the same type. Compatible partial parts share IDs.
// Constraints are nominal interface/base requirements. Multiple where bounds all apply.
// T:Object requires a reference type; boxing a primitive/struct cannot satisfy that bound.
// allows ref struct permits ref-like arguments, not heap storage/capture/boxing in the body.
// Inference uses arguments and expected types; unsupported inference must remain explicit.
// Simultaneous replacement T->U,U->int leaves the replacement for an original T as U.
// Substitution preserves nullable normalization, function passing modes and declared effects.
// Specialization depth is bounded at 128 and compiled instances at 100000 per compilation.
// The current SDK's interner budget is not evidence that machine-code specialization exists.
