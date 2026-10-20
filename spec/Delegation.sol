// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: declaration syntax only; protocol binding/metadata/forwarders remain planned.
module source.delegation.rules

import std.reflect

class NameDelegate(private var storedValue: String) {
    operator func getValue(thisRef: Object?, property: SolProperty<String>): String {
        this.storedValue
    }
    operator func setValue(thisRef: Object?, property: SolProperty<String>, value: String): () {
        this.storedValue = value
    }
}

class User(initialName: String) {
    var fullName: String by NameDelegate(initialName)
}

interface Logger { func log(message: String): () }
class ForwardingLogger(private val target: Logger): Logger by target

// by is contextual property/interface delegation, not an arbitrary binary operator.
// The delegate expression evaluates once per initialization. Optional operator
// provideDelegate(thisRef,property) runs once and supplies the stored delegate.
// Each read/write calls operator getValue/setValue. A val requires no setter.
// These named operator protocols are instance members, unlike companion token operators.
// SolProperty<T> identifies a declaration and closed type arguments, not an owner value.
// Owners share a descriptor but keep separate delegate storage; descriptors do not root owners.
// Top-level/local thisRef is null; instance thisRef must pass initialization/escape checking.
// Getter, setter and provideDelegate checked effects belong to read, write and initialization
// respectively. Delegated properties cannot supply ref/out writable storage.
// Interface by accepts only interfaces; explicit overrides win. Conflicting delegates
// require explicit resolution, and self-dispatch inside the target stays on that target.
