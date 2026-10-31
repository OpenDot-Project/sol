// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative nullability and flow cases, continued from GenericFirNullability.sol.
module source.spec.examples.semantics.generic_fir_nullability

// Nested nullable substitution, native boundaries, flow and diagnostic cases

// 46. Nested generic substitution is simultaneous and owner-sensitive.

func <T> nestedPair(value: (T?, []T)): (T?, []T) = value

// expect: Source: Bound; T = String?; first = String?; second = [](String?) == []String?; FIR: Rejected(SOL2400).
func case46OwnerOrdinalSubstitution(value: (String?, []String?)): (String?, []String?) =
    nestedPair<String?>(value)

// Every occurrence uses one closed substitution map. Equal parameter names owned by other
// declarations must not be rewritten by this call's map.

// 47. Expected result evidence and argument evidence must agree after normalization.

func <T> relay(value: T?): T? = value

// expect: Source: Bound; input String? and expected String? agree on T = String; FIR: Rejected(SOL2400).
func case47ConsistentInferenceEvidence(value: String?): String? = relay(value)

// If the inference relation cannot reconcile evidence without erasing nullability, it reports a
// source inference failure instead of picking whichever evidence is convenient for lowering.

// 48. Flow joins retain nullability when a reachable branch yields null.

func branchJoin(flag: bool, value: String): String? = {
    if flag { value } else { null }
}

// expect: Source: Bound; result String?; join has both reachable paths; FIR: Rejected(SOL2400).
// The true-branch non-null fact cannot be copied to the join point.

// 49. An unreachable bottom branch does not add a nullable result.

func branchWithBottom(flag: bool, value: String): String = {
    if flag { value } else { fail() }
}

// expect: Source: Bound; result String; bottom branch has no normal edge; FIR: Rejected(SOL2400).
// Bottom removes a reachable result edge; it does not synthesize null or a default value.

// 50. Incompatible nullable evidence is not repaired by native specialization.

func <T> keep(value: T): T = value

// expect: Source: Rejected; argument String? cannot bind to non-null T = String without a proof.
func case50IncompatibleNullableEvidence(value: String?): String = keep<String>(value)

// This source failure occurs before FIR admission. It must not be reported as SOL2400 or hidden by
// a cast inserted solely to make a specialization key appear closed.

// 51. Normalized identities are the keys used by specialization caches.

func <T> cacheProbe(value: T?): T? = value

// expect: Source: Bound; cache identity for T=String? equals the identity for the normalized one-layer form.
// expect: FIR: Rejected(SOL2400); no nullable cache entry or machine body is emitted.
func case51NormalizedSpecializationIdentity(value: String??): String? =
    cacheProbe<String?>(value)

// Display spelling may retain source text, but cache keys use declaration identity and canonical
// closed types. A cache hit cannot bypass current verifier admission.

// 52. A source-valid outer-nullable array call must not be rewritten for a supported array ABI.

func <T> arrayLength(values: ([]T)?): int? = values?.length

// expect: Source: Bound with T = int; argument and result have outer nullability; FIR: Rejected(SOL2400).
func case52DoNotEraseOuterArrayNullability(values: ([]int)?): int? = arrayLength<int>(values)

// The non-null `[]int` ABI is not applicable until the source-level null gate and nullable result
// are lowered and verified. Pointer-casting the input would change its contract.

// 53. A nullable element array is not interchangeable with a nullable array argument.

func <T> elementHead(values: []T?): T? = values[0]

// expect: Source: Bound with T = String; formal is []String?; result String?; FIR: Rejected(SOL2400).
func case53ElementArrayAbi(values: []String?): String? = elementHead<String>(values)

// The array container is non-null. This specialization needs an element descriptor capable of
// distinguishing null references from live String references.

// 54. Nullable outer arrays retain a null gate independently from element shape.

func <T> maybeHead(values: ([]T)?): T? = {
    if values == null { null } else if values.length == 0 { null } else { values[0] }
}

