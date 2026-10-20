// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: source convention examples; naming lint is a separate tool capability.
module examples.naming.conventions

const val MAX_BUFFER_SIZE: int = 4096

interface ReadableBuffer { func readCount(): int }
enum ConnectionState { Ready, Running, Stopped }

class HttpClient(val userId: ulong) {
    private var _requestCount: int = 0
    val requestCount: int { get = this._requestCount }
    func recordRequest(): () { this._requestCount += 1 }
}

func <Element> identity(value: Element): Element = value

// Types/annotations/objects use UpperCamelCase. Functions, properties, parameters and
// ordinary locals use lowerCamelCase. Immutable compile-time constants use UPPER_SNAKE_CASE.
// A val reference is not automatically a constant. Private backing names may use _prefix.
// Type parameters use T/E/K/V/R or a descriptive UpperCamelCase role.
// Module segments use lower case. Paths and case-insensitive Windows collisions are
// checked separately from case-sensitive language symbol identity.
// Compiler intrinsics use snake_case: size_of, align_of, offset_of, type_of, debug_assert.
// Primitive helpers such as to_string/try_parse/min_value keep their intrinsic spelling.
// Ordinary std APIs remain lowerCamelCase: String.byteLength, Object.hashCode, Vector.removeAt.
// Style does not rename symbols or change semantic identity; invalid declarations are
// compiler errors, while naming convention findings belong to Sol-Tidy.
