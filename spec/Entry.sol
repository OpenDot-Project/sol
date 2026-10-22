// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: intended executable entry; entry validation/runtime/native execution are not implemented.
module arbitrary.application.start

import std.stdio

object Main {
    func main(args: []String): () throws IOException {
        println("Hello, Sol")
        for val argument in args { println(argument) }
    }
}

// The entry is Main object's main([]String): (). No static declaration or entry flag
// selects it. Exactly one executable entry is required in the explicit source set.
// args excludes the executable path. Normal return exits with code 0.
// The entry adapter reports an unhandled Throwable and exits with code 1; declaring
// checked effects here does not exempt ordinary user calls from checked-effect analysis.
// Main's singleton initialization may itself fail. It follows the same unhandled policy.
// Source paths/module names do not choose the entry. Library artifacts do not invent one.