// expect: Source: Bound with T = String; container gate and empty branch produce null; FIR: Rejected(SOL2400).
func case54NullableContainerAndResult(values: ([]String)?): String? = maybeHead<String>(values)

// Each normal branch produces either a nullable result or the selected non-null String lifted to
// that result. No branch manufactures an array or invents an element.

// 55. Stable branch facts do not survive an invalidating write.

func case55WriteInvalidates(value: String?): int = {
    if value == null { return 0 }
    value = null
    value.length
}

// expect: Source: Rejected(SOL2243); reassignment invalidates the previous non-null flow fact.
// A sound analyzer does not reuse a fact across a write simply because the source variable name is equal.

// 56. A captured mutable value needs capture-aware flow invalidation.

func case56CaptureInvalidates(value: String?): int = {
    if value == null { return 0 }
    val clear = { value = null }
    clear()
    value.length
}

// expect: Source: Rejected(SOL2243); mutable capture may invalidate the proof before the read.
// If a language rule establishes stronger closure sequencing, the analyzer must record that proof.

// 57. Safe access to a nullable element is distinct from safe access to a nullable array.

func safeElement(values: []String?, index: int): int? = values[index]?.length

// expect: Source: Bound; array is non-null; indexed element is String?; safe member result is int?.
// expect: FIR: Rejected(SOL2400); nullable elements and safe-call control flow are not admitted.

// The element is evaluated once, then its null gate guards `length`; the array itself is not gated.

// 58. Two safe-call levels require two distinct nullable checks.

func safeArrayAndElement(values: ([]String?)?): int? = values?.get(0)?.length

// expect: Source: Bound; first gate checks container, second gate checks element; result int?.
// expect: FIR: Rejected(SOL2400); nested null gates have no current native lowering.

// Neither gate proves the corresponding local non-null after this expression.

// 59. Elvis on an indexed nullable element does not make the array nullable.

func elementOrEmpty(values: []String?, index: int): String = values[index] ?: ""

// expect: Source: Bound; values is non-null; selected element is String; FIR: Rejected(SOL2400).
// The fallback handles a null element and says nothing about the container representation.

// 60. Explicit type arguments preserve nullable array shape in nested substitution.

func <T> arrays(value: []T?): ([]T?)? = value

// expect: Source: Bound with T = String; result is nullable array of nullable String elements.
// expect: FIR: Rejected(SOL2400); neither outer nor element nullable representation is admitted.
func case60BothArrayDimensions(value: []String?): ([]String?)? = arrays<String>(value)

// The result grouping is essential: the nullable constructor wraps the array, while its element
// remains nullable inside `[]`.

// 61. Generic inference cannot use a current FIR rejection as negative source evidence.

func <T> selectNullable(value: T?): T? = value

// expect: Source: Bound; selected source candidate is unchanged by native ABI support; FIR: Rejected(SOL2400).
func case61NoBackendDrivenOverload(value: String?): String? = selectNullable(value)

// Candidate ranking is a source decision. If the selected candidate has no native representation,
// compilation reports that admission failure rather than silently selecting another declaration.

// 62. Nullability diagnostics retain the source type position after normalization.

// expect: Source: Bound for the declaration; argument error points at `String?`; FIR not reached.
func case62DiagnosticOrigin(value: String?): String = echoNonNull<String>(value)

// The diagnostic must identify the nullable argument and formal type. It must not point only to a
// synthesized canonical name that users cannot find in their source.

// 63. A native rejection names the unsupported position and keeps the source span.

// expect: Source: Bound; FIR: Rejected(SOL2400 at nullable result type); no body emitted.
func case63NativeDiagnosticOrigin(): String? = echoNullable<String>(null)

// This is a backend admission failure, not a syntax error, an unresolved generic, or a nullable
// flow error. The source declaration remains valid under this specification.

// 64. A partial FIR body is never a successful outcome for a nullable specialization.

