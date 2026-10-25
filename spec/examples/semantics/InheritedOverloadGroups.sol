// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; complete inherited overload and default dispatch are planned.
module examples.semantics.inherited_overloads

open class Reader {
    open func read(index: int): String = "index"
    open func read(name: String): String = name
    open func read(enabled: bool, fallback: String = "off"): String = fallback
}

class CachedReader: Reader {
    func read(key: byte): String = "key"

    override func read(name: String): String = "cached:" + name

    override func read(enabled: bool, fallback: String): String =
        if enabled { "on" } else { fallback }
}

func readAll(reader: CachedReader): String {
    val byIndex = reader.read(1)
    val byName = reader.read(name = "home")
    val byKey = reader.read(key = 2 as byte)
    val defaulted = reader.read(enabled = false)
    return byIndex + byName + byKey + defaulted
}

interface Indexed {
    func get(index: int): String
}

interface Named {
    func get(name: String): String
}

class BothReader: Indexed, Named {
    override func get(index: int): String = index.toString()
    override func get(name: String): String = name
}

func useBoth(reader: BothReader): String {
    val byNumber = reader.get(4)
    val byLabel = reader.get("four")
    return byNumber + byLabel
}

// The base `read(index: int)` remains available beside the derived `read(key: byte)` because
// their signatures differ. The inherited `read(name: String)` candidate is replaced by its valid
// override. The base default expression remains the call-site contract for enabled-only use.
//
// `BothReader` has two distinct inherited signatures; implementing them does not merge them into a
// single method or depend on interface declaration order. The selected signature follows its
// argument type. A same-signature pair from unrelated bases without an explicit common override
// is instead ambiguous at an indistinguishable call site.
