// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/semantic contract fixture; all multi-exit generic lambda joins remain under qualification.
module examples.semantics.generic_lambda_result_join

open class Animal { }
class Cat: Animal { }
class Dog: Animal { }

class Pair<A, B>(val first: A, val second: B)

func <T, R> map(value: T, block: (T) -> R): R = block(value)
func <T, R> select(flag: bool, first: () -> R, second: () -> R): R = if (flag) first() else second()

func resultFromExpectedType(): Animal {
    return map(true, { flag -> if (flag) Cat() else Dog() })
}

func nullableBranchResult(): String? {
    return map(true, { flag -> if (flag) "value" else null })
}

func twoOpenTupleResults(): Pair<String, int> {
    return map(true, { flag -> if (flag) Pair("a", 1) else Pair("b", 2) })
}

func branchOrderDoesNotSelectType(): Animal {
    return map(false, { flag -> if (flag) Dog() else Cat() })
}

func incompatibleInvariantResults(): () {
    // Expected diagnostic: Box<Cat> and Box<Dog> cannot be joined by changing invariant T.
    val invalid = map(true, { flag -> if (flag) Box(Cat()) else Box(Dog()) })
}

class Box<T>(val value: T)

func bottomBranch(): Animal {
    return map(true, { flag -> if (flag) Cat() else failAnimal() })
}

func failAnimal(): ! { throw Exception("no animal") }

func allPathsBottom(): ! {
    return map(false, { flag -> if (flag) failAnimal() else failAnimal() })
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

// Every reachable normal lambda exit participates in result joining. A throwing/bottom branch
// contributes no normal value, while its checked effect is tracked separately. Branch order cannot
// choose Cat or Dog when Animal is the unique common result. Invariant Box<T> result shapes cannot
// use a common inner base to rewrite Box's generic argument.
//
// The fixture also separates the immediate expected result type from arbitrary caller inference.
// A local `String` result can close an otherwise open result parameter; an unrelated future use of
// that result elsewhere cannot reach backward into the lambda candidate.