// expect: FIR: Rejected(SOL2402 if verifier receives an unsupported nullable node); no partial artifact.
func case64VerifierRejectsNullableNode(value: String?): String? = value?.toString()

// An independently constructed invalid FIR graph must fail verification even if source admission
// was accidentally bypassed. `SOL2402` identifies that verifier boundary, not source semantics.

// 65. Resource outcomes do not change nullable type meaning.

// expect: Source: Bound; FIR: Rejected(SOL2403 only when a documented specialization budget is exceeded).
func case65ResourceLimitIsSeparate(value: String?): String? = echoNullable<String>(value)

// A resource-limit diagnostic is not a nullable-type diagnostic. Raising a budget cannot make an
// unsupported representation valid, and a nullable rejection must not be mislabeled as exhaustion.

// 66. Native admission remains conservative for nullable value substitutions.

func <T> numericMaybe(value: T?): T? = value

// expect: Source: Bound with T = int; parameter and result int?; FIR: Rejected(SOL2400, nullable value layout).
func case66NullableNumeric(value: int?): int? = numericMaybe<int>(value)

// This case differs from nullable String at the machine representation boundary even though both
// carry the same source-level nullability meaning.

// 67. Non-null primitive generic support does not imply nullable primitive support.

func <T> numeric(value: T): T = value

// expect: Source: Bound with T = int; FIR outcome depends on the supported primitive operation subset.
func case67NonNullNumeric(value: int): int = numeric<int>(value)

// This non-null control case is intentionally distinct from case66. Adding the latter requires a
// documented nullable-value layout and verifier rule.

// 68. Nullable reference storage needs precise root behavior.

func retainMaybeString(value: String?): String? = value

// expect: Source: Bound; nullable reference can be null; FIR: Rejected(SOL2400, no admitted nullable root map).
func case68NullableReferenceRoot(value: String?): String? = retainMaybeString(value)

// A future collector must treat null as no object root and non-null String as a live reference.
// Nullable value payloads and reference-bearing structs need their own descriptor rules.

// 69. Nullable elements require an array descriptor that records null-capable slots.

func nullableArrayRoot(values: []String?): []String? = values

// expect: Source: Bound; FIR: Rejected(SOL2400, nullable element map and array operations unsupported).
func case69NullableElementRoot(values: []String?): []String? = nullableArrayRoot(values)

// Treating every slot as non-null can misidentify null as an object; treating all bits as scalar can
// miss live String references. The type descriptor must match the source element identity.

// 70. Nullability is part of a specialization key, not a separate late codegen option.

func <T> key(value: T): T = value

// expect: Source: Bound; key<T=String> differs from any future key<T=String?>; FIR: Rejected for nullable T.
func case70NullabilityInSpecializationKey(value: String?): String? = key<String?>(value)

// The ordered tuple of closed canonical type identities includes nullable wrappers. Cache reuse
// across nullable and non-null instances is invalid unless a future representation proof says so.

// 71. A null check on a nullable generic formal narrows only its value, not its type parameter.

func <T> genericLength(value: T?): int = {
    if value == null { 0 } else { 1 }
}

// expect: Source: Bound; else edge proves value is a present T; it does not bind or constrain T further.
// expect: FIR: Rejected(SOL2400); generic equality/null branching is not in the current native slice.

// Nullability flow is a value fact. It does not replace a declared type argument or add a trait bound.

// 72. A nullable wrapper does not satisfy an unrelated generic constraint.

func <T: std.hash.Hashable> hashValue(value: T): int = value.hashCode()

// expect: Source: Rejected; T = String? does not satisfy a non-null Hashable constraint by erasure.
func case72NullableConstraint(value: String?): int = hashValue<String?>(value)

// A constraint must state whether it permits nullable types. The compiler cannot infer conformance
// from the underlying non-null type while discarding the nullable constructor.

// 73. An array-of-nullable value does not satisfy a non-null element constraint by unwrapping.

