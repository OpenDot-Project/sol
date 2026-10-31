// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative inherited overload contract; interface default conflict closure is pending.
module source.spec.member_resolution.overloads

// The member set is not an overload winner
// =========================================
//
// Receiver lookup first forms a set of declarations. Applicability and overload ranking then run
// over that set using the normal function-call contract. Looking at the nearest declaration and
// stopping before argument types are known is incorrect: a distinct base overload may be the only
// applicable signature.
//
//     open class Base {
//         open func read(index: int): String = "index"
//         open func read(name: String): String = name
//     }
//
//     class Cache: Base {
//         func read(key: byte): String = "key"
//     }
//
//     func use(cache: Cache): String {
//         val byIndex = cache.read(2)
//         val byName = cache.read("item")
//         val byKey = cache.read(1 as byte)
//         return byName
//     }
//
// The three calls compare all applicable signatures. The nearest `read(byte)` does not hide the
// other two `read` overloads. The integer literal is checked against each applicable parameter and
// literal preference rules are applied after mapping.

// SIGNATURE IDENTITY
// ==================
//
// A method signature consists of its name, generic arity, ordered parameter types, and ordered
// passing modes. For an inherited signature, parameter types are substituted through the complete
// constructed base path before comparison. The following do not distinguish overloads:
//
// - return type;
// - checked effects;
// - parameter names;
// - default expressions;
// - generic constraints;
// - nullable reference annotation when the underlying reference storage identity is otherwise
//   identical;
// - `ref` versus `out` alone.
//
// Value versus ref/out mode remains part of the signature; ref and out require exact source markers.
// Vararg is a formal calling mode with a separate expansion rule. Same-signature declarations in
// one nominal owner are duplicate declarations even when only a non-identity item above differs.

// OVERRIDE REPLACEMENT
// ====================
//
// `override` matches a base function by generic arity, passing modes, and substituted parameter
// types. It cannot replace a property, constructor, nested type, or different overload. A valid
// override must also satisfy:
//
// - a matching inherited virtual/abstract/interface member exists;
// - the inherited member is not final and its owner permits overriding;
// - the return type is compatible under the source result conversion rule;
// - accessibility is not reduced;
// - checked effects are equal to or narrower than the inherited effect budget;
// - inherited parameter names and default expressions continue to come from the static base
//   contract and are not redeclared.
//
//     interface Parser<T> {
//         func parse(text: String, fallback: T? = null): T?
//     }
//
//     class StringParser: Parser<String> {
//         override func parse(text: String, fallback: String?): String? = fallback
//     }
//
//     func use(parser: StringParser): String? = parser.parse(text = "input")
//
// The call's omitted `fallback` comes from the `Parser<String>` contract. The implementation's
// parameter spelling cannot change the caller-visible named argument. Virtual dispatch chooses
// the StringParser body while default selection remains static.

// A same-signature declaration without `override` is invalid even if its body text is identical.
// The compiler emits an override diagnostic and can continue recovery by preferring the
// most-derived declaration. That recovery candidate is not proof of a valid program.

// GENERIC OVERRIDE SUBSTITUTION
// =============================
//
// Owner substitutions and method substitutions are composed in separate maps. First substitute
// the base owner arguments. Then alpha-map the base method's generic parameter ordinals to the
// overriding method's parameter ordinals for signature comparison.
//
//     interface Mapper<T> {
//         func <R> map(source: T, transform: (T) -> R): R
//     }
//
//     class StringMapper: Mapper<String> {
//         override func <U> map(source: String, transform: (String) -> U): U = transform(source)
//     }
//
// `R` and `U` have different owner identities but the same ordinal in the base/override signature
// comparison. Parameter names do not form that relation. A method returning String for every U
// does not satisfy the above signature; parameter and result contracts are checked independently.

