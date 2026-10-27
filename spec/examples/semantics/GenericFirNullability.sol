// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative source examples; nullable native FIR cases are intentionally rejected today.
module source.spec.examples.semantics.generic_fir_nullability

// Generic FIR nullability case matrix
// ===================================
//
// This file is a specification example set, not a claim that every declaration can currently be
// emitted as native code. Every case states the source-analysis outcome and the current FIR outcome.
// `Source: Bound` means the source type and semantic obligations are determinate. It does not imply
// that FIR has a machine representation. `FIR: Rejected` means the existing native verifier or
// admission boundary must reject the source shape without erasing nullability.
//
// The distinctions below are intentional and should survive CST, PSI, AST, source type identity,
// generic substitution, flow analysis, FIR construction, and diagnostics. Array spelling is
// particularly important: `[]String?` and `([]String)?` are different types.

// 1. A nullable scalar parameter and result remain nullable after substitution.

func <T> echoNullable(value: T?): T? = value

// expect: Source: Bound; inferred T = String; parameter = String?; result = String?; FIR: Rejected(SOL2400, nullable parameter/result representation).
func case01NullableParameterAndResult(): String? = echoNullable<String>(null)

// A nullable argument is accepted because the explicit substitution fixes its underlying type.
// The call is source-valid even though its specialized parameter and result are outside native FIR.

// 2. A non-null generic formal does not become nullable to accept null.

func <T> echoNonNull(value: T): T = value

// expect: Source: Rejected(SOL2243); explicit T = String; null is incompatible with String.
func case02RejectNullForNonNullFormal(): String = echoNonNull<String>(null)

// Explicit type arguments do not waive source conversions. The compiler cannot reinterpret the
// null literal as an arbitrary object reference because a pointer-shaped ABI happens to exist.

// 3. A non-null value may be wrapped in the declared nullable result.

func <T> widenToNullable(value: T): T? = value

// expect: Source: Bound; T = String; input = String; result = String?; FIR: Rejected(SOL2400).
func case03NonNullToNullableResult(value: String): String? = widenToNullable<String>(value)

// The conversion adds a nullable layer to the result. It does not change the argument's identity.

// 4. A nullable type argument is normalized when it is wrapped again.

func <T> nullableOfNullable(value: T?): T? = value

// expect: Source: Bound; T = String?; substituted T? = String?; normalization removes duplicate layer.
// expect: FIR: Rejected(SOL2400); no nullable String parameter or result layout is admitted.
func case04RepeatedNullableSubstitution(value: String?): String? =
    nullableOfNullable<String?>(value)

// The canonical identity contains one nullable constructor. The repeated source spelling is not
// preserved as a second runtime tag and must not create a second overload identity.

// 5. Repeated nullable suffixes on written scalar types have one identity.

// expect: Source: Bound; parameter identities of once and twice are equal; FIR: Rejected(SOL2400).
func case05RepeatedWrittenSuffix(once: String?, twice: String??): String? = {
    if once == null { twice } else { once }
}

// Both inputs can be null. A branch join remains nullable; branch order cannot make it non-null.

// 6. Nullable reference and nullable value types share source presence semantics.

// expect: Source: Bound; String? and int? admit null; native layouts are distinct; FIR: Rejected(SOL2400).
func case06NullableReferenceAndValue(reference: String?, number: int?): int? = {
    if reference == null { number } else { number }
}

// A nullable reference may use a null reference at runtime; nullable int needs a defined value
// representation. Neither layout follows from the source `?` marker alone.

// 7. An expected result can close a generic nullable result when the inference rules permit it.

func <T> noInputNullableResult(): T? = null

// expect: Source: Bound; expected result String? supplies T = String under the result-evidence rule.
// expect: FIR: Rejected(SOL2400); inference success does not admit nullable native representation.
func case07ExpectedTypeProvidesEvidence(): String? = noInputNullableResult<String>()

// This case spells the type argument so it remains deterministic even in an implementation that
// does not yet use expected-result evidence for this generic call.

// 8. A null-only call with no explicit argument or useful context is unresolved.

// expect: Source: Deferred; T has no unique evidence; no closed specialization key may be created.
func case08NullOnlyInferenceIsDeferred() = noInputNullableResult()

// A compiler must not choose Object, String, or a pointer type as a convenience default. A later
// independent statement cannot retroactively determine this expression's generic argument.

// 9. Multiple nullable candidates are not resolved by guessing the underlying type.

func <T> acceptMaybe(value: T?): int = 1
func <T> acceptMaybe(value: T): int = 2

