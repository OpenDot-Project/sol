// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: throws/catch syntax and effect identities; checked-effect/cleanup proof remains planned.
module source.exceptions.rules

import std.stdio

class ConfigException(message: String, cause: Throwable? = null): Exception(message, cause)

func requireName(name: String): String throws ConfigException {
    if name.byteLength == 0 { throw ConfigException("Missing name") }
    name
}

func recover(name: String): String {
    val result = try {
        requireName(name)
    } catch (error: ConfigException) {
        "fallback"
    } finally {
        // Cleanup has no result and cannot transfer control out of this scope.
    }
    result
}

func <E: Throwable> raise(error: E): ! throws E { throw error }

// Throwable and its subtypes are checked, except RuntimeException and its subtree.
// The checked I/O chain is Throwable -> Exception -> std.stdio.IOException.
// Static thrown type determines the obligation; casting RuntimeException to Throwable
// does not hide that static checked obligation. Throw requires non-null Throwable.
// A checked effect must be caught or explicitly declared. Effect lists are not overload keys.
// Catch clauses run most-specific first; an unreachable later catch is an error.
// Override effect budgets may narrow, never widen. Callback types carry checked effects.
// Main may declare checked effects; its adapter is the final reporting boundary.
// Finally/defer run on normal and exceptional exit. A cleanup failure preserves the
// primary failure and records a suppressed failure; it does not silently replace it.
