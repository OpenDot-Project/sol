// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative lambda inference contract; candidate-local contextual body checking is incomplete.
module source.spec.generic_inference.lambdas

// Lambda-driven generic inference
// ===============================
//
// Lambda syntax is parsed before a target function type is known. A lambda's parameter type and
// result type can therefore be evidence only after the candidate has established a valid expected
// function shape or the source explicitly wrote enough types to stand alone.

// Named-parameter lambda
// ----------------------
// A lambda with explicit parameter annotations supplies those written types for shape comparison.
// The candidate still checks count, modes, parameter compatibility, result conversion, and checked
// effects. Explicit types do not authorize a different overload parameter type.
//
//     func <T, R> map(value: T, transform: (T) -> R): R = transform(value)
//
//     val length = map("text", { text: String -> text.byteLength })
//
// The value argument supplies T = String. The lambda parameter confirms that shape. If the body
// expression is typed as int, it can supply result evidence for R. If the candidate expected a
// different parameter type, the lambda is rejected for that candidate only.

// Positional lambda
// -----------------
// `{ $0 + $1 }` has positional parameter references, not implicit local declarations. Arity and
// each parameter type come from the expected function type. The compiler cannot choose a candidate
// by assigning `$0` the type of the first candidate and then reusing it.
//
// If the generic type parameter appears in an earlier ordinary call argument, solve it first and
// then form the expected lambda parameter types. If a generic parameter appears only in a positional
// lambda parameter, no lower-bound evidence exists until another source fixes the candidate. The
// candidate remains unresolved unless explicit type arguments or expected result constraints solve
// it.

// Headerless lambda
// -----------------
// A headerless parameterless lambda can infer its body result when the body is independently typed.
// A headerless lambda with implicit argument names is invalid; Sol has no implicit `it`. A
// headerless zero-parameter lambda can still need a target function result/effect contract.

// Trailing lambda
// ---------------
// The outside-brace trailing lambda maps to the final eligible function parameter under the
// named/default/vararg call rules. It contributes evidence at that formal index. It does not move
// ahead of explicit source arguments for evaluation. Only one trailing lambda may be used and it
// must match the final function-valued parameter selected by the mapper.

// Candidate isolation
// -------------------
// Consider two overloads with distinct expected callback input types. For each candidate, the
// compiler needs an isolated lambda analysis context containing:
//
// - expected parameter type and passing mode;
// - candidate-local generic substitution;
// - candidate-local expression types and return inference;
// - candidate-local checked effect budget;
// - candidate-local definite assignment and captured-local state;
// - candidate-local diagnostics and source origins.
//
// Rejecting a candidate discards that candidate's contextual bindings. The next candidate begins
// with the original source lambda and its own context. Caching by the lambda node alone is invalid
// because one AST expression can have different expected types during overload search.

// Function variance and inference
// -------------------------------
// Function types are contravariant in value parameters and covariant in their result. Ref/out
// parameter modes must match and their storage types are invariant. Checked effects use the
// separate function-effect subset relation. Nominal generic applications remain invariant.
//
// When an actual function value is matched against a generic formal function parameter, inference
// reverses the evidence direction for each value parameter and keeps the direction for the result.
// When a generic function result is matched against an expected function type, parameter evidence
// likewise reverses while result evidence keeps the expected-result direction. Each nested
// function-valued parameter reverses direction again. A ref/out slot contributes exact identity
// evidence instead of directional evidence.
//
// If T occurs in both a callback parameter and result, the solver retains both bounds and chooses T
// only when one source type satisfies them and the final function conversion succeeds. This rejects
// a callback whose parameter is too narrow or whose result is too broad.

// Ref/out callback parameters
// ---------------------------
// A lambda parameter marked ref/out retains the exact mode in its function type. A callback with
// value parameter cannot satisfy a ref callback. A captured ref/out variable cannot escape the
// call-scoped location. These lifetime rules are checked after candidate selection and are not
// solved by substituting an Object type.

// Effects
// -------
// A lambda body can throw checked exceptions. Its contextual function type provides an effect
// budget; the enclosing callable must handle or declare the escaping effect. Effects form a set.
// Bounded matching can infer multiple top-level variables only from one complete assignment;
// parameter enumeration order cannot map two throw lists. Nested function-type effect matching
// remains deferred.

// Incomplete support
// ------------------
// If a source analysis cannot isolate candidate-local lambda facts, it returns a Deferred candidate
// with the lambda source location. It must not cache a guessed expression type, discard the
// candidate, or claim a unique overload. Explicitly typed function values remain ordinary typed
// arguments and can contribute shape evidence through their complete function type.
