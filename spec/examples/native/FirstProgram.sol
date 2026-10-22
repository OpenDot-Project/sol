// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: executable with the development LLVM AOT bootstrap component on Windows x86-64.
module examples.native.firstprogram

import std.stdio

func add(left, right: int): int = left + right

object Main {
    func main(args: []String) throws IOException {
        println("Hello, Sol!")
        println(add(20, 22))
        println("Argument count: ", args.length)
    }
}
