// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/semantic contract fixture; candidate-local lambda inference is only partially implemented.
module examples.semantics.generic_lambda_evidence_transaction

interface HasText { func text(): String }
interface HasNumber { func number(): int }

class InputValue: HasText, HasNumber {
    override func text(): String = "value"
    override func number(): int = 3
}

class Box<T>(val value: T)

func <T, R> mapOne(value: T, transform: (T) -> R): R = transform(value)
func <T, R> mapPair(first: T, second: T, transform: (T) -> R): R = transform(first)
func <T, R> run(value: T, callback: (ref T) -> R): R = callback(ref value)
func select(callback: (String) -> int): int = callback("")
func select(callback: (int) -> int): int = callback(0)
func <T, R> withDefault(value: T, fallback: T = value, transform: (T) -> R): R = transform(value)
func <T, R> around(prefix: String, vararg values: T, suffix: String, action: (T) -> R): R {
    return action(values[0])
}

func namedParameterAndResult(): int {
    return mapOne("river", { name: String -> name.byteLength })
}

func positionalParameterAndResult(): int {
    return mapOne("river", { $0.byteLength })
}

func twoInputBoundsAndCallback(): int {
    return mapPair("left", "right", { text: String -> text.byteLength })
}

func explicitOwnerAndInferredResult(): String {
    return mapOne<String, String>("river", { $0 })
}

func callbackExpectedFromNamedArgument(): int {
    return mapOne(transform = { item: String -> item.byteLength }, value = "lake")
}

func callbackAsFunctionValue(): int {
    val byteCount: (String) -> int = { item: String -> item.byteLength }
    return mapOne("harbor", byteCount)
}

func callbackResultUsesExpectedType(): String {
    val chosen: String = mapOne(InputValue(), { source: InputValue -> source.text() })
    return chosen
}

func callbackEchoDoesNotSeedTwoOpenParameters(): () {
    // Expected inference result: if both T and R are open, returning the contextual T from $0 is
    // an equality relation, not a concrete type chosen from the lambda's own expected input.
    val unresolved = mapOne(default<InputValue>(), { $0 })
}

func callbackParameterModeMismatch(): () {
    // Expected diagnostic: a value lambda cannot satisfy a ref function parameter.
    val invalid = run("abc", { item: String -> item })
}

func candidateLocalBodyError(): () {
    // Each candidate receives an independent `item` type. The first body is valid; the second
    // candidate cannot reuse a String binding after its own contextual check fails.
    val result = select { item -> item.byteLength }
}

func noImplicitIt(): () {
    // Expected syntax/semantic diagnostic: a headerless lambda has no implicit parameter name.
    val invalid = mapOne("text", { it.byteLength })
}

func resultBranchJoin(): int {
    return mapOne(true, { flag -> if (flag) 1 else 2 })
}

func incompatibleResultBranches(): () {
    // Expected diagnostic: String and int have no unique permitted common result here.
    val invalid = mapOne(true, { flag -> if (flag) "text" else 4 })
}

func bottomBranchHasNoResult(): String {
    return mapOne(true, { flag -> if (flag) "ready" else fail() })
}

func fail(): ! { throw Exception("stop") }

func trailingLambdaEvidence(): int {
    return withDefault("text") { value: String -> value.byteLength }
}

func middleVarargAndTrailingLambda(): int {
    return around("[", "a", "b", suffix = "]") { item: String -> item.byteLength }
}

// Evidence ledger for this fixture:
//
// - `value` supplies input evidence before lambda contextualization;
// - explicit lambda parameter annotations add exact function-shape facts;
// - `$0` uses the same candidate-specific parameter type without implicit `it`;
// - an independent body result supplies a lower/result fact for R;
// - expected local type may supply a separate call-result upper fact;
// - a value function supplies its already-typed signature and effect set;
// - named/default/vararg mapping assigns the callback to one formal before inference;
// - invalid and rejected candidate work must not leak local types or diagnostics.
//
// This fixture is parsed with the other published source contracts. A syntax parse is not proof of
// overload selection, flow joins, checked effects, closure storage, or executable lambda code.
