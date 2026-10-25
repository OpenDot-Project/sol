// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; cross-file order invariance needs semantic oracle coverage.
module examples.semantics.source_order_independence

interface Root<T> { func get(): T }
interface Left<T>: Root<T> { }
interface Right<T>: Root<T> { }

class Shared<T>: Left<T>, Right<T> {
    override func get(): T = default<T>()
}

class Reader<T>(val source: Root<T>) {
    func read(): T = source.get()
}

func readText(reader: Reader<String>): String {
    return reader.read()
}

// These declarations form a logical source module independent of file paths. A compiler may index
// separate files in any enumeration order, but source/module/symbol sorting cannot define override
// behavior or pick one of Left/Right. Both paths arrive at the same Root<T> identity and candidate.
//
// Reorder interface declarations, move the partial class part to another source file, or provide
// the caller before the declarations. The resulting member and inferred type identities remain
// equivalent. Source spans naturally follow their own file; semantic candidate status does not.
