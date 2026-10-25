// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative recursive-bound rules; complete declaration-cycle and FIR checks are pending.
module source.spec.generic_inference.cycles

// Recursive bounds and inference cycles
// =====================================
//
// This contract covers type variables created while binding one candidate. It does not make source
// type parameters recursively infinite. A source type is always finite syntax; recursive
// declarations are finite symbol graphs whose edges can be traversed with cycle detection.
//
// Candidate variables are private to a single overload candidate. No type selected for one
// candidate is evidence for another candidate. Rejected candidates cannot publish substitutions,
// contextual lambda results, or constraint diagnostics into a surviving candidate.

// 1. Occurrences and finite substitutions
// ---------------------------------------
//
// A candidate variable may appear in a direct formal, a constructed type, an array, a tuple, a
// nullable type, a function parameter/result, or an effect set. Solving the variable substitutes a
// finite existing type identity for every occurrence. It must not create a self-referential type
// node such as T = Box<T> as a successful closed substitution.
//
//     class Box<T>(val value: T)
//
//     func <T> wrap(value: T): Box<T> = Box(value)
//
// For wrap("text"), the input fixes T to String and the result is Box<String>. The fact that T
// occurs underneath Box in the return type does not form a cycle: the evidence is a concrete input
// type and substitution terminates.
//
//     func <T> emptyBox(): Box<T> = Box(default<T>())
//
// emptyBox() without an expected result has no evidence for T. It remains missing evidence; it is
// not solved by constructing an unbounded equation T = Box<T>.
//
// A repeated variable occurrence inside one structural pattern must refer to the same candidate
// variable identity. Spelling equality between parameters owned by different declarations does not
// unify those parameters.

// 2. Direct and indirect equations
// --------------------------------
//
// The candidate solver records structural relations before choosing a type. It applies an occurs
// check to candidate-variable equations after owner and explicit substitutions have been applied.
// The check walks arrays, fixed arrays, tuples, nullable wrappers, nominal arguments and callable
// signatures. It is cycle-safe when the input symbol graph itself is recursive.
//
//     func <T> same(left: T, right: T): T = left
//
//     val text = same("a", "b")
//
// Both arguments contribute compatible evidence and produce T = String.
//
//     func <T> incompatible(left: T, right: []T): T = left
//
// Given finite source expressions whose types require T to be both an enclosing array and its own
// element type, the candidate is rejected as a recursive inference equation. No runtime value can
// prove an infinitely nested source type.
//
// Indirect equations use the same rule. If matching a formal produces T = Box<U> and another
// produces U = []T, the solver expands the finite equation graph, finds the cycle, and rejects that
// candidate. It must not recurse until stack exhaustion, substitute an error type, or declare the
// equation solved because one branch happened to be visited first.

// 3. F-bounds are declaration constraints, not equations
// ------------------------------------------------------
//
// A source constraint may refer to its own parameter through a nominal interface. That declaration
// graph is not an inference equation and is legal when its declarations are otherwise well formed.
//
//     interface Comparable<T> {
//         func compareTo(other: T): int
//     }
//
//     func <T: Comparable<T>> maximum(left: T, right: T): T {
//         if (left.compareTo(right) >= 0) return left
//         return right
//     }
//
//     class Version: Comparable<Version> {
//         override func compareTo(other: Version): int = 0
//     }
//
// maximum(Version(), Version()) infers T = Version from its inputs, then validates the finite
// constructed bound Comparable<Version>. The bound is not recursively expanded into an infinite
// sequence Comparable<Comparable<...>>.
//
// If a type parameter has no input evidence and only the constraint T: Comparable<T>, the bound
// does not select a particular T. The candidate has missing evidence unless a local expected result
// or explicit type argument determines it.
//
// Recursive constraints on class parameters, method parameters and enclosing type parameters retain
// distinct owner identities. Reusing a source name in an inner declaration does not tie its
// inference variable to the outer one.

// 4. Graph visitation and cycle keys
// ---------------------------------
//
// Declaration-cycle analysis and constructed-bound projection use different identities:
//
// - declaration validation keys a node by declaration identity and the current type-parameter
//   substitution environment;
// - constructed projection keys a node by the canonical constructed type identity;
// - candidate equations key a variable by candidate identity and parameter ordinal;
// - callable effect matching keys an effect variable by its declaration owner and ordinal.
//
// A name string, source path, visitation order, or numeric type ID from another specialization
// session is not a valid cycle key. Canonical identities must belong to the current analysis.
//
// Visiting a node a second time through the same constructed identity closes that path and is not
// itself an error. Reaching the same generic declaration through a different constructed identity
// preserves the distinct projection. For example, Base<String> and Base<Object> must not be
// collapsed merely because they share the Base declaration.
//
// An invalid source declaration cycle is diagnosed during type/declaration validation. A valid
// recursive interface constraint is retained as a finite edge and checked against the already
// inferred concrete argument. The candidate solver does not silently repair an invalid graph.