func <T> acceptNonNullElements(values: []T): []T = values

// expect: Source: Rejected; []String? cannot convert to []String; element nullability is invariant here.
func case73ArrayElementInvariant(values: []String?): []String = acceptNonNullElements<String>(values)

// An implementation may add explicitly specified variance later, but it must never silently drop
// nullability to make an array call fit a current native signature.

// 74. An outer nullable array does not become a non-null empty array during lowering.

func defaultArray(): ([]int)? = null

// expect: Source: Bound; null remains null; FIR: Rejected(SOL2400).
func case74NoNullToEmptyRewrite(): ([]int)? = defaultArray()

// Replacing null by an empty allocation changes identity, allocation effects, and observable length.

// 75. Canonical printing may preserve either equivalent nullable-element spelling.

// expect: Formatter may print []String? or [](String?) consistently; both have equal type identity.
func case75EquivalentPrinting(value: []String?): [](String?) = value

// expect: Source: Bound; FIR: Rejected(SOL2400); formatter spelling cannot change nullable array identity.

// 76. Canonical printing must keep an outer nullable array visibly grouped.

// expect: Formatter prints a grouping form equivalent to ([]String)?; it must not print []String?.
func case76OuterPrinting(value: ([]String)?): ([]String)? = value

// The canonical source form may be normalized by the formatter, but it must remain unambiguous to
// a reader and round-trip to the same source type tree.

// 77. Nullability normalization happens before overload identity comparison.

func normalizedOverload(value: String?): int = 1

// expect: Source: Rejected declaration set if `String??` is introduced as a duplicate signature.
func duplicateNormalizedOverload(value: String??): int = 2

// Repeated wrappers cannot be used to declare a distinct overload or bypass duplicate-signature
// diagnostics. The source span of the duplicate declaration remains the diagnostic origin.

// 78. Nested nullable constructors are normalized independently at each position.

// expect: Source: Bound; T?? = T? and [](T??) = []T?; outer array remains non-null.
func case78IndependentNormalization(value: [][]String??): [][]String? = value

// The leaf's repeated wrapper collapses. The two array constructors remain, and neither gains an
// outer nullable layer.

// 79. Bottom on one branch and nullable value on another joins to nullable value.

func maybeAfterFailure(flag: bool, value: String?): String? = {
    if flag { value } else { fail() }
}

// expect: Source: Bound; reachable result type is String?; failing edge contributes no normal value.
// expect: FIR: Rejected(SOL2400); nullable return and bottom edge are not yet lowered natively.

// This result is nullable because the surviving value expression is nullable, not because the
// bottom expression contributes an implicit null.

// 80. A branch that throws on null can return the non-null underlying type.

func requirePresent(value: String?): String = {
    if value == null { fail() } else { value }
}

// expect: Source: Bound; null branch is bottom; else branch String; result String; FIR: Rejected(SOL2400).
// The compiler must not require a nullable result when every normal edge returns String.

// 81. Source smart casts are not serialized as machine layout claims.

func flowOnly(value: String?): int = {
    if value == null { 0 } else { value.length }
}

// expect: Source: Bound; flow fact on else edge is String; FIR: Rejected(SOL2400).
// A flow fact proves an operation safe at that point; it does not change the declared parameter ABI.

// 82. An analyzer must not refine a mutable property without a stability proof.

object MutableMaybe {
    var current: String?
}

// expect: Source: Deferred or Rejected unless property stability is proven across the member read.
func case82MutablePropertyFlow(): int = {
    if MutableMaybe.current == null { 0 } else { MutableMaybe.current.length }
}

// The second read may observe a different value from the first. A local snapshot can make the
// relationship explicit, but the snapshot remains nullable until proven present.

// 83. Safe-call argument evaluation is conditional and ordered after the receiver gate.

func indexForCall(): int = 0
func safeIndex(values: ([]String)?): String? = values?.get(indexForCall())

