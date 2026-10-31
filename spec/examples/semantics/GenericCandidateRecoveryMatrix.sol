// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax/semantic contract fixture; full candidate outcome inspection is pending.
module examples.semantics.generic_candidate_recovery_matrix

interface Readable<T> { func read(): T }
class TextReader: Readable<String> { override func read(): String = "text" }

func <T> choose(value: T): String = "generic"
func choose(value: String): String = "string"
func choose(value: std.core.Object): String = "object"

func choosesExactOverload(): String = choose("value")

func <T> merge(left: T, right: T): T = left

func missingEvidence(): () {
    // Expected candidate status: Missing/Deferred, not T = Object.
    val invalid = identityWithoutInputs<String>()
}

func <T> identityWithoutInputs(): T

func conflictingExactBounds(): () {
    var left: String = "left"
    var right: int = 1
    // Expected candidate rejection: ref/out exact identities conflict.
    exchange(ref left, out right)
}

func <T> exchange(ref left: T, out right: T): () { right = left }

func invariantConflict(): () {
    val text: Box<String> = Box("text")
    val number: Box<int> = Box(4)
    // Expected candidate rejection: Box<T> is invariant.
    val invalid = merge(text, number)
}

class Box<T>(val value: T)

func incompleteCandidateDoesNotWin(): () {
    funcHolder("value")
}

func funcHolder(value: String): String = "known"

func <T> funcHolder(value: T): String = "generic"

func projectionEvidence(): String {
    return readAs(TextReader())
}

func <T> readAs(value: Readable<T>): T = value.read()

func expectedResultEvidence(): String {
    val result: String = create()
    return result
}

func <T> create(): T

func noCandidateOrderTieBreak(): () {
    // Expected call ambiguity: independent equally ranked candidates remain an ambiguity.
    val invalid = choose(default<TextReader>())
}

func arrayCandidateFallback(): String {
    return first(["a", "b"])
}

func <T> first(values: []T): T = values[0]

func candidateBoundary(): () {
    // ResourceLimit/deferred candidates cannot be treated as rejected and cannot silently disappear.
    val value = choose(TextReader())
}

// Candidate status sequence:
//
// - map arguments and receiver before solving;
// - retain exact/lower/expected evidence and source origins;
// - reject proven invariant or ref/out conflicts;
// - keep unsupported nested body analysis deferred if it could change ranking;
// - rank complete applicable candidates by conversion vectors;
// - publish no winner for an unresolved tie or incomplete candidate set;
// - reanalyze the selected expression once under its closed signature.
//
// The file marks syntax and expected outcomes only. It does not supply executable methods for
// unresolved calls or assert that every negative case currently emits the stated diagnostic.
