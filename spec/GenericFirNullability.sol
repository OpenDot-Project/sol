// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative nullability contract; nullable generic native ABI is not implemented.
module source.spec.generic_fir_nullability

// Generic substitution and nullability
// ====================================
//
// This specification defines the source type identities and semantic obligations that arise when
// a generic type parameter is substituted by a nullable or non-null type. It also states which
// nullable shapes the current verified FIR/native slice rejects. A source type that is representable
// in CST, PSI, AST, or BodyAnalysis is not automatically a valid native parameter, result, local,
// array element, or call target.
//
// Current implementation status:
//
// - Nullable written-type identity and the distinction between a nullable array reference and an
//   array with nullable elements are represented in the source type system.
// - Repeated nullable suffixes normalize to one nullable layer during type construction and
//   substitution. The type-specialization workspace keeps canonical identities.
// - Body analysis has nullable-aware expression and flow facts, including safe access, assertion,
//   Elvis, and local narrowing in the supported analysis surface.
// - The current native FIR verifier rejects nullable wrappers, nullable array elements, and
//   nullable arrays. Generic specialization does not add a nullable machine representation.
// - Per-instance source rebinding, all flow invalidation cases, nullable value layout, nullable
//   array GC maps, and runtime null gates remain incomplete.
//
// The examples below describe normative source behavior. A case marked `Source` concerns source
// type analysis; a separate `FIR` outcome concerns whether the current compiler can produce
// verified native code. The distinction must remain visible in diagnostics and release notes.

// 1. Nullable type constructor
// ----------------------------
//
// `T?` is a nullable type constructor over the complete preceding type. It is not a textual suffix
// that can be stripped before generic inference. If `T` is a declaration-owned generic parameter,
// `T?` is a source type expression whose result depends on the selected substitution.
//
// A nullable type has at most one nullable layer. The following type pairs are identical after
// normalization:
//
//     String?       == String??
//     int?          == int??
//     T?            == T??
//     ([]String?)?  == ([]String?)??
//
// Normalization is recursive but does not erase other constructors. `T?[]` is not a supported
// written form for the dynamic array constructor; dynamic arrays are written with the element type
// after `[]`. `[]String?` and `[](String?)` both mean a non-null array whose element type is
// nullable String. `([]String)?` means a nullable array of non-null String values.
//
// The parser preserves the written shape in syntax nodes. The type-analysis phase constructs a
// canonical identity. Tools may retain the source spelling for display, but overload comparison,
// substitution, equality, and FIR validation use canonical type identities rather than suffix text.

func nullableSyntaxExamples(
    stringValue: String?,
    repeatNullable: String??,
    arrayOfNullable: []String?,
    parenthesizedElement: [](String?),
    nullableArray: ([]String)?,
    bothNullable: ([]String?)?,
    nestedNullableLeaf: [][]String?,
): () {}

// `repeatNullable` and `stringValue` have the same type identity. `arrayOfNullable` and
// `parenthesizedElement` have the same type identity. `nullableArray` has an outer nullable wrapper.
// `bothNullable` has an outer nullable wrapper and a nullable element type.
//
// A type parser or printer that rewrites `[]String?` as `([]String)?` changes the program and is
// incorrect. Parentheses are semantically significant when the nullable wrapper applies to the
// array reference rather than its element.

// 2. Identity and owner-sensitive substitution
// ---------------------------------------------
//
// A generic parameter is identified by its canonical declaration owner and ordinal. Equal spelling
// does not make two parameters equal. Nullable substitution first selects the parameter identity,
// then rewrites the complete type graph using one simultaneous substitution map.
//
//     func <T> echo(value: T): T = value
//     func <U> lift(value: U?): U? = value
//
// In `lift<String>(null)`, `U` is bound to `String`; the parameter and result are `String?`. If a
// source call supplies an already nullable value and the inference contract admits that evidence,
// substituting `U = String?` into `U?` normalizes to `String?`, not `String??`.
//
// Substitution is simultaneous. If a legal substitution map contains `T -> U?` and `U -> String`,
// replacing `T` produces the closed replacement `U?` as written by the map; it does not recursively
// apply the `U` entry to the replacement during the same rewrite. The declaration and inference
// layer must provide a complete canonical map for the selected call. Repeated sequential mutation
// of the source type graph is forbidden.
//
// Substitution traverses nullable wrappers, dynamic and fixed arrays, tuple elements, function
// parameter/result types, and declared effects. It preserves passing modes and effect-set identity.
// A nullability rewrite cannot change the owner or ordinal of a generic parameter.

