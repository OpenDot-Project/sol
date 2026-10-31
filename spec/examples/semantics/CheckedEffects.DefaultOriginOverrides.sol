// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative override-default fixture. Static declaration origins control omitted arguments.
module spec.effects.defaultoriginoverrides

class DefaultFailure: Exception {}
class BodyFailure: Exception {}

func readDefault(): int throws DefaultFailure { 1 }
func readBody(): int throws BodyFailure { 2 }

open class BaseReader {
    open func read(value: int = readDefault()): int throws BodyFailure {
        readBody()
    }
}

class CachedReader: BaseReader {
    override func read(value: int): int {
        value
    }
}

class RemoteReader: BaseReader {
    override func read(value: int): int throws BodyFailure {
        readBody() + value
    }
}

func callThroughBase(reader: BaseReader): int throws DefaultFailure, BodyFailure {
    // The static BaseReader declaration supplies the omitted argument and its effect.
    reader.read()
}

func callOverrideWithExplicitValue(reader: BaseReader): int throws BodyFailure {
    // Supplying the argument suppresses only the inherited default expression.
    reader.read(value = 4)
}

func inheritedDefaultEffectCannotBeDropped(reader: BaseReader): int throws BodyFailure {
    // Expected: omitted value still evaluates readDefault through the selected base contract.
    reader.read()
}

func overrideCannotWriteNewDefault(reader: CachedReader): int {
    // Parameter names are part of the public named-call contract; the override inherits the
    // original default origin and cannot declare a replacement expression.
    reader.read(value = 5)
}

open class LeftDefaults {
    open func load(value: int = readDefault()): int throws BodyFailure { readBody() }
}

open class RightDefaults {
    open func load(value: int = readBody()): int throws BodyFailure { readBody() }
}

class AmbiguousDefaultJoin: LeftDefaults, RightDefaults {
    override func load(value: int): int throws BodyFailure { readBody() }
}

func inheritedDefaultOriginsConflict(value: AmbiguousDefaultJoin): int throws DefaultFailure, BodyFailure {
    // Expected: conflicting inherited default origins require explicit resolution. Source order
    // must not decide which default effect the call receives.
    value.load()
}

func explicitArgumentAvoidsDefaultConflict(value: AmbiguousDefaultJoin): int throws BodyFailure {
    // The argument is explicit, so no inherited default expression is evaluated.
    value.load(value = 1)
}

// The statically selected callable contract supplies a default; virtual dispatch chooses the
// implementation body. Default expression effects retain the declaration origin and are
// evaluated per call, only when that formal is omitted.
