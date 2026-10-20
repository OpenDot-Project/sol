// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: declaration syntax/indexing/default origins; generated members and dispatch remain planned.
module source.objects.rules

interface Named { func name(): String }

open class Person(val id: ulong, var fullName: String): Named {
    constructor(name: String): this(0, name) {}
    init { requireName(this.fullName) }

    val displayName: String { get = this.fullName }
    var visits: int { get; private set; } = 0

    override func name(): String = this.fullName
    open func describe(prefix: String = "person"): String = prefix + this.fullName

    companion object {
        func create(name: String): Person = Person(name)
    }
}

class Employee(id: ulong, name: String): Person(id, name) {
    // Defaults come from Person.describe; repeating the default here is rejected.
    override func describe(prefix: String): String = prefix + ":" + this.fullName
}

func requireName(name: String): () {
    if name.byteLength == 0 { throw RuntimeException("Empty name") }
}

object SessionIds {
    private var _next: ulong = 0
    func next(): ulong { this._next += 1; this._next }
}

data class Contact(val name: String, val address: String)
sealed class State
class Ready: State
class Failed(val reason: String): State

// Classes are final unless open/abstract/sealed. One concrete base and multiple
// interfaces are permitted. Constructors call a base/this chain before publication.
// Primary val/var parameters declare properties; ordinary parameters are constructor-only.
// Default visibility is public; private/internal/protected apply their source contracts.
// An override is explicit. Object and companion scopes replace any static-member keyword.
// Singletons initialize lazily and publish once. A failed initializer caches its failure.
// Data members derive from primary properties; copy is shallow, not a deep clone.
// Sealed subtype sets belong to one distribution/source namespace, not one file path.
