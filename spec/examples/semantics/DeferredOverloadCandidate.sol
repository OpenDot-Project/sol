// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; candidate deferral and overload ranking remain under development.
module examples.semantics.deferred_overload_candidate

class Widget { }

func select(value: Widget): String = "widget"
func <T> select(value: T, mapper: (T) -> String): String = mapper(value)

func completeCandidate(): String {
    return select(Widget())
}

func incompleteLambdaCandidate(): String {
    return select(Widget(), { value -> value.toString() })
}

func explicitCandidate(): String {
    return select<Widget>(Widget(), { value: Widget -> value.toString() })
}

// The first call has one complete non-generic candidate. In the second, the generic candidate
// requires contextual lambda analysis; if that analysis is unsupported and could alter the winner,
// the entire call is Deferred instead of claiming the Widget overload is unique. The third call
// supplies explicit type/parameter information but still needs the ordinary overload/effect checks.
//
// No source declaration order, source path, or candidate iteration order can resolve a deferred
// possibility. The selected binding must contain the exact candidate evidence used to rank it.
