// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: syntax fixture; complete expected-result inference is under development.
module examples.semantics.expected_result

class Envelope<T>(val value: T)

func <T> identity(value: T): T = value

func <T> empty(): T? = null

func <T> envelope(value: T): Envelope<T> = Envelope(value)

interface Payload<T> { func value(): T }

open class BasePayload<T>(val stored: T): Payload<T> {
    override func value(): T = stored
}

class TextPayload(value: String): BasePayload<String>(value) { }

func <T> payloadOf(value: T): Payload<T> = BasePayload(value)

func useExpectedTypes(): () {
    val id: String = identity("Ada")
    val optional: String? = empty()
    val boxed: Envelope<String> = envelope("text")
    val inherited: Payload<String> = payloadOf("payload")
    val projected: Payload<String> = TextPayload("derived")
}

func noResultContext(): () {
    val unresolved = empty()
}

func expectedFromArgument(): String {
    val text: String = identity("result")
    return text
}

func nestedContext(): String {
    return identity(identity("nested"))
}

// `identity("Ada")` gets String evidence from the argument and expected result. `empty()` gets no
// input evidence, so it needs the surrounding String? type to determine its T. `envelope` checks
// invariant nested arguments after inference. `payloadOf` demonstrates a generic return contract;
// the expected type must be applied at this call expression and cannot be mined from a later use.
//
// The unresolved declaration is intentionally a negative semantic fixture. It has valid syntax but
// leaves T without evidence; a compiler must report/defer rather than choose Object or the first
// type found elsewhere in the file.
