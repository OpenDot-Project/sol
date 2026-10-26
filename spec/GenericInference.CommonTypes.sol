// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative common-type rules; complete semantic and overload integration is pending.
module source.spec.generic_inference.common_types

// Common-type inference
// ====================
//
// This contract defines how multiple ordinary input bounds can determine one type argument. It is
// separate from conversion ranking, expected-result validation, and constraint validation. An
// implementation must preserve those phases instead of using a convenient conversion as an
// implicit generic type-selection rule.

// 1. Candidate set
// ----------------
//
// For one generic parameter, the solver gathers finite source type identities from all ordinary
// input occurrences. The candidate set is drawn from the input types and their reachable declared
// base/interface constraints after substitution. It does not enumerate all types in the program,
// synthesize arbitrary user types, or choose a type based on source-file order.
//
//     interface Named { func name(): String }
//     open class Entity: Named {
//         open func name(): String = "entity"
//     }
//     class Person: Entity {
//         override func name(): String = "person"
//     }
//     class Service: Entity {
//         override func name(): String = "service"
//     }
//
//     func <T> common(left: T, right: T): T = left
//
//     val entity = common(Person(), Service())
//
// If Entity is the only most-specific candidate accepted by both arguments, T is Entity. Named is
// also common, but it is less specific. Neither the first argument's type nor the lexically earlier
// declaration decides the result.
//
// A candidate must be reachable by a source-defined conversion from every lower-bound input. The
// solver does not assume every reference-like type converts to every interface or that every value
// converts to Object. The conversion relation is the language's declared conversion relation after
// nullable and ref-like restrictions.

// 2. Unique best common type
// -------------------------
//
// A candidate is viable only if every lower bound converts to it and every upper/expected bound
// accepts the specialized type. Among viable candidates, select one only when it is uniquely more
// specific than every other viable candidate under the language's type conversion relation.
//
// “More specific” means the candidate converts to the other candidate and the reverse conversion
// does not hold under the same allowed conversion category. An implementation must not rank by
// declaration depth alone when incomparable interface contracts are involved.
//
//     interface Printable { }
//     interface Persistable { }
//
//     class Report: Printable, Persistable { }
//     class Invoice: Printable, Persistable { }
//
//     func <T> accept(left: T, right: T): T = left
//
// If Report and Invoice share only Printable and Persistable, there may be two incomparable
// maximal common candidates. The compiler reports ambiguity or deferred inference according to
// whether additional local context can still resolve it. It does not pick Printable because that
// name sorts first, nor Persistable because it was declared last.
//
// If both inputs have the same constructed type, that exact identity is the unique best candidate.
// A redundant list of equivalent projection paths does not create an ambiguity.

// 3. Lower bounds and exact bounds
// -------------------------------
//
// An ordinary value parameter contributes a lower-bound relationship. A writable ref/out location
// contributes an exact identity requirement and is never joined through a base type.
//
//     func <T> readBoth(ref left: T, ref right: T): T = left
//
//     var name: String = "Ada"
//     var label: std.core.Object = "label"
//     readBoth(ref name, ref label)
//
// This call conflicts even if String converts to Object. Neither location may be copied into a
// temporary to manufacture one common reference type.
//
// When an exact bound and ordinary lower bounds coexist, the exact type is fixed and every ordinary
// argument must convert to it. Failure makes that candidate inapplicable. The solver cannot widen
// the exact type or reinterpret an ordinary bound as a ref/out storage identity.
//
//     func <T> replace(ref target: T, fallback: T): T = target
//
//     var selected: std.core.Object = "original"
//     val result = replace(ref selected, "fallback")
//
// If String converts to the exact Object location under Sol's ordinary conversion rules, T remains
// Object. The ref parameter is not specialized to String just because one value argument has that
// narrower type.

// 4. Invariant constructors
// -------------------------
//
// A constructed invariant type contributes exact nested evidence only after the outer constructor
// identity and arity match. It does not contribute a common type by replacing its arguments with
// their own common supertype.
//
//     class Box<T>(val value: T)
//     func <T> inspect(left: Box<T>, right: Box<T>): T = left.value
//
//     val value = inspect(Box<String>("a"), Box<std.core.Object>("b"))
//
// This candidate has conflicting invariant evidence. Box<String> and Box<Object> do not imply
// Box<Object>. The ordinary common type of String and Object is irrelevant inside invariant Box.
//
// The same rule applies recursively to nested invariant applications:
//
//     class Pair<A, B>(val first: A, val second: B)
//     func <T> nested(left: Pair<Box<T>, []T>, right: Pair<Box<T>, []T>): T =
//         left.first.value
//
// Each repeated T occurrence must solve consistently. A mismatch at any nested exact occurrence
// rejects the candidate and preserves the pair of source locations for the diagnostic.
//
// If a future declaration explicitly supplies variance metadata, that variance must be applied
// according to the generic declaration contract. It cannot be guessed from read-only usage or
// retroactively introduced by common-type inference.

// 5. Constructed interfaces and diamonds
// --------------------------------------
//
// The same nominal interface declaration may be reachable more than once. Projection is keyed by
// complete constructed identity, not just the interface declaration.
//
//     interface Source<T> { }
//     interface Left<T>: Source<T> { }
//     interface Right<T>: Source<T> { }
//     class Both<T>: Left<T>, Right<T> { }
//
// Both<String> reaches the identical Source<String> twice. It contributes one candidate and one
// evidence identity.
//
//     interface IntSource: Source<int> { }
//     interface TextSource: Source<String> { }
//     class InvalidMixed: IntSource, TextSource { }
//
// If the source rules reject this incompatible repeated base contract, lookup and inference retain
// the invalid declaration diagnostic. They cannot collapse the two constructed identities or
// choose whichever one happens to be visited first.
//
// Candidate closure has a deterministic finite budget. Exceeding that budget reports a resource
// outcome; it does not return the partial set as if it were complete.

