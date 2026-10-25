// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative returned-function fixture. The outer call and returned invocation have separate effects.
module spec.effects.functionvaluereturns

class CreateFailure: Exception {}
class InvokeFailure: Exception {}
class ResultFailure: Exception {}

func invokeLater(): int throws InvokeFailure { 1 }
func createCallable(): () -> int throws InvokeFailure { { invokeLater() } }
func createPureCallable(): () -> int { { 1 } }
func failCreation(): () -> int throws CreateFailure { throw CreateFailure() }

func makeAndReturn(): () -> int throws InvokeFailure {
    createCallable()
}

func creationBudgetOnly(): () -> int throws InvokeFailure {
    createCallable()
}

func callReturned(): int throws InvokeFailure {
    val callback = createCallable()
    callback()
}

func missingInvocationBudget(): int {
    val callback = createCallable()
    // Expected: effect is introduced at callback invocation.
    callback()
}

func handleInvocationLocally(): int {
    val callback = createCallable()
    try {
        callback()
    } catch (failure: InvokeFailure) {
        0
    }
}

func makeDoesNotInvoke(): () -> int throws InvokeFailure {
    // Expected: only a function value is returned; invocation may fail later.
    { invokeLater() }
}

func creationCanThrowBeforeReturning(): () -> int throws CreateFailure, InvokeFailure {
    val callback = failCreation()
    callback
}

func catchCreationButNotInvocation(): int throws InvokeFailure {
    val callback = try {
        failCreation()
    } catch (failure: CreateFailure) {
        createCallable()
    }
    // The handler result is a callback value; invoking it adds InvokeFailure.
    callback()
}

func callbackResultHasItsOwnType(): () -> String throws InvokeFailure {
    { invokeLater().to_string() }
}

func returnedEffectFitsParentFunctionType(): () {
    val callback: () -> int throws Exception = createCallable()
    callback()
}

func checkedReturnCannotBeStoredAsPure(): () {
    val callback: () -> int = createCallable()
}

func functionReturningFunction(
    factory: () -> (() -> int throws InvokeFailure) throws CreateFailure
): () -> int throws InvokeFailure throws CreateFailure {
    factory()
}

func callFactoryAndResult(
    factory: () -> (() -> int throws InvokeFailure) throws CreateFailure
): int throws CreateFailure, InvokeFailure {
    val callback = factory()
    callback()
}

// A return type may itself be a function type with a checked budget. The outer callable's
// throws list describes evaluation of its body and return expression. The returned function's
// throws list describes a later call through the value.
