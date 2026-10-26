// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; complete companion/member category checking is pending.
module examples.semantics.type_object_receivers

object BuildStamp {
    const val VERSION: String = "0.0"
    func current(): String = VERSION
}

class Formatter(val prefix: String) {
    companion object {
        const val DEFAULT_WIDTH: int = 80
        func defaultWidth(): int = DEFAULT_WIDTH
    }

    func format(value: String): String = prefix + value
}

func formatAll(formatter: Formatter): String {
    val width: int = Formatter.defaultWidth()
    val version: String = BuildStamp.current()
    val formatted: String = formatter.format(version)
    return formatted + width.toString()
}

func wrongTypeReceiver(): () {
    // Expected diagnostic: Formatter has no instance receiver to call format on.
    Formatter.format("text")
}

func shadowedName(formatter: Formatter): String {
    val BuildStamp: Formatter = formatter
    // This is a member lookup on the local value, not a retry as object qualifier.
    return BuildStamp.format("text")
}

// Object members use the singleton object identity. Companion members use the companion object
// associated with a type. Instance members require an instance expression. The spelling of the
// root name alone cannot move a lookup between those categories after a failure.
