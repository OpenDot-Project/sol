// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/semantic contract fixture; all multi-exit generic lambda joins remain under qualification.
module examples.semantics.generic_lambda_result_join

open class Animal { }
class Cat: Animal { }
class Dog: Animal { }

class Pair<A, B>(val first: A, val second: B)

func <T, R> map(value: T, block: (T) -> R): R = block(value)
func <T, R> select(flag: bool, first: () -> R, second: () -> R): R {
    if flag { return first() }
    return second()
}

func chooseAnimal(flag: bool): Animal {
    if flag { return Cat() }
    return Dog()
}

func chooseNullable(flag: bool): String? {
    if flag { return "value" }
    return null
}

func choosePair(flag: bool): Pair<String, int> {
    if flag { return Pair("a", 1) }
    return Pair("b", 2)
}

func chooseAnimalReverse(flag: bool): Animal {
    if flag { return Dog() }
    return Cat()
}

func chooseAnimalOrFail(flag: bool): Animal {
    if flag { return Cat() }
    return failAnimal()
}

func resultFromExpectedType(): Animal {
    return map(true, { flag -> chooseAnimal(flag) })
}

func nullableBranchResult(): String? {
    return map(true, { flag -> chooseNullable(flag) })
}

func twoOpenTupleResults(): Pair<String, int> {
    return map(true, { flag -> choosePair(flag) })
}

func branchOrderDoesNotSelectType(): Animal {
    return map(false, { flag -> chooseAnimalReverse(flag) })
}

func incompatibleInvariantResults(): () {
    // Expected diagnostic: Box<Cat> cannot become Box<Animal> by changing invariant T.
    val invalid: Box<Animal> = map(true, { flag -> Box(Cat()) })
}

class Box<T>(val value: T)

func bottomBranch(): Animal {
    return map(true, { flag -> chooseAnimalOrFail(flag) })
}

func failAnimal(): ! { throw Exception("no animal") }

func allPathsBottom(): ! {
    return map(false, { flag -> failAnimal() })
}

func statementTailIsEmptyTuple(): () {
    val ignored = map(1, { value: int -> val copy = value })
}

func expectedTypeClosesCallback(): String {
    val result: String = select(
        true,
        { "first" },
        { "second" }
    )
    return result
}

func conflictingExpectedAndInput(): () {
    // Expected diagnostic: the input/body lower type conflicts with the enclosing result bound.
    val invalid: String = map(Cat(), { animal: Cat -> animal })
}

func positionalEchoWithClosedInput(): String {
    return map("text", { $0 })
}

func positionalEchoWithTwoOpenVariables(): () {
    // Expected inference result: R = T remains open without an independent seed.
    val unresolved = map(default<String>(), { $0 })
}

// The current syntax models if as a statement; if-expressions and branch-result inference are
// outside this grammar's scope. These typed helpers keep the callback examples parseable while the
// contracts below describe the future multi-exit result-join rule. Invariant Box<T> result shapes
// cannot use a common inner base to rewrite Box's generic argument.
//
// The fixture also separates the immediate expected result type from arbitrary caller inference.
// A local `String` result can close an otherwise open result parameter; an unrelated future use of
// that result elsewhere cannot reach backward into the lambda candidate.
