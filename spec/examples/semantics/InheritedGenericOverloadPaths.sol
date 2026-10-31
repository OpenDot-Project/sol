// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/semantic contract fixture; complete inherited generic override analysis is pending.
module examples.semantics.inherited_generic_overload_paths

interface Source<T> { func source(): T }

open class Store<T> {
    open func read(): T = default<T>()

    open func convert(value: T): String = "generic"

    open func convert(value: String): String = value

    open func <R> map(value: T, transform: (T) -> R): R = transform(value)
}

open class BaseStore<U>: Store<U>, Source<U> {
    override func read(): U = default<U>()
    override func source(): U = default<U>()
}

class TextStore: BaseStore<String> {
    override func read(): String = "text"
    override func convert(value: String): String = value
}

func inheritedOwnerSubstitution(store: TextStore): String {
    return store.read()
}

func retainedBaseOverload(store: TextStore): String {
    // The int overload is independent and would remain; this String route is the matching override.
    return store.convert("value")
}

func inheritedGenericMethod(store: TextStore): int {
    return store.map("value", { item: String -> item.byteLength })
}

func genericMethodParameterDoesNotAliasOwner(store: Store<String>): String {
    return store.map("value", { text: String -> text })
}

open class BaseRef<T> {
    open func replace(ref value: T): () { }
    open func replace(value: String): () { }
}

class DerivedRef: BaseRef<String> {
    override func replace(ref value: String): () { }
}

func refAndValueCandidates(value: DerivedRef): () {
    var item: String = "before"
    value.replace(ref item)
    value.replace("after")
}

open class DefaultBase<T> {
    open func label(value: T, suffix: String = "!"): String = suffix
}

class DefaultDerived: DefaultBase<String> {
    override func label(value: String, suffix: String = "?"): String = suffix
}

func baseDefaultContract(value: DefaultDerived): String {
    // Override validation rejects redefining the inherited default; runtime selection is separate.
    return value.label("name")
}

interface CommonRead<T> { func read(): T }
interface LeftRead<T>: CommonRead<T> { }
interface RightRead<T>: CommonRead<T> { }

class ReadDiamond<T>: LeftRead<T>, RightRead<T> {
    override func read(): T = default<T>()
}

func oneConstructedDiamond(value: ReadDiamond<String>): String = value.read()

// The receiver path determines `Store<T>`'s T before member method R is inferred. Source parameter
// spelling never joins owner and method identities. A valid same-signature override suppresses
// only its corresponding base candidate; a distinct overload remains available. Repeated paths to
// one constructed owner collapse but retain path origins.
//
// Default values belong to the static declaration contract. They do not participate in overload
// identity or generic evidence, and an override cannot replace them with its own expression.
//
// This is a source-contract fixture. It does not assert virtual/interface dispatch or generic body
// monomorphization exists in the native backend.
