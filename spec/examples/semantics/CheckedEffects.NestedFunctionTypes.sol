// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative nested callback fixture. Each function type carries its own checked-effect set.
module spec.effects.nestedfunctiontypes

class ReadFailure: Exception {}
class WriteFailure: Exception {}
class ParseFailure: Exception {}

func read(): int throws ReadFailure { 1 }
func write(value: int): () throws WriteFailure {}
func parse(value: int): String throws ParseFailure { "value" }

func acceptProducer(produce: () -> int throws ReadFailure): int throws ReadFailure {
    produce()
}

func acceptTransformer(transform: (int) -> String throws ParseFailure): String throws ParseFailure {
    transform(1)
}

func acceptPipeline(pipeline: () -> ((int) -> String throws ParseFailure) throws ReadFailure): String
    throws ReadFailure, ParseFailure {
    val transform = pipeline()
    transform(1)
}

func createPipeline(): () -> ((int) -> String throws ParseFailure) throws ReadFailure {
    {
        { value: int -> parse(value) }
    }
}

func producerEffectsAreNotTransformerEffects(
    produce: () -> int throws ReadFailure,
    transform: (int) -> String throws ParseFailure
): String throws ReadFailure, ParseFailure {
    transform(produce())
}

func broadOuterEffectDoesNotEraseInnerEffect(
    make: () -> (() -> int throws ReadFailure) throws WriteFailure
): int throws WriteFailure, ReadFailure {
    val action = make()
    action()
}

func omitInnerBudgetIsInvalid(
    make: () -> (() -> int throws ReadFailure) throws WriteFailure
): int throws WriteFailure {
    val action = make()
    // Expected: the nested function value has its own ReadFailure invocation effect.
    action()
}

func nestedPureBoundary(
    make: () -> (() -> int throws ReadFailure) throws WriteFailure
): () throws WriteFailure {
    val action = make()
    // Merely storing the inner function does not invoke it.
}

func returnedCallbackRetainsEffect(): () -> int throws ReadFailure {
    { read() }
}

func returnedCallbackMustNotBeInvokedHere(): () -> int throws ReadFailure {
    // This function returns a callable. Its body's declared effect describes invocation of
    // the returned function, not the creation of the function value.
    { 1 }
}

func nestedCatchAtCorrectDepth(
    make: () -> (() -> int throws ReadFailure) throws WriteFailure
): int throws WriteFailure {
    try {
        val action = make()
        action()
    } catch (failure: ReadFailure) {
        0
    }
}

func catchInnerKeepOuter(
    make: () -> (() -> int throws ReadFailure) throws WriteFailure
): int throws WriteFailure {
    val action = try {
        make()
    } catch (failure: WriteFailure) {
        { 0 }
    }
    try {
        action()
    } catch (failure: ReadFailure) {
        0
    }
}

func checkedFunctionCannotBecomePure(
    action: () -> int throws ReadFailure
): () {
    // Expected: function type conversion cannot erase its nested effect.
    val pure: () -> int = action
}

func pureFunctionFitsCheckedType(action: () -> int): () {
    val checked: () -> int throws ReadFailure = action
}

// Nested function types are recursive contracts. The effect set on the outer invocation does
// not replace the inner callback's effect set, and returning a callback does not call it.
