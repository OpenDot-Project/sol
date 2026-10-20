// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: intended std.reflect API usage; runtime descriptors/invocation are planned.
module source.reflection.rules

import std.reflect

func <T> description(value: T): String {
    val info = type_of<T>()
    info.qualifiedName
}

func readProperty(receiver: Object?, property: SolProperty<String>): String throws Throwable {
    property.get(receiver)
}

func writeProperty(receiver: Object?, property: SolProperty<String>, value: String): () throws Throwable {
    property.set(receiver, value)
}

// type_of<T>() returns immutable native Sol metadata, not CLR reflection. Descriptors
// retain exact nominal/generic/nullability/effect identity and do not create a T value.
// SolProperty<T> is a typed declaration descriptor, not per-owner value storage.
// Runtime metadata includes name/qualifiedName/moduleName/declaringType/valueType,
// visibility, mutability, delegation/local/override flags and accessor/initializer metadata.
// get/set invoke the real accessor/delegate and preserve exceptions and access rules;
// they do not bypass a private setter or permit a post-construction init-only write.
// Typed descriptor casts require exact runtime type checks; ref-like/ref-return properties
// cannot be converted into ordinary heap generic storage by reflection.
// Public metadata snapshots are deterministic shallow arrays; source/local descriptors
// do not expose arbitrary local variable storage. A descriptor must not root an owner.