func <T> unwrapAfterExplicitChoice(value: T?): T? = value
func <T> wrapAfterExplicitChoice(value: T): T? = value

// The two declarations have different source signatures. With `T = String`, the first accepts a
// nullable String and the second returns a nullable String. Neither substitution implies that null
// is accepted as a non-null `T` argument.

// 3. Reference and value nullability
// ----------------------------------
//
// The `?` marker has one source-level presence meaning: the type can carry null. The runtime
// representation depends on the underlying type category and target ABI. A nullable reference may
// use a null reference representation; a nullable value may require a discriminant or another
// qualified representation. The source identity does not license the native backend to guess one.
//
// `String?` is not merely an alias for `String` plus a relaxed check. The verifier and lowerer must
// agree on its calling convention, storage, root visibility, comparison behavior, and return ABI.
// A nullable numeric value likewise needs an exact layout and operation semantics. LLVM pointer
// width is not a substitute for nullable value layout.
//
// The same requirement applies to generic substitutions. If `T` is used as a parameter, local,
// return value, array element, tuple member, callback result, or thrown effect, every occurrence
// affected by `T = X?` must use the same nullable identity and representation. A backend cannot
// erase the wrapper at just one occurrence because the underlying `X` happens to fit a native
// register.
//
// The current native subset supports only the non-null representations enumerated by
// `FirVerifier.Supported`. It does not support nullable values in generic signatures or bodies.
// This is a backend admission limit, not a source-language statement that generic nullable types
// are invalid.

// 4. Nullable generic inference evidence
// --------------------------------------
//
// Candidate-local generic inference consumes nullable information only through the evidence rules
// defined in the generic inference contracts. A non-null argument of type `String` does not imply
// that a parameter `T?` should infer `T = String?`; the relation is selected by the candidate's
// formal type and evidence rule. An argument already typed `String?` must retain its nullable
// identity instead of being silently narrowed from its current runtime value.
//
// An explicit type argument fixes one generic parameter before argument checking. The compiler
// substitutes that fixed identity into the candidate signature, normalizes nullable layers, then
// checks conversions, ref/out exactness, constraints, and effects. Explicit syntax does not waive
// nullability safety.
//
// An expected result may provide evidence when the enclosing expression supplies a concrete
// expected type and the generic-inference rules permit the relation. The expected type is not
// inferred from a later statement, a sibling branch, or an eventual native ABI. If several
// nullable substitutions remain possible, the candidate remains ambiguous or deferred according
// to the owning analysis contract.
//
// The null literal alone does not choose an arbitrary non-null generic type. In a nullable context,
// it may be compatible with several `T?` candidates, but that does not prove one unique `T`. A
// return annotation, explicit generic argument, or other permitted typed evidence must determine
// the underlying type where required. Never default unresolved null-only evidence to `Object`,
// `String`, or a backend pointer type.

func <T> fromNullable(value: T?): T? = value
func <T> fromNonNull(value: T): T = value
func <T> chooseByContext(): T? = null

// Source outcomes depend on the full candidate set and expected context:
//
// - `fromNullable<String>(null)` has a fixed underlying type and nullable parameter.
// - `fromNonNull<String>(null)` is rejected by source argument checking.
// - `chooseByContext<String>()` is closed by the explicit type argument.
// - an omitted type argument with no input or expected-result evidence remains unresolved; the
//   compiler must not construct an open FIR call.
//
// An inference result of `Bound` is not a native specialization proof. The FIR verifier still
// checks closure, declaration constraints, substituted types, and target ABI admission.

