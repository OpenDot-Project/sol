// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; full category conflict diagnostics are pending.
module examples.semantics.member_name_categories

interface HasValue { val current: String }
interface HasFunction { func current(): String }

class ValueAndFunction: HasValue, HasFunction {
    override val current: String = "value"
    override func current(): String = "function"
}

func readValue(value: ValueAndFunction): String {
    // Expected ambiguity if property and callable are distinct same-depth names.
    return value.current
}

func callFunction(value: ValueAndFunction): String {
    // Expected ambiguity at name merge unless the language declaration explicitly resolves it.
    return value.current()
}

object Services {
    func current(): String = "service"
}

class ServiceHolder {
    val Services: String = "shadow"

    func localValue(): String = Services
}

func singletonCall(): String = Services.current()

// A member group retains whether a name is a property, method, nested type, indexer, or variant.
// Call syntax does not transform an arbitrary property into a method. A missing method does not
// fall back to a module/object qualifier when the receiver expression is a local shadow.