// expect: Source: Deferred; a bare null may fit more than one generic candidate; no overload selected.
func case09NullableOverloadNeedsEvidence() = acceptMaybe(null)

// An implementation with additional applicable overloads must apply ordinary candidate-local
// inference and tie-breaking. Native support cannot be used as a hidden overload preference.

// 10. A concrete argument gives an underlying type for T? without permitting implicit unwrapping.

func <T> nullableInput(value: T?): T? = value

// expect: Source: Bound; explicit T = String; argument null is valid for String?; FIR: Rejected(SOL2400).
func case10ExplicitNullableInput(): String? = nullableInput<String>(null)

// Passing a variable of type String? is also valid. Reading a non-null value at runtime would not
// change the declared argument type or make the result non-null.

// 11. Arrays whose elements may be null are non-null containers.

func nullableStringElements(): []String? = ["Sol", null]

// expect: Source: Bound; array container non-null; element type String?; FIR: Rejected(SOL2400).
func case11NullableArrayElements(): []String? = nullableStringElements()

// The result can be indexed without proving the container non-null. The selected element itself is
// nullable and must be checked before a non-null String operation.

// 12. Parenthesizing the nullable element type spells the same array identity.

func parenthesizedNullableElements(): [](String?) = [null, "Sol"]

// expect: Source: Bound; [](String?) == []String?; FIR: Rejected(SOL2400, nullable element layout).
func case12ParenthesizedNullableElements(): []String? = parenthesizedNullableElements()

// Parentheses group the element type. They do not apply nullability to the array reference.

// 13. A nullable array has non-null elements after its container is proven present.

func nullableStringArray(): ([]String)? = null

// expect: Source: Bound; container nullable; element String non-null; FIR: Rejected(SOL2400).
func case13NullableArrayContainer(values: ([]String)?): int? = values?.length

// The safe call guards access to the array's length. Its lifted result is int?, not a nullable
// array and not an array of nullable integers.

// 14. Both the container and each element may independently be null.

func nullableArrayAndElements(): ([]String?)? = null

// expect: Source: Bound; outer nullable array; inner nullable String element; FIR: Rejected(SOL2400).
func case14BothArrayLayers(): String? = {
    val values: ([]String?)? = nullableArrayAndElements()
    values?.get(0)
}

// Safe access handles the outer array only. `get(0)` still returns String? because the element
// type remains nullable after the container gate succeeds.

// 15. The compact array suffix belongs to the element type.

func case15CompactArrayGrammar(values: []String?): String? = values[0]

// expect: Source: Bound; values is non-null []String?; index result is String?; FIR: Rejected(SOL2400).
// A formatter must not rewrite the formal type as ([]String)? because that moves the nullable layer.

// 16. Outer nullability is represented with grouping around the complete array type.

func case16GroupedNullableArray(values: ([]String)?): String? = {
    if values == null { null } else { values[0] }
}

// expect: Source: Bound; branch non-null proof applies to container; item is String; FIR: Rejected(SOL2400).
// This is not equivalent to case15: here the array itself may be absent.

// 17. Normalizing a repeated nullable suffix does not flatten array constructors.

// expect: Source: Bound; ([]String)?? == ([]String)?; outer wrapper is retained; FIR: Rejected(SOL2400).
func case17RepeatedContainerSuffix(values: ([]String)??): int? = values?.length

// Only adjacent nullable layers normalize. Array nesting and the location of the wrapper are
// preserved, including any nullable element constructor inside the array.

// 18. Generic element substitution can make a non-null array's element nullable.

func <T> identityArray(values: []T): []T = values

// expect: Source: Bound; T = String?; []T substitutes to [](String?) == []String?; FIR: Rejected(SOL2400).
func case18GenericArrayOfNullable(values: []String?): []String? =
    identityArray<String?>(values)

// The container remains non-null. Substitution rewrites only the element parameter occurrence.

// 19. A nullable array element formal is separate from an outer nullable container formal.

func <T> maybeElements(values: []T?): []T? = values

// expect: Source: Bound; T = String; formal/result are []String?; FIR: Rejected(SOL2400).
func case19GenericNullableElements(values: []String?): []String? =
    maybeElements<String>(values)

// Here the source declaration owns the nullable element constructor. It is not inferred by making
// the array container nullable.

// 20. An outer nullable generic array remains outer after substitution.

func <T> maybeArray(values: ([]T)?): ([]T)? = values

// expect: Source: Bound; T = String; result is ([]String)?; FIR: Rejected(SOL2400).
func case20GenericNullableContainer(values: ([]String)?): ([]String)? =
    maybeArray<String>(values)

