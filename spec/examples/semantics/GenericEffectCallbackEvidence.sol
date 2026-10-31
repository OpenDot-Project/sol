// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/semantic contract fixture; bounded unique top-level effect matching is unqualified by runtime tests.
module examples.semantics.generic_effect_callback_evidence

class DiskFailure(message: String): Exception(message)
class NetworkFailure(message: String): Exception(message)

func readDisk(): String throws DiskFailure = "disk"
func readNetwork(): String throws NetworkFailure = "network"
func noFailure(): String = "ok"

func <E: Throwable> invoke(callback: () -> String throws E): String throws E = callback()

func readBoth(): String throws DiskFailure, NetworkFailure {
    readDisk()
    return readNetwork()
}

func <E: Throwable, F: Throwable> pair(
    left: () -> () throws E,
    right: () -> () throws F
): () { }

func <E: Throwable> acceptFixedAndOpen(
    callback: () -> String throws DiskFailure, E
): String throws DiskFailure, E = callback()

func <T, E: Throwable> transform(value: T, callback: (T) -> String throws E): String throws E {
    return callback(value)
}

func inferredOneEffect(): String throws DiskFailure {
    return invoke({ readDisk() })
}

func inferredWithOrdinaryType(): String throws DiskFailure {
    return transform("path", { value: String -> readDisk() })
}

func emptyCallbackEffects(): () {
    // Expected inference result: no actual checked effect does not choose E.
    val unresolved = invoke({ noFailure() })
}

func multipleEffectsNeedSetProof(): () {
    // Expected inference result: one open E cannot be selected by zipping throws order.
    val unresolved = invoke({ readBoth() })
}

func twoOpenEffectParameters(): () {
    // Expected inference result: one effect cannot be assigned to E or F by declaration order.
    pair({ readDisk(); () }, { readNetwork(); () })
}

func fixedAndOpenEffectSet(): String throws DiskFailure, NetworkFailure {
    return acceptFixedAndOpen({ readBoth() })
}

func catchOneEffect(): String throws NetworkFailure {
    try {
        readDisk()
        return readNetwork()
    } catch (error: DiskFailure) {
        return "recovered"
    }
}

func callbackBudgetTooNarrow(): () {
    val noThrow: () -> String = { readDisk() }
    // Expected diagnostic: DiskFailure escapes a callback with an empty checked budget.
}

func checkedAndUnchecked(): String throws DiskFailure {
    // RuntimeException is unchecked and does not become an inferred checked E.
    return invoke({
        if (true) { readDisk() } else { throw IllegalStateException("invalid") }
    })
}

// Effect identities form an unordered set. The candidate can infer one E only when the residual
// matching is unique and the constructed effect satisfies E's bounds. A callback with two actual
// checked effects does not infer a supertype merely because both exceptions share Exception unless
// the declared generic/effect conversion rule explicitly permits that widening.
//
// The selected call adds the closed effect to its enclosing callable. Candidate replay discards the
// rejected callback's effect set. Getter/operator/delegate effects are collected from the actual
// selected operations and catches remove only effects they cover.
//
// Parsing this fixture does not prove exception construction, native unwinding, suppressed cleanup
// effects, callback ABI, or full checked-effect coverage.
