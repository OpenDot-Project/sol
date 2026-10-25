// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; fully specialized generic member bodies are planned.
module examples.semantics.method_type_shadowing

class Cache<T>(val initial: T) {
    func <T> replace(value: T): T = value

    func ownerValue(): T = initial

    func localTypes(): String {
        val owner: T = initial
        val method: String = this.replace("method")
        return method
    }
}

func closedOwner(): int {
    val cache: Cache<int> = Cache(5)
    return cache.ownerValue()
}

func closedMethod(): String {
    val cache: Cache<int> = Cache(5)
    return cache.replace("value")
}

func explicitMethodArgument(): String {
    val cache: Cache<int> = Cache(5)
    return cache.replace<String>("value")
}

// The owner T in Cache<int> is fixed to int. The method-owned T in replace is independently
// inferred as String. `ownerValue` returns int. A substitution map keyed only by the parameter name
// would incorrectly overwrite one identity with the other and is not a valid implementation.
//
// Method type parameter ordinals are compared with alpha-renaming during override matching. The
// source spelling remains available for diagnostics but does not participate in type identity.
