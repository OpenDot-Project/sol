// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; full member hiding and access recovery are pending.
module examples.semantics.member_hiding

open class BaseLabel {
    open val value: String
    open func value(index: int): String = index.toString()
}

class DerivedLabel: BaseLabel {
    private var value: String

    func readOwn(): String = value
}

open class BaseMethod {
    open func convert(value: String): String = value
    open val convert: String
}

class DerivedMethod: BaseMethod {
    func convert(value: int): String = value.toString()
}

func inaccessibleNearest(value: DerivedLabel): String {
    // Expected diagnostic: private nearest value owns the spelling; no fallback to BaseLabel.value.
    return value.value
}

func derivedMethodGroup(value: DerivedMethod): String {
    // The more-derived callable group owns convert; the hidden base property does not reappear.
    return value.convert(1)
}

func notAPropertyFallback(value: DerivedMethod): String {
    // Expected diagnostic: no applicable convert method; do not read BaseMethod.convert instead.
    return value.convert
}

// Member lookup returns a name group, not a sequence of “try this declaration, then fall back.”
// A nearest inaccessible non-callable declaration blocks a same-spelling base lookup. A nearer
// function overload group hides a base non-callable value. Same-depth mixed declarations are an
// ambiguity that declaration validation should diagnose, never resolve by file order.