// expect: Source: Bound; indexForCall executes only on non-null values; result String?; FIR: Rejected(SOL2400).
// Receiver evaluation occurs once. Null receiver skips member lookup and argument evaluation.

// 84. Elvis fallback effects occur only on the null edge.

func fallbackWithEffect(): String throws Exception { throw Exception("fallback") }
func elvisWithEffect(value: String?): String throws Exception = value ?: fallbackWithEffect()

// expect: Source: Bound; checked Exception remains in the enclosing effect set; FIR: Rejected(SOL2400).
// If the left value is present, the fallback call has no runtime effect on that execution path.

// 85. Nullable normalization does not remove checked effects from a function type.

func <E: Throwable> effectFactory(): () -> () throws E = { throw RuntimeException("effect") }

// expect: Source: Bound only when E satisfies the declared checked-effect constraints; FIR: Rejected(SOL2400).
func case85EffectFunctionType(): () -> () throws Exception = effectFactory<Exception>()

// The function value's effect identity is separate from nullability of its result or closure object.

// 86. Nullable return inference requires a unique canonical result type.

func <T> chooseNull(): T? = null

// expect: Source: Deferred without explicit type argument or permitted expected-result evidence.
func case86NoDefaultTypeForNull() = chooseNull()

// No default type is selected just because the result is nullable. The compiler reports unresolved
// inference and emits no specialization record.

// 87. Explicit generic syntax closes only the named generic owner.

func <T> outer(value: T?): T? = inner<String>("fixed")
func <U> inner(value: U): U = value

// expect: Source: Bound; explicit U = String does not bind outer T; unused outer T is unresolved if required.
func case87OwnerIsolation(value: String?): String? = outer<String>(value)

// Owner and ordinal identity prevent an explicit argument at one nested call from rewriting a
// same-spelled parameter owned by another declaration.

// 88. A generic call cannot manufacture a non-null proof from its type argument.

func <T> claimNonNull(value: T?): T = value

// expect: Source: Rejected(SOL2243); body has a reachable nullable value on the return path.
// A future concrete call with T = String cannot repair the declaration-level source error.

// 89. Source rebinding and FIR projection are separate obligations.

func <T> addOne(value: T): T = value

// expect: Source: Bound does not prove each substituted operation is valid; native FIR must verify the closed body.
func case89ClosedBody(value: int): int = addOne<int>(value)

// This non-null control case may be admitted only if its complete body, operations, and ABI are
// supported. The rule cannot be generalized to a nullable instance by substituting pointer types.

// 90. Nullable specializations must be rejected before artifact emission when unsupported.

// expect: Source: Bound; FIR admission rejects the closed nullable signature; no native artifact or cache entry.
func case90NoPartialArtifact(value: String?): String? = echoNullable<String>(value)

// Release metadata must not count this case as native support. A frontend result, AST dump, or
// source-level inferred type is not a successful native compilation.

// 91. Boundary diagnostics distinguish source, verifier, and resource failures.

// expect: Source type error -> source diagnostic; valid unsupported nullable shape -> SOL2400.
// expect: malformed/unsupported constructed FIR -> SOL2402; bounded specialization exhaustion -> SOL2403.
func case91DiagnosticFamilies(value: String?): String? = echoNullable<String>(value)

// The same nullable shape may produce different diagnostics only when the failing phase and reason
// differ. Diagnostics must retain a stable source span and must not conflate the three conditions.

// 92. This matrix reserves native success until the verifier owns a complete nullable proof.

func <T> futureNullableSupport(value: T?): T? = value

// expect: Source: Bound with T = String; FIR: Rejected(SOL2400) in the current implementation.
func case92CurrentImplementationStatus(value: String?): String? =
    futureNullableSupport<String>(value)

// Planned work includes concrete reference/value layouts, null-aware GC descriptors, array element
// maps, safe-call and Elvis CFG, assertion failure behavior, flow proof transport, and AOT/ORC
// parity. Until each requirement is implemented and verified, the outcome above remains rejection.
