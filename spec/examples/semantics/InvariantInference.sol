// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; complete generic overload validation remains pending.
module examples.semantics.invariant_inference

open class Animal(val name: String) { }
class Cat(name: String): Animal(name) { }
class Dog(name: String): Animal(name) { }

class Box<T>(val value: T)
class PairBox<A, B>(val first: A, val second: B)

func <T> keep(value: T): T = value
func <T> keepBox(value: Box<T>): T = value.value
func <A, B> pair(value: PairBox<A, B>): PairBox<A, B> = value

func inferSingle(): Cat = keep(Cat("Milo"))

func inferNested(): Cat = keepBox(Box(Cat("Milo")))

func inferPair(): PairBox<Cat, Dog> {
    val input: PairBox<Cat, Dog> = PairBox(Cat("Milo"), Dog("Rex"))
    return pair(input)
}

func invarianceRejects(): () {
    val cats: Box<Cat> = Box(Cat("Milo"))
    val animals: Box<Animal> = Box(Animal("any"))
    // Expected diagnostic: the same invariant Box<T> cannot accept both closed arguments.
    val conflict: Animal = chooseBox(cats, animals)
}

func <T> chooseBox(left: Box<T>, right: Box<T>): T = left.value

func lowerBoundsChooseAnimal(): Animal {
    return choose(Cat("Milo"), Dog("Rex"))
}

func <T> choose(left: T, right: T): T = left

func explicitTypeArguments(): Animal {
    return choose<Animal>(Cat("Milo"), Dog("Rex"))
}

// `Box<Cat>` and `Box<Animal>` do not infer a Box<Animal> conversion. The call is an intentional
// negative example even if both contained objects derive from Animal. Direct `T` input bounds are
// different: they may select one unique common nominal type. Explicit type arguments supply that
// type directly, after which each argument is still checked for conversion.
//
// Parameter order does not select the join. Reversing Cat and Dog in choose has the same inferred
// identity whenever Animal is the unique common nominal candidate in the declared base graph.
