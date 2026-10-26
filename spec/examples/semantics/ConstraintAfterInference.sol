// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; generic constraint and body safety validation are incomplete.
module examples.semantics.constraint_after_inference

interface Encodable { func encode(): String }

class TextValue(val text: String): Encodable {
    override func encode(): String = text
}

class PlainValue(val text: String) { }

func <T: Encodable> encode(value: T): String = value.encode()

func validCandidate(): String {
    return encode(TextValue("ok"))
}

func rejectedCandidate(): String {
    // Expected diagnostic: T is inferred as PlainValue, then the Encodable bound fails.
    return encode(PlainValue("no"))
}

func explicitBound(): String {
    return encode<TextValue>(TextValue("explicit"))
}

func wrongExplicitBound(): String {
    // Expected diagnostic: explicit type arguments still must satisfy constraints.
    return encode<PlainValue>(PlainValue("invalid"))
}

class Container<T: Encodable>(val value: T)

func inferredOwner(): Container<TextValue> {
    return Container(TextValue("nested"))
}

func invalidOwner(): () {
    // Expected diagnostic: owner constraint is checked after T is inferred as PlainValue.
    val invalid = Container(PlainValue("nested"))
}

// Inference selects a candidate identity from input/expected bounds before validating constraints.
// A constraint does not replace PlainValue with Encodable and does not turn an invalid argument into
// a valid base value. Explicit types bypass inference only; they never bypass constraints.