// 5. Arrays: nullable container vs nullable element
// ------------------------------------------------
//
// The array constructor consumes the complete element type. Consequently:
//
//     []String?        non-null dynamic array; each element may be null
//     [](String?)      the same element-nullable array identity
//     ([]String)?      nullable dynamic array; elements are non-null String
//     ([]String?)?     nullable dynamic array; elements may also be null
//     [][]String?      nested non-null arrays with nullable String leaves
//
// The outer `?` on a dynamic array requires grouping. A formatter may choose one configured
// equivalent spelling for a nullable element, but it must not move the wrapper outside `[]`.
//
// A null array reference and a non-null empty array have different identities and behavior. The
// empty array has length zero. The nullable array has no usable length until non-null is proven or
// a safe-access operation is selected. A non-null array with nullable elements can be indexed, but
// the result remains nullable until the element is proven non-null.
//
// For generic `T`, `[]T` and `[]T?` are not interchangeable. Substituting `T = String?` into `[]T`
// yields `[](String?)`, which is equivalent to `[]String?`. Substituting `T = String` into `[]T?`
// also yields `[]String?`. In both cases the array container itself is non-null. An outer nullable
// array must be represented explicitly as `([]T)?`.
//
// The current native FIR subset admits only one-dimensional arrays of supported non-null scalar or
// `String` elements. It rejects nullable element arrays, nullable array references, nested arrays,
// and fixed arrays. A generic array specialization with `T = int` can fit the current native array
// boundary when its entire body is otherwise admitted. `T = String?` and `T = []int` do not fit the
// current closed type predicate.

func <T> firstOrNull(values: []T): T? {
    if values.length == 0 { return null }
    values[0]
}

// Whether `firstOrNull` can be emitted depends on the substituted result ABI. Source typing may
// identify `T?` correctly, but the current FIR gate rejects that nullable result. This example is
// normative source behavior, not an assertion that the native backend can lower it.

// 6. Nullable array flow and element flow
// ---------------------------------------
//
// A flow fact about the container does not prove any element non-null. A check `values != null`
// narrows the array reference only. A later indexed value from `[]String?` still has type `String?`.
// Each element read has its own value identity; checking one index does not generally refine a
// different index or a later read from a mutable array.
//
// Array element mutation invalidates facts for that element location if the language's aliasing
// rules permit another write. A local copy of an element may be stable after assignment to `val`,
// but its provenance and capture behavior must be proven. Array iteration over nullable elements
// binds a nullable item; the iterator does not silently unwrap null.
//
// A nullable array receiver may be checked and then used in the dominated region if the flow
// analyzer proves the same stable local identity. A property access, mutable alias, unknown call,
// closure capture, or asynchronous write can invalidate the proof under the flow contract.
//
//     func countPresent(values: ([]String?)?): int {
//         if values == null { return 0 }
//         var count = 0
//         for val item in values {
//             if item != null { count += 1 }
//         }
//         count
//     }
//
// The first null check proves the container non-null inside the branch. The inner check proves only
// the current `item` non-null in the true branch. It does not alter the declared element type.

// 7. Flow facts inside a generic body
// -----------------------------------
//
// A generic declaration may be analyzed with a nullable `T?` parameter while `T` is still open.
// Source flow can reason about a local test such as `value != null`, but the validity of that fact
// must be checked against storage stability, writes, captures, calls, and joins. The fact refines a
// value at a program point; it does not replace the declaration's canonical nullable type.
//
// A smart cast cannot be serialized as if the generic declaration changed from `T?` to `T`. FIR
// specialization must either retain the source flow proof and substitute it consistently, or rerun
// the relevant analysis under the concrete type. It cannot infer non-null from `T = String`, since
// the source argument might itself be a nullable instantiation in a future supported context.
//
// The current generic FIR slice substitutes already-bound expression type facts but does not rerun
// complete body analysis for every instance. Its narrow admission therefore rejects generic member
// and operator operations that need concrete-type re-resolution, and the FIR verifier rejects
// nullable native types. It does not claim to complete nullable generic flow lowering.

