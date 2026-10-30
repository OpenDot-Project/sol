// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
module examples.interface_implementations

interface Reader<T> {
    func read(value: T): T = value
}

interface Indexed {
    this[index: int, offset: int = 0]: int { get = index + offset }
}

class Direct: Reader<int>, Indexed {
    override func read(value: int): int = super<Reader<int>>.read(value)
}

class Forward<T>(delegate: Reader<T>): Reader<T> by delegate {}

interface ReadItem { val item: int { get = 1 } }
interface WriteItem { var item: int { set {} } }
class Combined: ReadItem, WriteItem {}

func use(value: Direct, item: Combined): int {
    item.item = value[index = 2, offset = 1]
    value.read(item.item)
}

// Independent defaults for one operation require an explicit override at the type declaration.
// A bodyless re-override removes its ancestor default; a concrete owner must supply that operation.
// Replacing super<Reader<int>> above with an indirect or differently constructed interface is invalid.
// These are source-analysis examples; native dispatch and forwarding still receive SOL2400.
