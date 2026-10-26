// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative array/vararg inference; all body-flow and storage forms remain in qualification.
module source.spec.generic_inference.array_varargs

// Array literals, varargs, named arrays, and spread
// =================================================
//
// Vararg mapping decides whether an argument denotes one element or one complete array. Generic
// inference observes the mapping result; it cannot infer the mapping from an array-shaped type or
// silently change the source syntax's meaning.

// Ordinary vararg element
// -----------------------
//
// For `func <T> collect(vararg items: T): []T`, every ordinary mapped argument contributes one
// element fact against T. A literal value contributes its source default/representable type; an
// already-typed local contributes its declared/effective type. The result is one specialized `[]T`.
//
// A vararg may be in the middle of the parameter list. The call mapper uses declaration positions,
// names, and the final function-valued parameter/trailing lambda rule. An argument that maps to a
// later ordinary parameter is not evidence for the vararg. A malformed mapping rejects that
// candidate before generic evidence is collected.
//
// One array-valued expression passed as an ordinary vararg element can infer T as that array type
// when the expression has a unique standalone type. The element value is then an array; no spread
// occurs unless `*` is written.

// Spread
// ------
//
// The `*array` marker means that the array's elements fill the vararg sequence. Its expression
// must have managed-array type. A typed `[]Derived` source contributes its static element type
// under the vararg inference relation; runtime contents are not enumerated.
//
// A spread of `[][N]T` requires an explicitly supported conversion to the vararg array model. A
// fixed array cannot be reinterpreted as a managed array by name alone. Span, ReadOnlySpan, tuple,
// string, and collection values do not become spread arrays unless the language defines a separate
// conversion.
//
// One call may include several ordinary elements and spreads. All source arguments keep their
// evaluation order. Each spread expression is evaluated once. Length is read/iterated only at the
// selected runtime call site, never during inference.

// Named vararg array
// ------------------
//
// A named vararg argument supplies one complete array. Its shape must match the vararg's managed
// array form, and its element identity is invariant. A `[]Object` cannot satisfy `vararg String`
// because source elements happen to be strings at runtime.
//
// A non-empty array literal in named-array position first establishes one unique common element
// type and then matches that constructed array to the vararg formal. Its common type is exact at
// the array boundary; it is not treated as an arbitrary number of separately mapped vararg
// arguments.
//
// An empty array literal has no standalone element type. It can serve a named array only when the
// element type is fixed by another inference source, explicit generic argument, or selected
// expected array context. The compiler may then validate that zero elements satisfy the target.

// Empty vararg
// ------------
//
// An omitted vararg is the empty sequence for call mapping but does not create an array source
// expression. It contributes no generic evidence. Its runtime representation follows the selected
// ABI; inference never allocates it.
//
// `collect()` with an unresolved T remains missing evidence. `collect<String>()` is closed by the
// explicit type argument. An expected return `[]String` may supply an upper bound if the result
// shape contains T. A default argument elsewhere cannot infer T by executing its expression.

// Default and named argument interaction
// --------------------------------------
//
// Named arguments map before inference, but their evaluation remains source-ordered. A named array
// can follow a named ordinary argument or precede one while preserving each formal identity.
// Duplicate assignment to one formal is a mapping error. A positional argument after a named
// reorder is accepted only if it continues the declaration's remaining ordered sequence under the
// call contract.
//
// Omitted defaults are checked after selection under the closed substitution. A default of type
// `[]T` is not evaluated to discover T. If a generic parameter appears only in an omitted default,
// the call remains missing evidence.

// Trailing lambda after vararg
// ----------------------------
//
// A trailing lambda maps to the final eligible function parameter, even when a vararg appears
// earlier. The lambda supplies callback evidence only for that mapped function parameter. The
// vararg does not consume the lambda unless its declared element itself is a function and the call
// syntax explicitly maps it as an ordinary vararg value.
//
// An outside-brace lambda is still one source argument. It does not count as a spread array, does
// not bypass named/default mapping, and does not move before explicitly written values for runtime
// evaluation.

// Inference relation table
// ------------------------
//
// | Call form | Formal vararg | Evidence | Must be exact? |
// |-----------|---------------|----------|----------------|
// | `collect("a")` | element T | String input | no, ordinary value relation |
// | `collect(*source)` | element T | source array element | according to spread relation |
// | `collect(values = source)` | element T | source array element identity | yes, array invariant |
// | `collect(values = ["a"])` | element T | unique array literal common type | exact after common type |
// | `collect()` | element T | none | n/a |
// | `collect(values = [])` | element T | none until context | n/a |
// | `collect([1])` | element T | whole array as one element | ordinary element relation |
// | `collect(*span)` | element T | invalid unless explicit conversion exists | n/a |
//
// The table assumes the declaration's mapper recognizes the shown argument form. It does not
// replace the canonical named/default/vararg rules in the function-call specification.

// Candidate isolation
// -------------------
//
// Each overload receives an independent mapping. Candidate A cannot consume a spread element and
// leave fewer elements for candidate B. It cannot infer T from a candidate-specific empty-array
// target and make that target global. It cannot evaluate the spread expression to inspect length.
//
// When one candidate requires an exact array identity and another accepts ordinary per-element
// values, each receives its own candidate relation and conversion rank. An unresolved candidate
// that could outrank the known result remains a blocker.

// Error ownership
// ---------------
//
// - non-array spread: diagnose the source spread expression;
// - wrong named-array element identity: diagnose the named argument/formal pair;
// - duplicate named/positional fill: diagnose the duplicate source argument;
// - generic element has no evidence: diagnose the call and identify missing parameter;
// - conflicting vararg element bounds: identify contributing element indexes;
// - empty array without target: identify the literal and expected element type requirement;
// - unsupported array-to-span conversion: diagnose conversion, not a missing member;
// - cancellation/resource exhaustion: propagate/report resource state, never source inapplicability.

// Runtime evaluation after selection
// ----------------------------------
//
// The selected call evaluates receiver, ordinary arguments, array literal elements, and defaults
// according to source order and parameter-default policy. Spread consumes its selected array once.
// A named-array literal constructs one array using the final specialized element type. No candidate
// exploration allocates a runtime array.
//
// Any element exception exits the call before invocation and follows the checked-effect/cleanup
// rules of the enclosing body. Evaluation is not restarted for a different overload after a
// selected argument throws. The array's GC roots, write barriers, and layout belong to verified FIR
// and Yellow Tea, not to the generic inference solver.

// Required regression matrix
// --------------------------
//
// - middle vararg and following named/positional formals;
// - one ordinary array-valued element versus one spread;
// - named typed array, named literal array, empty named literal;
// - multiple spread and ordinary elements in source order;
// - spread nullable container, nullable elements, fixed array, span, and string;
// - explicit and inferred T; expected result and default-only T;
// - generic overload plus non-generic overload; exact versus element conversion;
// - one trailing lambda after vararg; malformed lambda mapping;
// - out/ref adjacent to array argument; ref-like element rejection;
// - element exception, call cancellation, and candidate resource limit.

// Current implementation boundary
// -------------------------------
//
// BodyAnalysis distinguishes ordinary, spread, and named array mapping for supported generic call
// forms and can gather bounded literal evidence. Complete runtime vararg packing/spread, every
// named/default interaction, exact lifetime proof, and FIR/native array layout remain unverified.
