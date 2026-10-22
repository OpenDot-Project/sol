// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Compiler-owned native declarations. Implemented members have verified runtime lowering.
module std.core

open class Object {}
class String {
    val length: int
    func substring(start: int, length: int): String
    func indexOf(value: String, start: int): int
    func compareOrdinal(other: String): int
    func hashCode(): int
}
open class Throwable {}
open class Exception: Throwable {}
open class RuntimeException: Exception {}

object Gc {
    func collect()
}
