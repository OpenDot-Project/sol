// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/AST fixture; stdio, entry adapter and native execution are planned.
module demonstrations.console

import std.stdio
import std.fmt

object Main {
    func main(args: []String): () throws IOException {
        println("Hello, Sol")
        for val argument in args {
            println(format("argument: {}", argument))
        }
    }
}

// With arguments Alice and 20, intended output is:
// Hello, Sol
// argument: Alice
// argument: 20
// The executable path is not an element of args.
