// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; constrained receiver body verification remains incomplete.
module examples.semantics.constrained_receiver

interface Readable<T> {
    func read(): T
}

interface Rewindable {
    func rewind(): ()
}

interface Named {
    func name(): String
}

class MemoryStream<T>(val items: []T): Readable<[]T>, Rewindable, Named {
    override func read(): []T = items
    override func rewind(): () { }
    override func name(): String = "memory"
}

func <T> inspect(value: T): String
    where T: Readable<[]String>, T: Rewindable, T: Named {
    value.rewind()
    val contents: []String = value.read()
    return value.name() + contents.length.toString()
}

func use(): String {
    val stream: MemoryStream<String> = MemoryStream(["a", "b"])
    return inspect(stream)
}

interface SharedA { func shared(): String }
interface SharedB { func shared(): String }

open class SharedBase: SharedA, SharedB {
    open override func shared(): String = "shared"
}

class SharedChild: SharedBase { }

func <T> readShared(value: T): String where T: SharedA, T: SharedB {
    return value.shared()
}

func useShared(): String = readShared(SharedChild())

// The generic function's constraints are all active. The compiler specializes each constructed
// bound, merges the same declaration reached by multiple paths, and retains unrelated declarations
// as distinct candidates. SharedBase supplies one declaration identity through both inherited
// interface paths. Source order of the where clauses cannot choose one bound.
//
// MemoryStream<String> does not gain an implicit conversion to `Readable<String>` or erase its
// generic argument. Its exact implemented contract is Readable<[]String>, which is the bound used
// by inspect.
