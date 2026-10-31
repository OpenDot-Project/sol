// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; complete multiple-bound member lookup is pending.
module examples.semantics.multiple_bound_members

interface HasName { func name(): String }
interface HasCode { func code(): int }

class Item: HasName, HasCode {
    override func name(): String = "item"
    override func code(): int = 1
}

func <T> describe(value: T): String where T: HasName, T: HasCode {
    return value.name() + value.code().toString()
}

func useItem(): String {
    val item: Item = Item()
    return describe(item)
}

interface LeftName { func name(): String }
interface RightName { func name(): String }

func <T> ambiguous(value: T): String where T: LeftName, T: RightName {
    // Expected ambiguity unless one declaration identity satisfies both bounds.
    return value.name()
}

class SharedName: LeftName, RightName {
    override func name(): String = "shared"
}

func sharedName(value: SharedName): String {
    return ambiguous(value)
}

// Independent members from different bounds are both callable. The same source spelling on two
// distinct bounds is not automatically one declaration. A concrete type implementing both does
// not erase the bound-level ambiguity unless declaration identity/override relationships prove one
// shared contract.
