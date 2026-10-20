// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: explicit module/import/partial declaration analysis; linkage remains planned.
module logical.api.unrelated.to.path

import std.stdio
import text = std.fmt
import std.stdio.file : loadText = readText
public import std.math

partial class Panel {
    val title: String
    constructor(title: String) { this.title = title }
}

partial class Panel {
    func describe(): String = this.title
}

partial struct Coordinates { var x: int }
partial struct Coordinates { var y: int }
partial ref struct Cursor { val position: int }
partial ref struct Cursor { func current(): int = this.position }
partial object Versions { const val CURRENT: int = 1 }
partial object Versions { func current(): int = CURRENT }

// A logical module is declared explicitly in every file and never inferred from its path.
// std.core is implicitly imported. Imports belong to the file, declarations to its module.
// Normal imports expose public names; module aliases qualify access. Selective local=original
// renames are explicit. public import re-exports public symbols, without widening private access.
// Lookup does not choose an ambiguous import by source order. Ordinary signature cycles can
// form compilation groups; re-export/inheritance/inline layout cycles are errors.
// Partial class/struct/ref struct/object parts share module+nesting+name+arity/distribution.
// Every repeated part is partial and compatible. At most one part contains initialization.
// Part path/input order is not initialization order or ABI identity.