// 8. Safe access, assertion, and Elvis
// ------------------------------------
//
// `receiver?.member` evaluates the receiver once. When null, the selected member access or call
// arguments are skipped. When non-null, member lookup uses the underlying type. A safe property
// result is lifted to nullable even when the property itself is non-null. If the member result is
// already nullable, normalization retains one nullable layer.
//
// Safe calls do not narrow the original receiver after the expression. Safe access is not
// automatically writable storage and cannot be used as an address for `ref` or `out`.
//
// `value!!` performs an explicit null assertion. Source analysis gives the asserted expression its
// non-null type on the normal path; the null path throws or terminates according to the runtime
// contract. It is not a cast and does not assert that a generic type argument has any other bound.
//
// `left ?: right` evaluates `left` once. The right operand executes only when the left value is
// null. Its result type is selected by the nullable-left and right-hand conversion rules. If the
// right operand has type `!`, it contributes no normal value, so the expression's normal result can
// use the non-null left type. A branch returning `!` does not create a phantom default value.
//
// For generic `T?`, safe access, `!!`, and Elvis preserve the owner-sensitive `T` identity and the
// exact flow/effect facts. Any synthetic null gate must retain the source origin and a verifier proof.
//
// These operations are outside the current native generic specialization admission except where
// an existing independent verifier rule explicitly supports a shape. The current FIR gate rejects
// nullable result/slot types and safe-call nodes; source acceptance does not override that gate.

// 9. Bottom and nullable bottom
// -----------------------------
//
// `!` is the bottom type. It has no values and no normal continuation. It converts to every source
// type because a non-completing expression never supplies a value on a normal edge. `!?` denotes
// the nullable form of bottom, whose only value is null under the current type specification.
// Repeated nullable wrapping is still normalized: `!??` has one nullable layer.
//
// Bottom is not an uninitialized value, an error type, a wildcard, or a nullable reference. A call
// returning `!` does not assign an `out` parameter after its call site because no normal successor
// exists. Checked effects raised before termination remain part of the source effect obligations.
//
// In generic inference, a bottom-typed argument may supply evidence only under the ordinary
// conversion/evidence rules. It cannot be used as a fabricated concrete native type argument. The
// FIR specialization boundary rejects `!` as a storable slot or instance ABI argument. A
// bottom-returning source function may require a distinct control-flow lowering rule before it can
// enter native output.

func neverReturns(): ! { throw RuntimeException("no normal result") }
func nullBottom(): !? = null

// `nullBottom` is a nullable-bottom value, not a function that fails to infer its result type.
// It remains outside the current native generic argument boundary.

// 10. Checked effects and nullable generic values
// -----------------------------------------------
//
// Nullability does not change whether an exception type is checked or unchecked. The static
// declared effect identity determines caller obligations. A nullable exception parameter is not a
// valid thrown value until flow or explicit assertion proves it non-null; `null` is not thrown as a
// Throwable. If a generic effect parameter is substituted, its type argument must still satisfy
// Throwable constraints and the resulting effect set must be checked under the call contract.
//
// A nullable wrapper around Throwable does not itself satisfy a non-null Throwable bound. The
// compiler must not erase `?` to accept `throws E?`, cast a null to a Throwable reference, or remove
// a checked effect because the current control flow appears non-throwing.
//
// The current verified native subset preserves declared non-null effect metadata for admitted
// function bodies, but does not lower source catch/finally or arbitrary nullable exception flow.
// Generic nullable effects remain unadmitted until both source obligations and native exception
// behavior have a complete contract.

// 11. Native ABI admission and diagnostic ownership
// -------------------------------------------------
//
// FIR admission occurs only after source analysis has selected one callable and produced a complete
// closed substitution. Every signature and body type is checked against the native subset. A
// nullable argument, result, local, array element, tuple field, function type, or effect wrapper
// must be rejected at the owning source location if the verifier lacks a matching representation.
//
// `SOL2400` reports source constructs outside the verified native subset. `SOL2402` denotes an
// independent FIR verifier rejection. `SOL2403` denotes a generic specialization resource limit.
// These codes belong to distinct phases. Nullable source diagnostics such as `SOL2243` remain
// source-binding outcomes; they are not rewritten as backend errors when the source expression is
// itself invalid.
//
// A source-valid nullable program rejected by native FIR is not a syntax error or a generic
// inference failure. The diagnostic must identify the unsupported nullable operation or type
// position and retain its source span. The compiler must not emit a partially lowered body, erase
// the wrapper, choose a worse overload, or silently change `[]String?` into `([]String)?`.
//
// Existing type-analysis support is broader than the initial native ABI:
//
// - nullable reference and nullable value types can have source identities;
// - arrays may have nullable elements or a nullable container;
// - tuples and function types can carry nullable positions;
// - generic substitution can reconstruct nullable wrappers;
// - source flow can refine selected stable values.
//
// None of these facts promises that every such shape has an LLVM type, GC descriptor, calling
// convention, root map, native equality operation, or safe-call control-flow lowering.

