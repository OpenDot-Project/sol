// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; full accessor visibility/effect validation is pending.
module examples.semantics.member_accessors

class ReadFailure(message: String): Exception(message)

class Account {
    private var storedName: String

    public var displayName: String {
        get = storedName
        private set { storedName = value }
    }

    public var checkedName: String {
        get throws ReadFailure = readName()
        private set { storedName = value }
    }

    private func readName(): String throws ReadFailure = storedName

    func rename(value: String): () {
        displayName = value
    }
}

func readAccount(account: Account): String throws ReadFailure {
    return account.checkedName
}

func writeAccount(account: Account): () {
    // Expected accessibility diagnostic: setter is private.
    account.displayName = "outside"
}

func passPropertyByRef(account: Account): () {
    var output: String = ""
    // Expected storage diagnostic: an accessor is not a writable address.
    copy(ref output, ref account.displayName)
}

func copy(ref left: String, ref right: String): () {
    left = right
}

// Reading checkedName contributes ReadFailure from the getter only. A write would select the
// setter's visibility/effect contract and would not invoke the getter. The property itself is
// never a raw ref/out location; an analyzer cannot manufacture a temporary and pass its address.
//
// Inside Account the private setter is reachable. Outside it, the compiler reports an inaccessible
// setter instead of searching a base declaration or treating the property as immutable storage.
