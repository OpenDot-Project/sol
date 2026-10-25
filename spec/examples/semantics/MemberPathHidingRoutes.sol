// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/semantic contract fixture; interface-default conflict policy remains incomplete.
module examples.semantics.member_path_hiding_routes

interface Root<T> {
    func read(): T
}

interface Left<T>: Root<T> { }
interface Right<T>: Root<T> { }

open class Shared<T>: Left<T>, Right<T> {
    open override func read(): T = default<T>()
}

class Leaf<T>: Shared<T> { }

interface TextRoot { func label(): String }
interface TextBranch: TextRoot { }
interface PropertyLabel { val label: String }
interface OtherBranch: PropertyLabel { }

class MultiRoute: TextBranch, OtherBranch { }

interface ValueBranch { val name: String }
interface CallableRoot { func name(): String }
interface CallableBranch: CallableRoot { }
class MixedRoutes: ValueBranch, CallableBranch { }

interface IntRoot: Root<int> { }
interface StringRoot: Root<String> { }
class ConflictingRoutes: IntRoot, StringRoot { }

func repeatedDiamond(): String {
    val leaf: Leaf<String> = Leaf()
    return leaf.read()
}

func overloadGroup(value: Shared<String>): String {
    return value.read()
}

func conflictingConstructedOwner(): () {
    // Expected declaration diagnostic: Root<int> and Root<String> are distinct constructed owners.
    val invalid = ConflictingRoutes()
}

func mixedCategory(): () {
    val value = MixedRoutes()
    // Expected member diagnostic: callable and property paths remain distinct until conflict policy.
    val invalid = value.name
}

func hiddenBaseDoesNotFallback(value: MultiRoute): () {
    // Expected resolution behavior: a property/function conflict is not chosen by interface order.
    value.label()
}

open class BaseWithOverloads {
    open func convert(value: String): String = value
    open func convert(value: int): String = value.toString()
}

class DerivedWithOneOverride: BaseWithOverloads {
    override func convert(value: String): String = value
}

func retainedInheritedOverload(value: DerivedWithOneOverride): String {
    return value.convert(1)
}

// Path ledger:
//
// 1. Leaf<String> reaches Shared<String> and then Root<String> along Left and Right.
// 2. Shared.read is one constructed declaration candidate with two route identities.
// 3. ConflictingRoutes reaches the same Root symbol at different constructed owners and is not
//    collapsed by declaration identity.
// 4. MultiRoute has unrelated branches. A nearer declaration on one branch cannot erase a valid
//    candidate reachable only through the other branch.
// 5. DerivedWithOneOverride suppresses only the matching String base signature; convert(int) remains.
//
// The member resolver returns a bounded group before the body binder checks operation kind,
// visibility, argument mapping, generic substitution, and overload rank. It never invokes a
// property getter while collecting routes. A path budget failure leaves the group incomplete.
