// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative function-effect fixture. Lambda creation and invocation are separate control edges.
module spec.effects.callbacks

class CallbackFailure: Exception {}
class OtherFailure: Exception {}

func pureValue(): int { 42 }
func checkedValue(): int throws CallbackFailure { 42 }
func failValue(): int throws CallbackFailure { throw CallbackFailure() }

func callPure(action: () -> int): int {
    action()
}

func callChecked(action: () -> int throws CallbackFailure): int throws CallbackFailure {
    action()
}

func callBroader(action: () -> int throws Exception): int throws Exception {
    action()
}

func pureLambdaFits(): int {
    callPure({ 42 })
}

func narrowCallbackFitsBroadBudget(): int throws Exception {
    callBroader({ checkedValue() })
}

func broadCallbackCannotFitPureBudget(action: () -> int throws CallbackFailure): int {
    // Expected: the function value carries an effect that the pure type cannot represent.
    callPure(action)
}

func lambdaBudgetCheckedAtCreation(): () {
    // Expected: the expected callback type has an empty checked-effect budget.
    val action: () -> int = { failValue() }
}

func lambdaCreationDoesNotInvokeBody(): () -> int {
    // Positive: this body is not executed while the closure is created.
    { 42 }
}

func invokeAtCoveredCallSite(action: () -> int throws CallbackFailure): int throws CallbackFailure {
    action()
}

func invokeAtCatchSite(action: () -> int throws CallbackFailure): int {
    try {
        action()
    } catch (failure: CallbackFailure) {
        0
    }
}

func invokeAtUncoveredCallSite(action: () -> int throws CallbackFailure): int {
    // Expected: invocation leaks CallbackFailure.
    action()
}

func functionEffectSetIsNotOrdered(): () throws CallbackFailure, OtherFailure {}

func acceptBothEffects(action: () -> int throws OtherFailure, CallbackFailure): int
    throws CallbackFailure, OtherFailure {
    action()
}

func effectSubtypingIsOneWay(action: () -> int throws Exception): () {
    // Expected: broad-to-narrow conversion cannot erase possible subtypes.
    val narrower: () -> int throws CallbackFailure = action
}

func subtypeEffectFitsSupertype(action: () -> int throws CallbackFailure): () {
    val broader: () -> int throws Exception = action
}

func labeledLambdaLocalReturn(): () -> int {
    answer@ { value: int -> return@answer value }
}

func lambdaCannotReturnFromOuter(): int {
    val action = { -> return 1 }
    0
}

func refCaptureCannotEscape(ref value: int): () -> int {
    // Expected: a ref parameter cannot be captured by an escaping closure.
    { value }
}

// Checked effects are part of function metadata and survive generic substitution, capture
// analysis, callback conversion, and call binding. Candidate rejection is isolated: a failed
// candidate's effect does not leak into a different selected candidate.
