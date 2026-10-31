// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative context/evidence rules; full contextual lambda and effect inference is pending.
module source.spec.generic_inference.context

// Contextual evidence and call mapping
// ====================================
//
// This companion contract covers cases in which the source argument form affects what evidence is
// available. Mapping is determined before inference. Evaluation order is determined after candidate
// selection. These phases are separate even though they inspect the same call expression.

// Positional and named arguments
// ------------------------------
//
//     func <K, V> pair(key: K, value: V): Pair<K, V> = Pair(key, value)
//
//     val left = pair("id", 7)
//     val right = pair(value = 7, key = "id")
//
// Each source argument maps to its declared formal before its evidence is collected. Named order
// does not change formal order, generic parameter identity, or later default evaluation. Source
// arguments still evaluate left to right as written. The named call infers K = String and V = int.

//     func <T> reorder(first: T, second: T): T = first
//
//     val selected = reorder(second = "second", first = "first")
//
// Both arguments provide the same lower-bound type. If one uses an unrelated concrete type, the
// bounds are joined using the unique most-specific candidate rule, not whichever parameter is
// declared first.

// Middle varargs
// --------------
//
//     func <T> surround(prefix: String, vararg values: T, suffix: String): []T {
//         return values
//     }
//
//     val names = surround("[", "red", "green", "]")
//
// The mapper assigns the middle arguments to the vararg element type T and the last positional
// argument to suffix because the declaration leaves that position available. Each middle value
// contributes a lower bound. The result is `[]String` when the bounds resolve to String.
//
//     val empty = surround("[", suffix = "]")
//
// The empty vararg provides no evidence for T. The call needs an expected `[]T` result that can
// uniquely solve T or an explicit type argument permitted by the grammar. It cannot infer `T =
// Object` because the sequence is empty.

// Spread and named vararg arrays
// ------------------------------
//
//     func <T> copy(vararg values: T): []T = values
//
//     val source: []String = ["a", "b"]
//     val fromSpread = copy(*source)
//     val fromNamedArray = copy(values = source)
//
// A spread verifies that its source is an array, then contributes the element type. A named
// vararg array is already an array argument and requires the invariant array element identity.
// Passing `[]Object` does not infer `T = String` merely because every runtime value currently
// happens to contain text.

// Defaults
// --------
//
//     func <T> withDefault(value: T, fallback: T = value): T = fallback
//
//     val explicit = withDefault("chosen")
//
// The explicit argument infers T as String. The default expression is then checked and evaluated
// under that closed substitution. The default is not evaluated to discover T before selection.
//
//     func <T> defaultOnly(value: T = default<T>()): T = value
//
//     val notInferred = defaultOnly()
//
// A default-only call has no inference evidence in this contract. It requires explicit type
// arguments or a unique expected-result type. A candidate-specific default body cannot be executed
// during overload search.

// Trailing lambdas
// ----------------
//
//     func <T, R> transform(value: T, block: (T) -> R): R = block(value)
//
//     val length = transform("text") { value: String -> value.byteLength }
//
// The explicit lambda parameter type supplies an exact shape constraint after the call mapper
// identifies the final function parameter. Its body result may contribute R after the lambda is
// checked under a candidate-local expected type. The implementation must not reuse the lambda's
// type or diagnostics from a rejected candidate.
//
//     val inferred = transform("text") { $0.byteLength }
//
// The positional parameter has no source type outside an expected function shape. T can be
// inferred from the first argument, then the candidate can contextually type `$0`; R can be
// inferred from the checked lambda body if that operation is available. If no unique candidate
// supplies a complete context, the call remains unresolved. No implicit `it` is introduced.

// Explicit function type
// ----------------------
//
//     func <T> apply(value: T, operation: (T) -> int): int = operation(value)
//
//     val result = apply("abc", { text: String -> text.byteLength })
//
// The function parameter mode is value and the function shape is exact in parameter count and
// passing mode. A `{ ref text: String -> ... }` lambda cannot satisfy `(String) -> int`. The
// function result and declared effect set are checked after contextual lambda analysis.

// Array literals
// --------------
//
//     func <T> values(items: []T): []T = items
//
//     val names = values(["one", "two"])
//
// The expected array parameter supplies the element pattern. Each literal element contributes
// evidence and is checked against the one solved T. Conflicting element types may infer a unique
// nominal common type only when ordinary conversions and invariance permit it.
//
//     val noElements = values([])
//
// An empty literal provides no element evidence. With an expected `[]String` outside the call, T
// may be String. Without expected context or explicit type arguments, the call remains unresolved.

// Ref/out modes
// -------------
//
//     func <T> exchange(ref left: T, out right: T): () {
//         right = left
//     }
//
//     var original: String = "before"
//     exchange(ref original, out var result)
//
// `original` supplies exact evidence for T. `result` is not a type argument until the candidate is
// unique. It becomes String and is assigned on the normal call continuation. A conversion from
// `String` storage to `std.core.Object` storage is not allowed to satisfy `ref` or `out`.

// Checked effects
// ---------------
//
//     class ReadFailure: Exception { }
//
//     func <T> recover(value: T, loader: () -> T throws ReadFailure): T throws ReadFailure {
//         return loader()
//     }
//
// The return parameter and callback result refer to the same T identity. The callback's effect
// contract is checked against the function type and the enclosing `throws` contract. Effect types
// are an unordered set. If type parameters occur only in a top-level callback effect set, bounded
// matching can infer them only from one complete assignment after structural matching and owner
// constraints. Nested sets and multiple valid assignments remain deferred; source ordering of
// `throws` names is never evidence.

// Candidate-local contextual typing
// ---------------------------------
//
// Suppose overloads differ only in callback parameter type:
//
//     interface TextInput { func text(): String }
//     interface NumericInput { func number(): int }
//
//     func convert(block: (TextInput) -> String): String
//     func convert(block: (NumericInput) -> String): String
//
//     val value = convert { input -> input.toString() }
//
// If the lambda parameter is untyped and both candidates remain applicable, the compiler analyzes
// each lambda against an isolated expected type. It then compares complete candidate results. It
// cannot first pick the first overload, assign `input` a permanent type, and treat that as source
// evidence against the second overload.

// Nested calls and expected result propagation
// --------------------------------------------
//
//     func <T> box(value: T): Box<T> = Box(value)
//     func <T> unbox(value: Box<T>): T = value.value
//
//     val text: String = unbox(box("nested"))
//
// The inner call receives an expected parameter type from the candidate selected for the outer
// call, and the outer call receives the expected String result. Inference proceeds only when those
// contexts produce a finite, uniquely bound dependency chain. Recursive/cyclic call inference is
// bounded and requires written annotations where the cycle has no seed.
//
// A future call graph that searches arbitrary call sites for an expected type is not allowed. Each
// expected type must flow from the immediate expression context through a semantically selected
// parent candidate.

// Error and recovery observability
// --------------------------------
//
// Candidate records retain:
//
// - original type parameter owner and ordinal;
// - source argument and formal parameter indexes;
// - lower, exact, and expected evidence origins;
// - constructed base projections that provided evidence;
// - each constraint result and its reason;
// - final specialized parameter, result, and effect types;
// - mapping, conversion, or inference rejection/defer status.
//
// Public diagnostics may summarize these facts, but a diagnostic limit must not change which
// candidate would otherwise win. A candidate is not successful merely because its partial
// substitution can be printed. A successful candidate contains every required parameter and
// passes nested nominal and ref-like constraints.
