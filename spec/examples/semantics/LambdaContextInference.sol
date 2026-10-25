// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; candidate-local generic lambda result inference is incomplete.
module examples.semantics.lambda_context_inference

interface TextSource { func text(): String }
interface NumberSource { func number(): int }

class LiteralSource: TextSource, NumberSource {
    override func text(): String = "value"
    override func number(): int = 1
}

func <T, R> apply(value: T, transform: (T) -> R): R = transform(value)

func typedLambda(): int {
    return apply("items", { text: String -> text.byteLength })
}

func positionalLambda(): int {
    return apply("items", { $0.byteLength })
}

func explicitTypeLambda(): String {
    return apply<String, String>("items", { value: String -> value })
}

func callbackBounds(): () {
    val text: (String) -> int = { value: String -> value.byteLength }
    val copied = apply("abc", text)
}

func noImplicitIt(): () {
    // Expected diagnostic: a named lambda parameter or positional form is required.
    val invalid = apply("abc", { it.byteLength })
}

// The first ordinary argument provides T before the lambda is contextually analyzed. The expected
// function type then supplies the parameter type for `$0`; the body can provide R if its expression
// type is available. Each overload candidate receives an isolated lambda analysis context.
//
// `it` is intentionally not part of Sol's lambda grammar. A function value can provide its own
// already-typed signature as evidence, but invocation effects and parameter modes remain part of
// the function type and must be checked separately.
