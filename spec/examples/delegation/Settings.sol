// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: by/operator syntax; protocol resolution, descriptor metadata and runtime are planned.
module demonstrations.settings

import std.reflect

class TextSetting(private var stored: String) {
    operator func getValue(thisRef: Object?, property: SolProperty<String>): String = this.stored
    operator func setValue(thisRef: Object?, property: SolProperty<String>, value: String): () {
        this.stored = value
    }
}

class Settings(initialName: String) {
    var fullName: String by TextSetting(initialName)
    val displayName: String { get = this.fullName }
}

func change(settings: Settings): String {
    settings.fullName = "Sol"
    settings.displayName
}

// Each Settings owns its delegate state. Every fullName descriptor represents the same
// declaration, not a particular Settings object. Reads and writes call the delegate once.