// BASE OVERLOADS AND MOST-DERIVED METHODS
// ========================================
//
// The inherited callable set merges different signatures from the receiver and its bases. A
// same-signature override removes the corresponding base candidate from dispatch selection. A
// same-signature method from two unrelated interfaces is not deduplicated by text; it requires a
// valid common override or becomes ambiguous at an indistinguishable call site.
//
//     interface ReadById { func read(id: int): String }
//     interface ReadByName { func read(name: String): String }
//
//     class Combined: ReadById, ReadByName {
//         override func read(id: int): String = "id"
//         override func read(name: String): String = name
//     }
//
// The class contains two distinct overloads. A call with an `int` selects the first; a call with a
// `String` selects the second. Implementing one interface does not remove the other overload.
//
//     interface Primary { func render(value: String): String }
//     interface Secondary { func render(value: String): String }
//
//     class Conflicted: Primary, Secondary { }
//
// `Conflicted().render("x")` is ambiguous until a declaration explicitly resolves the identical
// contracts. Source order of `Primary` and `Secondary` is not a resolution policy. If one interface
// extends the other and both paths reach the same declaration identity, the repeated path is
// collapsed instead of reported as two candidates.

// DEFAULT AND VARARG CONTRACTS
// ============================
//
// A derived method does not replace the base declaration's default expression. Caller-side
// parameter names/default origins are selected from the static contract. This rule holds even when
// lookup reaches an inherited overload through multiple generic base projections.
//
// Vararg expansion and default count are ranking dimensions only after conversions for the mapped
// arguments have been compared. Ordinary arity is preferred over an otherwise equally ranked
// expansion. A candidate consuming fewer defaults is preferred when conversion ranks tie. This
// does not allow a less-specific conversion vector to win merely because the candidate has fewer
// defaults.
//
// An override's parameter names must remain compatible with the inherited named-call contract.
// Default expression redeclaration on an override is rejected. Different defaults on two
// unrelated inherited origins are a declaration conflict, not a runtime dispatch choice.

// CONVERSION RANKS AFTER INHERITED LOOKUP
// =======================================
//
// Applicable candidates compare per-argument conversion vectors. Identity is preferred to
// reference conversion, reference conversion to boxing, and boxing to one permitted user implicit
// conversion. A candidate is better only if it is no worse for every compared argument and strictly
// better for at least one, subject to the language's exact literal preference. Incomparable vectors
// remain ambiguous.
//
// Constraints are applicability filters. They are not a tie-breaker. A more restrictive constraint
// does not grant an overload priority over a less restrictive valid signature. Generic/non-generic
// equal-rank candidates prefer the exact non-generic signature. Declaration/file order is never a
// final tie-break.
//
// An inaccessible candidate can still own a name and prevent a base fallback. Accessibility is
// checked after target resolution and is reported at the call. The compiler must not silently pick
// a less-visible or less-specific method that happens to be callable from this source location.

// AMBIGUITY CLASSES
// =================
//
// The source analysis distinguishes these cases:
//
// 1. Name ambiguity: non-callable declarations or mixed value/callable categories share a name.
// 2. Inherited declaration conflict: two base contracts require unresolved same-signature methods
//    or defaults.
// 3. Generic inference conflict: candidate type variables receive incompatible exact/lower/upper
//    evidence.
// 4. Overload ambiguity: multiple complete candidates remain equally applicable after inference.
// 5. Deferred selection: an unsupported candidate analysis could change which complete candidate
//    wins.
//
// These states are not interchangeable. A diagnostic should point to the declaration for an
// inherited conflict, the call for a final overload tie, and the smallest generic evidence source
// for an inference conflict. Deferred analysis records the unverified call rather than reporting a
// successful arbitrary winner.

// Source-order permutations required for validation:
//
// - receiver declaration before/after its bases in the supplied files;
// - base interface order reversed;
// - override source file before/after base declaration file;
// - named arguments reordered while preserving parameter mapping;
// - equivalent partial declaration file enumeration changes;
// - duplicate diamond path added/removed without changing constructed identity.
//
// All non-ambiguous outcomes must remain stable. All genuinely ambiguous outcomes must remain
// ambiguous under each permutation.
