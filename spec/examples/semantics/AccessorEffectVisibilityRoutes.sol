// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/semantic contract fixture; complete accessor effect and indexer lowering is pending.
module examples.semantics.accessor_effect_visibility_routes

class ReadFailure(message: String): Exception(message)
class WriteFailure(message: String): Exception(message)
class UpdateFailure(message: String): Exception(message)

open class BaseCell<T>(initial: T) {
    protected var value: T {
        get throws ReadFailure = readValue()
        protected set throws WriteFailure { writeValue(value) }
    }

    protected func readValue(): T throws ReadFailure = initial
    protected func writeValue(value: T): () throws WriteFailure { }
}

class TextCell(initial: String): BaseCell<String>(initial) {
    func readOwn(): String throws ReadFailure = this.value

    func writeOwn(value: String): () throws WriteFailure {
        this.value = value
    }

    func readSibling(other: TextCell): String throws ReadFailure = other.value

    func useBaseReceiver(other: BaseCell<String>): String throws ReadFailure {
        // Expected protected receiver diagnostic from the derived owner context.
        return other.value
    }
}

func readOutside(cell: TextCell): String throws ReadFailure {
    // Expected access diagnostic: the property is protected.
    return cell.value
}

func writeOutside(cell: TextCell): () throws WriteFailure {
    // Expected access diagnostic: the setter is protected independently of the getter.
    cell.value = "changed"
}

class PublicReadPrivateWrite {
    public var name: String {
        get = "readable"
        private set { }
    }
}

func readPublic(account: PublicReadPrivateWrite): String = account.name

func cannotWritePrivate(account: PublicReadPrivateWrite): () {
    // Expected diagnostic: readable does not imply writable.
    account.name = "hidden"
}

class PublicWritePrivateRead {
    public var state: String {
        private get = "hidden"
        set { }
    }
}

func writePublic(account: PublicWritePrivateRead): () {
    account.state = "visible"
}

func cannotReadPrivate(account: PublicWritePrivateRead): String {
    // Expected diagnostic: writable does not imply readable.
    return account.state
}

func compoundOperation(cell: TextCell): () throws ReadFailure, WriteFailure, UpdateFailure {
    // Required order: getter -> selected operator -> setter; receiver is evaluated once.
    cell.value += updateValue()
}

func updateValue(): String throws UpdateFailure = "!"

func safeRead(cell: TextCell?): String? throws ReadFailure {
    return cell?.value
}

func propertyCannotBeRef(cell: TextCell): () {
    var copy: String = ""
    // Expected storage diagnostic: getter/setter accessors do not produce a location.
    exchange(ref copy, ref cell.value)
}

func exchange(ref left: String, ref right: String): () {
    left = right
}

// A read adds ReadFailure only. A simple write adds WriteFailure only. A compound update adds the
// selected getter, operator, conversion, and setter effects in execution order. Safe access skips
// the getter effect on the null branch. None of these accessor expressions is raw ref/out storage.
//
// The constructed base owner is BaseCell<String>, while the actual protected receiver type may be
// TextCell. Those identities are intentionally separate. A failed accessor check does not search a
// farther public declaration with the same spelling.