// 5. Bound closure order
// ----------------------
//
// For each candidate, the binder applies evidence in a stable conceptual order:
//
// 1. explicit method type arguments and already-constructed receiver arguments become fixed;
// 2. value, ref, out, spread, and named/default mapping determine formal/actual relationships;
// 3. structurally matching inputs contribute lower or exact bounds;
// 4. a local expected result contributes upper bounds through the result pattern;
// 5. constructed bases and declared bounds contribute finite projected evidence;
// 6. lambda, array, literal and effect evidence is collected only when the feature has a defined
//    candidate-local rule;
// 7. equations are checked for cycles and each parameter is solved;
// 8. all nominal constraints and ref-like anti-constraints are validated;
// 9. the fully substituted call is checked for conversions, effects and result compatibility.
//
// This list is a dependency order, not a tie-break rule. Reordering input files, declarations,
// bounds, or equivalent syntax cannot change the selected type.
//
// A failure at one stage retains the candidate's evidence and stage-specific status. An invalid
// projected bound does not become an empty bound list. A resource limit is not a conflict and a
// conflict is not missing evidence.

// 6. Recursive nominal projection
// -------------------------------
//
// Projection walks only declared base and interface edges after substituting the current node's
// arguments. It does not instantiate arbitrary nominal constructors from a bound's spelling.
//
//     interface Source<T> { func read(): T }
//     interface Wrapped<T>: Source<[]T> { }
//     class TextSource: Wrapped<String> {
//         override func read(): []String = ["text"]
//     }
//
//     func <T> consume(source: Source<T>): []T = source.read()
//
// TextSource projects to Wrapped<String>, then Source<[]String>. The solver records T = []String.
// Revisiting Source<[]String> through another route is a duplicate. Reaching Source<String> by a
// separate legal path is a distinct constructed identity and conflicts with this evidence.
//
// A malformed cycle in metadata must terminate with an invalid-metadata or resource result. It may
// not hang declaration lookup, generic inference, member lookup, diagnostics, or serialization.

// 7. Mutual constraints
// ---------------------
//
// A declaration can express constraints that refer to multiple parameters. Each constraint is
// validated against the complete substitution, not against a partially chosen traversal prefix.
//
//     interface Converts<TFrom, TTo> {
//         func convert(value: TFrom): TTo
//     }
//
//     func <A, B> transcode(
//         converter: Converts<A, B>,
//         input: A
//     ): B {
//         return converter.convert(input)
//     }
//
//     class TextToBytes: Converts<String, []byte> {
//         override func convert(value: String): []byte = []
//     }
//
//     val bytes: []byte = transcode(TextToBytes(), "payload")
//
// Both A and B are solved from the constructed interface projection. Their owner identities and
// evidence sources remain separate, even when both parameters have the same source spelling in
// nested declarations.
//
// If constraints form a dependency cycle, validation uses the complete map and a visited set. A
// constraint is not considered satisfied merely because another constraint in the cycle mentions
// the same name.

// 8. Cycles involving function types
// ----------------------------------
//
// Callable types are structural nodes for the occurs check. Parameter modes, parameter order,
// result type and set-valued checked effects are all traversed. No candidate may smuggle an
// unresolved variable into a supposedly closed callable by examining only its result.
//
//     func <T> accept(callback: (T) -> String, seed: T): String {
//         return callback(seed)
//     }
//
// A callback with a known parameter type and a seed of the same type can infer T. A positional
// lambda with no expected type cannot independently prove T. If its candidate context depends on T,
// the binder waits for other evidence or reports missing/deferred evidence; it does not infer T by
// executing the lambda during overload discovery.
//
// A type variable occurring only in a checked effect set also needs the dedicated set-matching
// rule. Positionally pairing effect lists can create a false cycle or a false solution when the
// declaration order differs.

// 9. Error ownership and recovery
// -------------------------------
//
// Recursive-equation diagnostics belong to the candidate call site and point to the smallest
// participating type occurrence. Declaration cycles belong to the declaration that closes the
// invalid graph. Neither diagnostic is rewritten as a generic “type cannot be inferred” if the
// cycle is known.
//
// During error recovery, a candidate with a cycle is inapplicable. Other overload candidates remain
// isolated and can still be selected if exactly one is applicable. If every candidate fails, the
// final diagnostic summarizes the applicable failure class and retains candidate notes in stable
// declaration order.
//
// Error types are not evidence. A prior error must not make an occurs check pass, satisfy a bound,
// or cause a concrete overload to outrank an unresolved generic candidate. The output remains
// incomplete until the diagnostic owner has been resolved.

// 10. Bounded work and cancellation
// --------------------------------
//
// Every equation expansion, projection and recursive constraint validation uses a shared
// cancellation token and a documented finite budget. Reaching a budget returns a resource-limit
// outcome with the operation and source owner recorded. It is never treated as a successful leaf,
// an empty interface set, Object, bottom, or a fallback conversion.
//
// A cancellation request propagates without publishing the candidate's partial substitution.
// Repeated runs with the same source snapshot and budget return the same diagnostic classification.
// Resource exhaustion in one rejected candidate does not corrupt state belonging to a different
// candidate.
