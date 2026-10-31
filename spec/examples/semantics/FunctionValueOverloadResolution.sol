// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
module examples.semantics.function_value_overload_resolution

open class Animal: Object {}
class Dog: Animal {}

func choose(value: Object): int = 1
func choose(value: String): int = 2

func selectOverload(): () {
    val callback: (String) -> int = choose
}

func equallyRanked(value: Object): Dog { for {} }
func equallyRanked(value: Animal): Animal { for {} }

func ambiguousOverload(): () {
    val callback: (Dog) -> Animal = equallyRanked
}

func missingContext(): () {
    val callback = choose
}
