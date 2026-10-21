// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Native bootstrap declaration surface. Full std.core APIs and collectible objects remain pending.
module std.core

open class Object {}
class String {}
open class Throwable {}
open class Exception: Throwable {}
open class RuntimeException: Exception {}
