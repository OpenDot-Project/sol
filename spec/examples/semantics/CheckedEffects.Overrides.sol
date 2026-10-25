// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative override-effect fixture. Throws lists are contracts, never overload discriminators.
module spec.effects.overrides

open class ReadFailure: Exception {}
class FileFailure: ReadFailure {}
class WriteFailure: Exception {}
class RuntimeFailure: RuntimeException {}

open class Source {
    open func read(): int throws ReadFailure { 1 }
    open func reset(): () {}
}

class CachedSource: Source {
    override func read(): int { 2 }
}

class FileSource: Source {
    override func read(): int throws FileFailure { 3 }
}

class InvalidSource: Source {
    // Expected: unrelated WriteFailure widens the inherited checked effect set.
    override func read(): int throws WriteFailure { 4 }
}

class UncheckedSource: Source {
    override func read(): int throws RuntimeFailure { 5 }
}

class InvalidReset: Source {
    // Expected: reset had no checked budget to widen.
    override func reset(): () throws ReadFailure {}
}

func useViaBase(source: Source): int throws ReadFailure {
    // The caller honors the base static contract despite a narrower runtime implementation.
    source.read()
}

func subtypeBudgetIsAllowed(): int throws FileFailure {
    FileSource().read()
}

func effectListDoesNotAddOverload(): int throws ReadFailure {
    read(1)
}

func read(value: int): int throws ReadFailure { value }
// Expected declaration conflict: the throws list alone does not form a new overload.
func read(value: int): int throws FileFailure { value }

interface AsyncReader {
    func readAsync(): int throws ReadFailure
}

class LocalAsyncReader: AsyncReader {
    override func readAsync(): int { 0 }
}

class InvalidAsyncReader: AsyncReader {
    override func readAsync(): int throws WriteFailure { 0 }
}

func genericRethrow<E: Throwable>(error: E): ! throws E {
    throw error
}

func instantiateGenericRethrow(error: FileFailure): ! throws FileFailure {
    genericRethrow(error)
}

func genericThrowsMustBeThrowable<E>(error: E): ! throws E {
    // Expected constraint error: E is not known to be Throwable.
    throw error
}

func callbackContractUsesOverrideBudget(source: Source): () {
    val action: () -> int throws ReadFailure = { source.read() }
}

// Override compatibility is checked against every applicable inherited declaration. Interface
// diamonds with conflicting effect origins require explicit conflict resolution; declaration
// order is not a valid policy. The base contract determines effect checking at a virtual call.
