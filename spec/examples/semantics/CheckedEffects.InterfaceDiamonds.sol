// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative interface-effect fixture. Inherited declarations retain their separate origins.
module spec.effects.interfacediamonds

class ReadFailure: Exception {}
class FileFailure: ReadFailure {}
class WriteFailure: Exception {}

interface Reader {
    func read(): int throws ReadFailure
}

interface FileReader {
    func read(): int throws FileFailure
}

interface Writer {
    func write(value: int): () throws WriteFailure
}

class LocalReader: Reader, FileReader {
    override func read(): int { 1 }
}

class NarrowReader: Reader, FileReader {
    override func read(): int throws FileFailure { 2 }
}

class InvalidReader: Reader, Writer {
    override func read(): int throws WriteFailure { 3 }
    override func write(value: int): () { }
}

func callAsReader(reader: Reader): int throws ReadFailure {
    reader.read()
}

func callAsFileReader(reader: FileReader): int throws FileFailure {
    reader.read()
}

func oneImplementationThroughBothViews(value: NarrowReader): int throws ReadFailure {
    val first = (value as Reader).read()
    val second = (value as FileReader).read()
    first + second
}

func unrelatedBudgetsDoNotMergeBySimpleName(value: InvalidReader): int throws ReadFailure {
    // Expected: WriteFailure cannot satisfy Reader's ReadFailure contract.
    value.read()
}

interface LeftDefault {
    func label(): String throws ReadFailure
}

interface RightDefault {
    func label(): String throws WriteFailure
}

class ConflictingDefaults: LeftDefault, RightDefault {
    override func label(): String throws ReadFailure, WriteFailure { "resolved" }
}

func effectDiamondRequiresExplicitResolution(value: ConflictingDefaults): String
    throws ReadFailure, WriteFailure {
    value.label()
}

class ImplicitConflict: LeftDefault, RightDefault {}

func unresolvedDiamondIsNotSourceOrdered(value: ImplicitConflict): String {
    // Expected: conflicting inherited defaults require an explicit override or conflict choice.
    value.label()
}

// Every inherited callable path carries the effects from its own declaring contract. A concrete
// implementation must satisfy all applicable contracts. Compatible subtyping may make one
// implementation valid for multiple views; unrelated budgets or default origins need explicit
// resolution and cannot be selected by declaration order.