// 12. Substitution order through nested constructors
// ---------------------------------------------------
//
// For each source occurrence, first resolve its canonical owner/ordinal type parameters and then
// apply the final candidate substitution simultaneously. Normalize nullable layers after replacing
// the entire type node. Preserve array constructors around their element types and preserve outer
// nullability around the array separately.
//
// Examples with source parameter `T`:
//
//     T?                  substitute T = String?  => String?
//     []T                 substitute T = String?  => []String?
//     ([]T)?              substitute T = String   => ([]String)?
//     ([]T?)?             substitute T = String   => ([]String?)?
//     [][]T               substitute T = String?  => [][]String?
//     (T?, String)        substitute T = int      => (int?, String)
//
// The dynamic-array prefix is part of the type tree. `[]T?` parses as array of nullable `T`;
// `([]T)?` parses as nullable array of non-null `T`. Parenthesized spelling is not discarded until
// after type identity is known.
//
// If substitution produces a constructed type that violates an invariant, the type workspace
// returns its bounded failure outcome. For example, a ref-like element cannot be placed in an
// ordinary managed array merely because the array was written with an open `T` parameter. The
// compiler must not catch that failure and keep the pre-substitution open array type.

// 13. Flow joins and branches involving nullable results
// ------------------------------------------------------
//
// A conditional branch may produce one nullable value and one non-null value. The result type is
// determined by source common-type rules; it is not always the type of the branch evaluated first.
// A branch with type `!` contributes no normal result edge. A branch returning null contributes a
// nullable candidate only when the context and language rules permit it.
//
// When `T?` is returned from a generic function, both branch expressions must be bound against the
// declaration-level expected type. A concrete native substitution cannot retroactively make a
// source-invalid branch valid. Conversely, a source-valid branch still needs a native representation
// for the complete specialized result type.
//
// Flow joins intersect facts from all reachable incoming edges. The non-null fact from one branch
// cannot be copied to the join when another reachable branch leaves the value nullable. A bottom
// edge is removed because it has no normal successor. Loops require fixed-point analysis; a check
// that occurs only in one iteration does not prove the value non-null before the loop.
//
// 14. Source examples and outcome annotations
// -------------------------------------------
//
// `spec/examples/semantics/GenericFirNullability.sol` is the source-case matrix for this contract.
// Its annotations use these categories:
//
// - `Source: Bound` — the source-level type, candidate, and permitted flow obligations are complete.
// - `Source: Rejected` — a source invariant is violated, such as nullable use without a required
//   proof or a non-null parameter supplied a nullable value.
// - `Source: Deferred` — a documented analysis obligation remains unresolved; it is not success.
// - `FIR: Rejected` — source typing may succeed, but a nullable representation is outside the
//   current verified native subset.
// - `FIR: Verified` — reserved for cases whose complete closed signature/body is admitted and
//   independently checked; the example file does not claim nullable native support today.
//
// Each case names the affected nullable position, generic owner/ordinal, normalized type, expected
// source result, expected flow fact, and current native outcome. A parser-only example is not proof
// that source binding or FIR accepts it. A passing native program for `T = int` is not evidence for
// `T = int?` or `T = String?`.

// 15. Current native subset boundaries
// ------------------------------------
//
// The current generic FIR slice admits closed module-level free-function specializations only when
// their type arguments and complete substituted body use supported native representations. It
// admits selected primitive scalars, String, and one-dimensional arrays of supported non-null
// elements. It rejects nullable wrappers and nullable element arrays.
//
// It also rejects generic member/owner specialization, function values, closures, unsupported
// native operations on an open type parameter, default/vararg lowering, and source shapes that
// cannot be independently checked. Generic body facts are currently projected from declaration-
// level analysis; full source rebinding for every concrete substitution is future work.
//
// A source function may be semantically valid and still receive `SOL2400` from FIR admission. This
// does not change the source type identity and must not make the frontend report the declaration as
// ill-typed. The backend cannot add an alternate nullable representation by pointer-casting the
// value or by passing null through a non-null parameter.

