// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative future contract: structural iteration is resolved as one exact method pattern.
module spec.arrayiteration.structuralpattern

class Failure: Exception {}
class CloseFailure: Exception {}

class IndexCursor {
    var index: int
    val values: []int
    func moveNext(): bool throws Failure = index < values.length
    func current(): int throws Failure = values[index]
}

class IndexSource {
    val values: []int
    func iterator(): IndexCursor = IndexCursor(0, values)
}

func exactStructuralPattern(source: IndexSource): int throws Failure {
    var total = 0
    for val item in source { total += item }
    total
}

class WrongReturnCursor {
    func moveNext(): int = 1
    func current(): int = 0
}

class WrongReturnSource {
    func iterator(): WrongReturnCursor = WrongReturnCursor()
}

func wrongAdvanceRejected(source: WrongReturnSource): () {
    for val item in source { consume(item) }
}

class MissingValueCursor {
    func moveNext(): bool = false
}

class MissingValueSource {
    func iterator(): MissingValueCursor = MissingValueCursor()
}

func missingCurrentRejected(source: MissingValueSource): () {
    for val item in source { consume(item) }
}

class ThrowingCloseCursor {
    func moveNext(): bool = false
    func current(): int = 0
    func close(): () throws CloseFailure {}
}

class ThrowingCloseSource {
    func iterator(): ThrowingCloseCursor = ThrowingCloseCursor()
}

func closeIsOptionalButItsEffectsAreNot(source: ThrowingCloseSource): ()
    throws CloseFailure {
    for val item in source { consume(item) }
}

func closeBudgetMissing(source: ThrowingCloseSource): () {
    for val item in source { consume(item) }
}

class NoCloseCursor {
    func moveNext(): bool = false
    func current(): int = 0
}

class NoCloseSource {
    func iterator(): NoCloseCursor = NoCloseCursor()
}

func noCloseMemberMeansNoCleanupEdge(source: NoCloseSource): () {
    for val item in source { consume(item) }
}

class BadCloseCursor {
    func moveNext(): bool = false
    func current(): int = 0
    func close(): int = 1
}

class BadCloseSource {
    func iterator(): BadCloseCursor = BadCloseCursor()
}

func badCloseSignatureRejected(source: BadCloseSource): () {
    // Expected protocol error: optional close must return the empty tuple type.
    for val item in source { consume(item) }
}

class CloseOverloadCursor {
    func moveNext(): bool = false
    func current(): int = 0
    func close(mode: int): () = ()
}

func parameterizedCloseIsNotCleanup(source: CloseOverloadCursor): () {
    // There is no applicable zero-argument close operation.
    for val item in source { consume(item) }
}

func closeResolvedOnStaticCursorType(source: StaticCursorSource): () {
    for val item in source { consume(item) }
}

func methodPatternDoesNotSearchRuntimeSubclasses(source: StaticBaseSource): () {
    for val item in source { consume(item) }
}

func methodPatternStoresExactTargetIdentity(source: IndexSource): () throws Failure {
    for val item in source { consume(item) }
}

func methodPatternEffectsAreNotMethodNameEffects(source: IndexSource): () throws Failure {
    for val item in source { consume(item) }
}

func propertyCurrentIsNotCallablePattern(source: PropertyCurrentSource): () {
    for val item in source { consume(item) }
}

func extensionFunctionCannotSatisfyMissingInstanceMethod(source: MissingInstanceMethod): () {
    for val item in source { consume(item) }
}

// Structural cursor discovery uses ordinary receiver binding and exact signatures. The compiler
// does not synthesize members, retry through runtime reflection, or treat any same-named property as
// an operation. A resource close method creates explicit cleanup and effect obligations.
