// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative process-boundary fixture. No executable is emitted by this file.
module spec.effects.unhandledboundary

import std.stdio

class StartupFailure: Exception {}

func loadArguments(args: []String): int throws StartupFailure {
    if args.length == 0 {
        throw StartupFailure()
    }
    args.length
}

object Main {
    func main(args: []String): () throws StartupFailure {
        val count = loadArguments(args)
        println(count)
    }
}

func ordinaryCallerMustHandle(args: []String): int {
    // Expected: Main's adapter contract does not exempt this ordinary helper.
    loadArguments(args)
}

func explicitlyHandleAtLibraryBoundary(args: []String): int {
    try {
        loadArguments(args)
    } catch (failure: StartupFailure) {
        0
    }
}

func foreignStatusAdapter(status: int): int {
    // Native/CLR/Ruby failures are translated at the bridge into an explicit result or a
    // declared Sol Throwable. A foreign exception object does not unwind into this function.
    status
}

// At process entry, an uncaught Sol Throwable is rendered as a sanitized diagnostic to stderr
// and the process exits nonzero. Diagnostic rendering must not expose arbitrary object fields,
// private paths, secrets, or unsafe memory contents. The entry adapter is the final reporting
// boundary; ordinary functions still preserve their declared checked-effect contract.