// 16. Future admission requirements
// ----------------------------------
//
// Nullable native generic support requires all of the following before the native gate expands:
//
// 1. A concrete representation for nullable references and nullable values for each admitted ABI
//    position, including parameters, results, locals, spills, arrays, and runtime calls.
// 2. A GC descriptor and root/barrier rule for nullable reference values and arrays with nullable
//    reference elements; null payloads must not be treated as live objects.
// 3. Exact source-to-FIR proofs for nullable operations, safe-call gating, Elvis branches, assertions,
//    equality, comparison, casts, and flow refinements.
// 4. Per-instance body rebinding or an equivalent proof that generic overload/member/operator
//    selection remains identical after substitution.
// 5. Correct checked-effect and bottom-flow propagation through all normal and exceptional edges.
// 6. Matching AOT and ORC behavior, artifact metadata, diagnostic spans, and serialization schema.
// 7. Positive and negative regression coverage for both array element nullability and outer array
//    nullability, with no parser-only claim counted as native support.
//
// Adding a nullable type to `FirVerifier.Supported` before those obligations are met is not an
// implementation. A local success for nullable String does not prove nullable integer layout, GC
// safety, generic member calls, or general checked-exception behavior.

// 17. Compatibility and non-goals
// --------------------------------
//
// This specification does not change the entry contract, command-line interface, LLVM version,
// runtime process isolation, or `.solib` schema. It adds no source keyword and no dynamic type
// descriptor. A specialization key remains declaration identity plus an ordered tuple of closed
// canonical type identities; nullability is part of each canonical identity.
//
// The first native slice is compilation-local. It does not provide an on-disk nullable specialization
// cache, a stable generic C ABI, cross-version code sharing, boxing, dynamic dispatch, nullable
// function values, or native reflection for generic nullable types.
//
// The long-term Sol source contract may admit all nullability forms independent of the temporary
// native subset. The spec, not the current ABI gate, owns the language meaning. Compiler code,
// diagnostics, docs, and release notes must label those two layers separately.

// 18. Contract for compiler phase boundaries
// -------------------------------------------
//
// The parser records the written nullable constructor and its grouping. PSI and AST preserve a
// recoverable source origin. Type construction creates the canonical nullable identity. Generic
// inference selects a closed substitution. Source body analysis checks conversions and flow facts.
// FIR construction may project only facts that are represented in its nodes. FIR verification
// independently rejects any nullable type that lacks an admitted layout or operation rule.
//
// Each phase must preserve the same distinction between `[]T?` and `([]T)?`. A later phase must
// not reconstruct grouping from a formatted name or a legacy display string. Canonical identity
// serialization includes both the array constructor and the nullable layer's position. Diagnostic
// serialization retains the source span even when normalization collapses `T??` to `T?`.
//
// A source diagnostic is appropriate when the program violates a language rule: for example, a
// nullable value is passed to a non-null formal without a permitted proof. A backend diagnostic is
// appropriate when the source program is well-typed but its closed FIR form is outside the current
// native subset. The two errors must not be conflated, and an unsupported backend shape must not
// cause overload resolution to retry with a different candidate.

// 19. Review checklist for nullable specializations
// -------------------------------------------------
//
// Before enabling a new nullable generic specialization, reviewers must be able to answer all of
// these questions from code and phase-owned invariants:
//
// - What exact source type identity does each nullable position have after substitution?
// - Did inference use only permitted argument, explicit-type, and expected-result evidence?
// - Does the substitution map use canonical parameter owners and ordinals?
// - Is nullability preserved at every array, tuple, function, and result constructor?
// - Which control-flow edge proves a value non-null, and which writes can invalidate that fact?
// - Does each safe-call, assertion, and Elvis operation evaluate each operand the specified number
//   of times and preserve checked effects on exceptional edges?
// - What machine representation and calling convention are used for references and values?
// - Which GC roots and array element descriptors describe nullable references?
// - Do the verifier and lowerer consume the same representation proof?
// - Are AOT and ORC behavior, diagnostics, and emitted metadata consistent?
//
// If any answer depends on a guessed pointer layout, a textual type spelling, or an unrecorded
// source-flow fact, the specialization remains rejected. Increasing a resource limit or adding a
// conversion helper cannot compensate for a missing semantic or representation invariant.