// Replacing T changes the element type only. It does not move the outer wrapper inside `[]`.

// 21. Nested arrays preserve each constructor during nullable substitution.

func <T> nested(values: [][]T): [][]T = values

// expect: Source: Bound; T = String?; result = [][]String?; nullable leaves; FIR: Rejected(SOL2400).
func case21NestedNullableLeaves(values: [][]String?): [][]String? =
    nested<String?>(values)

// Both array containers are non-null in this example. The leaf value is nullable; neither array
// can be null without an additional grouped nullable wrapper.

// 22. An explicitly nullable outer nested container adds a separate layer.

// expect: Source: Bound; outer container nullable; nested element array non-null; leaf String?; FIR: Rejected(SOL2400).
func case22NestedOuterNullability(values: ([][]String?)?): int? = values?.length

// A safe call checks only the outermost container. It says nothing about inner arrays or leaves.

// 23. A local null check refines a stable nullable reference in its dominated branch.

func case23StableReferenceFlow(value: String?): int = {
    if value == null { 0 } else { value.length }
}

// expect: Source: Bound; else-branch value has String; join result int; FIR: Rejected(SOL2400, nullable parameter).
// The flow fact ends at its valid control-flow region and does not rewrite the declaration type.

// 24. A nullable array element check refines the selected local item only.

func case24NullableItemFlow(values: []String?): int = {
    var count = 0
    for val item in values {
        if item != null { count += item.length }
    }
    count
}

// expect: Source: Bound; item is String? before check and String in true branch; FIR: Rejected(SOL2400).
// Iteration does not unwrap elements. The mutable array's later reads retain nullable element type.

// 25. Checking one array slot does not refine another slot.

func case25SlotFactsAreDistinct(values: []String?): int? = {
    if values[0] == null { null } else { values[1].length }
}

// expect: Source: Rejected(SOL2243); proof for slot zero does not prove slot one is non-null.
// An alias or intervening mutation makes even repeated reads of one slot require a stability proof.

// 26. A stable local copy can retain a proven non-null value.

func case26StableCopy(value: String?): int = {
    if value == null { 0 } else {
        val stable = value
        stable.length
    }
}

// expect: Source: Bound; stable has String in the else branch; FIR: Rejected(SOL2400).
// Captures, mutable aliases, or concurrent writes require the corresponding flow invalidation rule.

// 27. A generic body keeps T distinct from T? while checking a branch.

func <T> returnIfPresent(value: T?): T? = {
    if value == null { null } else { value }
}

// expect: Source: Bound; branch fact narrows the value to T; result remains T?; FIR: Rejected(SOL2400).
// A future native instance must bind this body against its closed substitution and representation.

// 28. A generic non-null result cannot be returned from a null branch.

func <T> invalidUnwrap(value: T?): T = {
    if value == null { null } else { value }
}

// expect: Source: Rejected(SOL2243); null branch cannot satisfy non-null result T.
// The fact on the else edge does not justify the distinct reachable null edge.

// 29. A safe call skips the selected member path when its receiver is null.

func safeLength(value: String?): int? = value?.length

// expect: Source: Bound; result int?; receiver evaluated once; FIR: Rejected(SOL2400, safe-call/null gate).
// Member access and any safe-call arguments execute only on the non-null edge.

// 30. Safe-call lifting is idempotent for a member that already returns nullable.

object NullableBox {
    val content: String?
}

func safeNullableMember(box: NullableBox?): String? = box?.content

// expect: Source: Bound; lifted nullable member normalizes to String?; FIR: Rejected(SOL2400).
// Safe access does not produce String?? and does not prove `box` non-null after this expression.

// 31. A safe call is not a writable l-value or a ref/out address.

func consumeRef(ref value: String): () = ()

// expect: Source: Rejected; `ref box?.content` is not an addressable variable.
func case31SafeCallCannotPassByRef(box: NullableBox?): () = consumeRef(ref box?.content)

// An implementation must diagnose the invalid passing mode during source binding, before FIR ABI
// selection. It must not evaluate the receiver twice to synthesize an address.

// 32. The null assertion produces a non-null value on the normal path.

func assertedLength(value: String?): int = value!!.length

// expect: Source: Bound; value!! has String on normal path; null edge throws; FIR: Rejected(SOL2400).
// `!!` is an explicit assertion, not a conversion from an arbitrary generic type to another type.

// 33. Assertion on a nullable generic parameter keeps the substituted underlying identity.

func <T> assertPresent(value: T?): T = value!!

