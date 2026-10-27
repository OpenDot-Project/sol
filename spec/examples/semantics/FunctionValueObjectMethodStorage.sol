// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: native cases for receiver-free methods on eligible stateless top-level objects.
module examples.semantics.function_value_object_method_storage

import std.stdio

object Arithmetic {
    func increment(value: int): int = value + 1
    func twice(value: int): int = value * 2
    func <T> identity(value: T): T = value
    func <T> apply(value: T, callback: (T) -> T): T = callback(value)
}

object Main {
    func main(args: []String) throws IOException {
        val callbacks: []((int) -> int) = [Arithmetic.increment, Arithmetic.twice]
        val nested: [][]((int) -> int) = [[Arithmetic.increment], [Arithmetic.twice]]
        val identities: []((String) -> String) = [Arithmetic.identity]

        Gc.collect()
        println(callbacks[0](41))
        println(nested[1][0](21))
        println(identities[0]("Sol"))
        println(Arithmetic.apply<int>(40, Arithmetic.increment))
    }
}
// expect: AOT and ORC output 42, 42, Sol, 41; function pointers carry no receiver or GC environment.
