// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: generic/type constraints partially available; method binding/specialization are planned.
module demonstrations.generic.names

interface Named { func name(): String }
class Item(private val text: String): Named {
    override func name(): String = this.text
}

func <T: Named> label(value: T): String = value.name()
func <T> identity(value: T): T = value

func demonstrate(item: Item): () {
    val name = label(item)
    val integer = identity(42)
    val explicitInteger = identity<long>(42)
}

// T's bound is nominal. A similarly named unrelated method does not automatically
// implement Named. Literal 42 can be contextually long; an int variable cannot widen.
