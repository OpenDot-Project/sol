// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative literal/context evidence; binary128 rounding and all contextual AST forms remain pending.
module source.spec.generic_inference.literals

// Literal, array, and lambda evidence
// ==================================
//
// Generic inference receives typed evidence only after parsing and expression classification have
// preserved the source form. A literal, lambda, array expression, and already-typed value have
// different evidence rules. Converting all of them to a guessed ResolvedType too early loses
// information and can choose an invalid overload.

// Integer and floating literals
// ------------------------------
//
// Integer and floating source literals retain their exact token text and sign context. An
// uncontextualized integer's default type is int; an uncontextualized floating literal's default is
// double. Generic inference may use this default identity as one input bound. If another argument
// contributes a conflicting type, the ordinary common-type rule applies; the compiler does not
// widen a typed variable because a literal happened to be present.
//
//     func <T> echo(value: T): T = value
//
//     val number = echo(12)
//
// T is inferred as int. The exact value 12 remains available to check any contextual conversion.
//
//     func <T> select(value: T): T = value
//
//     val large: ulong = select(18446744073709551615)
//
// If a local expected ulong supplies a candidate bound, the literal must be representable as
// ulong. The compiler cannot first overflow an int temporary and then widen the truncated result.
//
// Negative signed minima include the sign token/expression in representability. A magnitude that
// cannot fit a positive signed literal may fit after unary minus only when the exact target domain
// permits it. Parsing and generic inference retain the raw magnitude until the target is known.
//
// Binary128 `quad` contextual rounding has an explicit proof obligation. If the frontend cannot
// round the exact source decimal according to the language rule, it defers the candidate instead of
// treating an approximate `double` value as the original literal.

// Nullable literal contexts
// -------------------------
//
// A null literal has the nullable form of the bottom type `!` in the internal normalized model but
// is written `null` in source. It supplies no concrete non-null generic argument on its own. An expected
// `T?` context may provide an upper bound; a lower-bound solver must not choose Object simply
// because Object is available in `std.core`.
//
// `T` matched with an already-typed nullable value retains the complete nullable identity. `T?`
// matched with a nullable value structurally compares one payload layer. Nullable normalization
// prevents nested `T??` wrappers.

// Array literals
// --------------
//
// An array literal's elements are checked under one selected element type. Evidence may come from:
//
// - an explicit expected `[]T` or `[N]T` target;
// - a unique common type among non-empty, independently typed elements;
// - a generic parameter inferred from other arguments or expected result;
// - explicit generic type arguments.
//
// The compiler must not infer one array element type per position. The array's generic element
// type is invariant. A nested array remains nested; `[][]T` evidence cannot be flattened to `[]T`.
//
//     func <T> first(values: []T): T = values[0]
//
//     val text = first(["a", "b"])
//
// Each literal contributes String to T and every element is checked. If a later element does not
// convert to the selected String, the candidate is rejected.
//
//     val empty: []String = first([])
//
// An empty literal has no element evidence. If the expected result fixes T as String, the formal
// parameter supplies the element target. Without that context or explicit type arguments, T is
// missing.

// Fixed arrays and tuples
// -----------------------
//
// Fixed-array length is part of the constructor identity and must match before nested evidence is
// collected. An argument `[4]T` does not provide evidence for `[8]T`. Tuple arity and element order
// also match structurally. A tuple `(T, U)` matched with `(String, int)` supplies ordered evidence
// for T and U. It does not create a nominal common supertype for individual elements.
//
// Ref-like element restrictions apply after inference. A generic argument may infer a ref struct
// only when `allows ref struct` permits it. A managed array still rejects a ref-like element even
// when T itself is otherwise allowed at the call site.

// Lambdas
// -------
//
// Lambda source forms are preserved until a candidate supplies a function target. Named parameters
// may declare their own types. Positional parameters `{ $0 + $1 }` depend on an expected arity and
// typed parameter sequence. An untyped headerless lambda cannot be assigned an implicit `it` type.
//
//     func <T, R> apply(value: T, transform: (T) -> R): R = transform(value)
//
//     val length = apply("item", { text: String -> text.byteLength })
//
// The first argument contributes T = String. The explicit lambda parameter is checked against
// that type; if its body result is known as int, R receives result evidence. For `{ $0.byteLength }`,
// the candidate first needs T from the ordinary argument, then supplies String as the positional
// parameter's expected type.
//
// With overloaded callback targets, each candidate gets an independent expected function type and
// independent body analysis. Candidate failure cannot leave a stale lambda binding in the shared
// expression table. If the analysis API cannot store per-candidate lambda facts, the candidate
// must remain deferred until the representation supports that isolation.

// Function parameter modes and effects
// -------------------------------------
//
// Function value parameter modes are part of function shape. A `(ref T) -> R` callback is not a
// `(T) -> R` callback. Mode mismatch is not repaired by a wrapper closure unless the language
// explicitly inserts and proves that wrapper. Ref/out lifetime and storage checks happen after
// type inference and remain exact.
//
// A function type's checked effect set is unordered. If `E` occurs only in a set such as
// `() -> R throws E`, the solver cannot pair it by sequence index. It either applies a set-aware
// constraint algorithm with one unique mapping or reports/defer missing evidence. Function result
// inference never hides checked effects.

// Numeric variables versus numeric literals
// ------------------------------------------
//
//     func <T> add(value: T): T = value
//
//     val literalResult: int = add(1)
//     val variable: byte = 1 as byte
//     val variableResult = add(variable)
//
// The literal defaults to int unless expected context selects another representable target. The
// variable contributes byte because its type is already fixed. A later conversion ranking cannot
// reinterpret `variable` as int or select a numeric common type from storage widths.
//
// If multiple overloads accept a literal, exact default-type match is preferred before another
// representable contextual target. A literal that fits several unrelated numeric types with no
// unique preference leaves an overload ambiguity and requires an explicit cast/type argument.

// Negative contextual evidence
// ----------------------------
//
// The following are not valid inference shortcuts:
//
// - choosing Object for an empty array or empty vararg;
// - inferring from an untyped lambda before supplying a candidate target;
// - reading a default expression to bind a missing parameter;
// - assigning a null literal to a generic non-null T;
// - pairing function effects by sorted name or declaration order;
// - widening a typed numeric local because a different candidate accepts a wider type;
// - inferring T from a parameter declaration name or a future use of the result;
// - rounding quad through double when exact binary128 conversion is unavailable.
//
// A negative input can reject one candidate while another remains applicable. It does not
// invalidate unrelated candidates, but an unsupported expression whose type could change the
// candidate set must keep the final result Deferred.