// 6. Nullability
// --------------
//
// Nullability is part of type identity. A nullable input does not silently become non-null merely
// because another argument is non-null. A unique nullable join is allowed only when the language's
// nullable conversion rules accept all arguments and the expected result.
//
//     func <T> choose(left: T?, right: T?): T? = left
//
//     val result = choose("left", null)
//
// The non-null String input supplies the payload identity. The null literal is compatible with the
// nullable formal but does not independently choose T. The inferred result is String?.
//
//     func <T> requireBoth(left: T, right: T): T = left
//
//     val ambiguous = requireBoth("left", null)
//
// A null literal is not a non-null T input. Unless the language supplies another local bound, this
// candidate lacks enough evidence for T and cannot silently use Object.
//
// If all actual arguments are nullable, their nullable identities participate in the conversion
// check. The solver must not strip nullability from a typed variable to produce a more attractive
// non-null common candidate.

// 7. Value types, boxing, and ref-like restrictions
// ------------------------------------------------
//
// Boxing is considered only when the source conversion rules permit it for the actual and target
// categories. It is not an unconditional escape hatch that makes all value types share Object.
//
//     struct Point { val x: int }
//     func <T: std.core.Object> heapValue(value: T): T = value
//
// A Point value does not satisfy the Object constraint by boxing. A ref struct is never boxed or
// stored as an Object candidate. Its actual escape and anti-constraint rules remain in force even
// if another input has a reference type.
//
//     ref struct Window { val start: int }
//     func <T> combine(left: T, right: T): T = left
//
// A Window and an unrelated heap object do not infer a common heap type if doing so would require
// boxing or escape that the source contract forbids. The candidate is rejected or remains
// ambiguous; it is not promoted to Object.
//
// A common base type for two ordinary reference types does not authorize a ref-like type argument.
// Every generic anti-constraint is checked after the candidate has been selected.

// 8. Numeric values and literals
// -----------------------------
//
// Typed numeric variables contribute their declared types. Common-type inference does not invent
// numeric widening, a signedness conversion, or a floating representation solely to combine them.
//
//     func <T> numeric(left: T, right: T): T = left
//
//     val small: byte = 1
//     val wide: int = 2
//     val mixed = numeric(small, wide)
//
// The two typed values do not automatically infer int. The call follows the language's explicit
// generic input and conversion rules and may be inapplicable.
//
// An uncontextualized integer literal has its default source type int; an uncontextualized floating
// literal has double. Its exact lexical value is checked against the final target. Literal fitting
// can reject an unrepresentable candidate but does not change the type of a typed variable.
//
//     func <T> small(value: T): T = value
//     val count: byte = small(300)
//
// The expected target cannot make the literal fit in byte if 300 is not representable. The
// diagnostic points to the literal and reports representability, not a fabricated common type.

// 9. Function and tuple types
// ---------------------------
//
// Structural callable inputs contribute evidence only when parameter count, order, passing mode,
// result and effect relationship can be checked by the function-type contract. Contextual lambda
// syntax waits for a candidate-local expected function type.
//
//     func <T> invoke(callback: (T) -> String, item: T): String {
//         return callback(item)
//     }
//
//     val name = invoke({ value: String -> value }, "Ada")
//
// The typed lambda parameter and item argument agree on T = String. An untyped lambda cannot be
// checked against a type guessed from an unrelated candidate.
//
// Tuple constructors and element order are invariant structural positions unless another explicit
// type rule says otherwise. A pair with a different element count is an incompatible shape, not a
// partial source of evidence.

// 10. Expected result and common input
// -----------------------------------
//
// A local expected result constrains the result after candidate-local input evidence is aggregated.
// It may select among valid common candidates only when the result pattern and conversion rules
// define a unique solution.
//
//     open class Message { }
//     class TextMessage: Message { }
//     class AlertMessage: Message { }
//
//     func <T> select(left: T, right: T): T = left
//
//     val message: Message = select(TextMessage(), AlertMessage())
//
// If Message is the unique candidate satisfying both inputs and the expected result, inference may
// use it. If two incomparable interface candidates remain and the expected result accepts both,
// ambiguity remains. The expected declaration's type is not automatically substituted for every
// generic argument.
//
// Expected types are local to the call expression. Later assignment, return, overload, or future
// use sites cannot rewrite a completed inference decision.

// 11. Determinism, errors, and completeness
// ----------------------------------------
//
// The following cases remain distinguishable:
//
// - no candidate because an empty array/vararg supplies no element identity;
// - one viable candidate satisfying every input and expected result;
// - incompatible invariant nested evidence;
// - different exact ref/out identities;
// - no unique best among incomparable nominal candidates;
// - a nullable-only input with no non-null payload evidence;
// - a numeric mismatch that would need unsupported implicit widening;
// - failed nominal or ref-like constraints after candidate selection;
// - incomplete contextual lambda/effect evidence;
// - a bounded projection that exceeded its resource budget.
//
// Diagnostics and candidate output are stable under source-file enumeration changes. Alphabetical
// sorting is suitable for display but never chooses a semantic result. Error types and unresolved
// metadata cannot act as successful candidates.
//
// A call is complete only if a candidate is selected, all type parameters are closed, all
// constraints pass, all supplied values convert, the expected result is satisfied, and any
// candidate-specific lambda/effect work has a defined result. An API that reports only a
// provisional candidate must expose that state instead of calling it a successful binding.
