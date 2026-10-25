// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/AST fixture; complete member specialization and native lowering are planned.
module examples.semantics.constructed_bases

interface Snapshot<T> {
    func snapshot(): []T
}

open class Storage<T>(val initial: []T): Snapshot<T> {
    protected var current: []T = initial

    open override func snapshot(): []T = current

    open func firstOrDefault(fallback: T): T {
        if current.length == 0 {
            return fallback
        }
        return current[0]
    }
}

open class FilteredStorage<U>(initial: []U): Storage<U>(initial) {
    private var visibleCount: int = initial.length

    open func visible(): []U {
        return current
    }

    open override func snapshot(): []U = this.visible()
}

class NamedStorage<V>(initial: []V, val label: String): FilteredStorage<V>(initial) {
    override func snapshot(): []V = super.snapshot()

    func labelAndCount(): String = label + ":" + this.visible().length.toString()
}

func useStorage(storage: NamedStorage<String>): String {
    val all: []String = storage.snapshot()
    val first: String = storage.firstOrDefault(fallback = "empty")
    val shown: []String = storage.visible()
    return storage.labelAndCount() + first + all.length.toString() + shown.length.toString()
}

func <T> useOpenGeneric(storage: NamedStorage<T>, fallback: T): T {
    return storage.firstOrDefault(fallback)
}

// Resolution obligations:
// - `NamedStorage<String>` specializes V, U, and Storage<T> edge by edge.
// - `snapshot()` retains the most-derived override while returning []String.
// - `firstOrDefault` is inherited and has a substituted String parameter/result.
// - The generic body keeps T open and does not capture a concrete String identity from the
//   preceding closed example.
// - `current` is protected and `visibleCount` is private to FilteredStorage.
// - The compiler checks the receiver and accessibility before trying an overload.
// - A different file order for these declarations produces identical member identities.