// expect: Source: Bound; normal result T; assertion failure edge follows runtime null-assertion contract.
// expect: FIR: Rejected(SOL2400); generic nullable input and assertion lowering are not admitted.
func case33GenericAssertion(value: String?): String = assertPresent<String>(value)

// 34. Elvis evaluates its left operand once and uses the fallback only on null.

func elvisLength(value: String?): int = (value?.length) ?: 0

// expect: Source: Bound; left int?; result int; fallback evaluated only when left is null.
// expect: FIR: Rejected(SOL2400); safe-call and Elvis CFG are outside the current native subset.

// 35. Elvis with a non-null right operand unwraps only the selected nullable value.

func elvisString(value: String?): String = value ?: "fallback"

// expect: Source: Bound; result String; value is non-null on left-selected edge; FIR: Rejected(SOL2400).
// The source declaration of `value` remains String? outside this expression's flow edge.

// 36. A bottom right operand contributes no normal value to an Elvis result.

func fail(): ! { throw RuntimeException("missing value") }
func required(value: String?): String = value ?: fail()

// expect: Source: Bound; result String; null edge has no normal continuation; FIR: Rejected(SOL2400).
// There is no fabricated empty String on the bottom edge.

// 37. The bottom type has no values and is distinct from an error placeholder.

func neverNormally(): ! { throw RuntimeException("stop") }

// expect: Source: Bound; normal successor is unreachable; ! is not a storable generic argument.
// expect: FIR: Rejected(SOL2400); bottom control-flow lowering is not admitted for this specialization.

// 38. Nullable bottom denotes null as its only value under the type contract.

func nullBottom(): !? = null

// expect: Source: Bound; result type !?; null is its sole value; FIR: Rejected(SOL2400).
// Normalization makes !?? identical to !? and does not make it an unconstrained type variable.

// 39. Bottom does not make a non-null generic input inferable.

func <T> requireValue(value: T): T = value

// expect: Source: Rejected(SOL2243); null cannot satisfy non-null T even when a later edge is bottom.
func case39BottomDoesNotChangeFormal(): String = requireValue<String>(null ?: fail())

// The Elvis expression itself has type String, but its null branch does not alter the generic
// formal's conversion rules for other call sites.

// 40. Nullable exception references must be proven present before throwing.

func throwMaybe(error: Throwable?): ! = {
    if error == null { throw RuntimeException("no error") }
    throw error!!
}

// expect: Source: Bound; Throwable? is not itself a thrown value; assertion gives Throwable.
// expect: FIR: Rejected(SOL2400); source exception and nullable assertion lowering are incomplete.

// 41. Nullable effects do not satisfy a non-null Throwable constraint.

// expect: Source: Rejected; a nullable Throwable? effect is not a declared non-null thrown type.
func case41NullableThrowType(error: Throwable?): () throws Throwable? = throw error

// Nullability never removes checked-effect obligations. A null value is not an exception object.

// 42. Substituting a checked Throwable subtype retains its declared effect.

func <E: Throwable> raise(value: E): ! throws E { throw value }

// expect: Source: Bound; E = Exception keeps checked effect Exception; FIR: Rejected(SOL2400).
func case42CheckedGenericEffect(error: Exception): ! throws Exception = raise<Exception>(error)

// Wrapping E in E? would no longer meet the non-null Throwable bound and cannot erase the effect.

// 43. Nullable function results preserve their constructor through function types.

func <T> nullableProducer(): () -> T? = { null }

// expect: Source: Bound with T = String; function result String?; FIR: Rejected(SOL2400, function value).
func case43NullableFunctionResult(): () -> String? = nullableProducer<String>()

// A future function-value ABI must describe nullable returns independently of closure allocation.

// 44. Nullable tuple positions preserve element identity.

func <T> pair(value: T?): (T?, int) = (value, 1)

// expect: Source: Bound with T = String; tuple element zero String?; FIR: Rejected(SOL2400, nullable tuple field).
func case44NullableTupleElement(value: String?): (String?, int) = pair<String>(value)

// Nullability of one tuple field does not make the tuple container itself nullable.

// 45. A nullable tuple container requires a separate outer wrapper.

// expect: Source: Bound; tuple nullable, field String non-null; FIR: Rejected(SOL2400).
func case45NullableTupleContainer(value: ((String, int))?): int? = value?.second

// Grouping around the tuple preserves the distinction between a null tuple and a present tuple
// whose first component may be null.


// See [GenericFirNullabilityFlow.sol](GenericFirNullabilityFlow.sol) for the remaining contract.
