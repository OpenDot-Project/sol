// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: intended application shape; stdio, entry adapter and native execution remain planned.
// Source root: spec/examples/layout/src. Source set: Greeter.sol + Main.sol.
module myproject.example

import std.stdio

object Main {
    func main(args: []String): () throws IOException {
        val greeter = Greeter("Sol")
        println(greeter.message())
    }
}

// Intended output: Hello, Sol. This source set shares a module across two PascalCase
// files under its conventional lowercase path; no one-type-per-file restriction is implied.
