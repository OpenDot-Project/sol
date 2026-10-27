// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: generic substitution must close over the native ABI before FIR verification.
module spec.examples.generic_fir_primitive_abi
import std.stdio
import std.collections

// Status: the expected outcomes describe the bounded verified-FIR/native slice. A source-level
// binding alone is not a native success. The companion guide defines the boundary and records the
// distinctions between a permitted type argument and operations admitted by the current backend.

func <T> identity(value: T): T = value
func <T> first(values: []T): T = values[0]
func <T> size(values: []T): int = values.length
func <T> show(value: T) { println(value) }
func <T> echoArray(values: []T): []T = values

// Primitive scalar arguments: each call selects a closed function instance.
// expect: verified FIR with one instance each; lowering succeeds for all listed primitive layouts.
func scalarBool(): bool = identity(true)
func scalarSbyte(): sbyte = identity(-7)
func scalarShort(): short = identity(31)
func scalarInt(): int = identity(41)
func scalarLong(): long = identity(9000000000)
func scalarByte(): byte = identity(200)
func scalarUint(): uint = identity(300)
func scalarUlong(): ulong = identity(9000000000)
func scalarSingle(): single = identity(1.25)
func scalarDouble(): double = identity(2.5)

// A source-level specialization is an exact type identity. It never changes the language's
// ordinary signedness, width, nullability, or floating-point conversion rules.
// expect: `identity<sbyte>` and `identity<byte>` are distinct instances.
func signedAndUnsignedAreDistinct(): int {
    val signed: sbyte = identity<sbyte>(-1)
    val unsigned: byte = identity<byte>(255)
    println(signed, unsigned)
    0
}

// expect: each closed type tuple is interned once, regardless of how often its call appears.
func repeatedSameTypeCalls(): int {
    val firstValue = identity<int>(1)
    val secondValue = identity(2)
    println(firstValue, secondValue)
    firstValue + secondValue
}

// expect: `identity<int>` and `identity<long>` have different specialization arguments, ids,
// signatures, and call targets; neither body shares a mutable FIR slot table with the other.
func sameDeclarationDifferentTypes(): int {
    val narrow = identity<int>(1)
    val wide = identity<long>(2)
    println(narrow, wide)
    narrow + 1
}

// The parameter type, return type, and expression result all use the same closed substitution.
// expect: verified FIR parameters and result are `int`; no open `T` remains in the instance.
func explicitArgumentClosesSignature(): int = identity<int>(17)

// expect: inferred `T` is `String`; the function instance uses the managed String pointer ABI.
func scalarString(): String = identity("sol")

// expect: the String instance is distinct from every primitive instance.
func stringLength(): int = identity("native string").length

// Scalar operations are checked against the specialized source type and FIR operation contract.
// expect: `int` arithmetic is verified and emitted using its selected LLVM integer width.
func intBodyOperation(value: int): int = add<int>(value, 3)
func <T> add(left: T, right: T): T = left + right

// expect: `single` and `double` arithmetic retain their own native type; no conversion is added
// merely because both instances originate from the same generic source declaration.
func singleBodyOperation(value: single): single = add<single>(value, 0.5)
func doubleBodyOperation(value: double): double = add<double>(value, 0.5)

// A generic body may create locals; every local declaration and read is substituted before FIR.
// expect: `saved` and the returned expression are `long`, not the declaration-owned parameter T.
func <T> keepInLocal(value: T): T {
    val saved: T = value
    saved
}
func localSpecialization(): long = keepInLocal<long>(5)

// Branches and returns use the same closed result type on every reachable path.
// expect: the Boolean test and both integer result paths verify as one `int` body.
func <T> choose(flag: bool, left: T, right: T): T {
    if flag { return left }
    right
}
func branchSpecialization(): int = choose<int>(true, 4, 9)

// Generic specialization composes at a generic-to-generic edge.
// expect: `forward<int>` contains a call to the unique `identity<int>` instance.
func <T> forward(value: T): T = identity<T>(value)
func forwardedInt(): int = forward(6)

// expect: nested type arguments are substituted in the caller context before the callee key is
// formed; an open caller parameter is never stored as a native instance argument.
func <T> forwardTwice(value: T): T = forward<T>(identity<T>(value))
func nestedForwarding(): String = forwardTwice("nested")

// Nested generic calls with different parameter order keep declaration-owned order.
// expect: `pair<int, String>` and `swap<String, int>` remain distinct keys.
func <A, B> pair(first: A, second: B): A = first
func <B, A> reversePair(first: A, second: B): A = pair<A, B>(first, second)
func genericOrderIsStable(): String = reversePair<int, String>("x", 1)

// Array type arguments use the existing one-dimensional managed-array representation.
// expect: `identity<[]int>` has a pointer parameter/result whose element representation is i32.
func intArrayIdentity(values: []int): []int = identity<[]int>(values)

// expect: `first<int>` returns an integer value loaded through the array element operation.
func firstInt(values: []int): int = first(values)

// expect: the String-array element pointer is protected by the existing managed array/root ABI.
func firstString(values: []String): String = first<String>(values)

// expect: array length is `int`; the generic element type does not change the length ABI.
func intArraySize(values: []int): int = size(values)
func stringArraySize(values: []String): int = size(values)

// expect: arrays of each supported native scalar preserve the element identity in the closed
// instance key; this does not imply that every operation on every scalar is available.
func boolArrayIdentity(values: []bool): []bool = echoArray<bool>(values)
func sbyteArrayIdentity(values: []sbyte): []sbyte = echoArray<sbyte>(values)
func shortArrayIdentity(values: []short): []short = echoArray<short>(values)
func intArrayIdentityAgain(values: []int): []int = echoArray<int>(values)
func longArrayIdentity(values: []long): []long = echoArray<long>(values)
func byteArrayIdentity(values: []byte): []byte = echoArray<byte>(values)
func uintArrayIdentity(values: []uint): []uint = echoArray<uint>(values)
func ulongArrayIdentity(values: []ulong): []ulong = echoArray<ulong>(values)
func singleArrayIdentity(values: []single): []single = echoArray<single>(values)
func doubleArrayIdentity(values: []double): []double = echoArray<double>(values)
func stringArrayIdentity(values: []String): []String = echoArray<String>(values)

// `show<T>` delegates to the existing scalar/String print adapter only after T is closed.
// expect: bool and integer/float/String substitutions satisfy the adapter's scalar contract.
func showScalars(): () {
    show<bool>(true)
    show<sbyte>(-1)
    show<short>(-2)
    show<int>(-3)
    show<long>(-4)
    show<byte>(5)
    show<uint>(6)
    show<ulong>(7)
    show<single>(1.5)
    show<double>(2.5)
    show<String>("shown")
}

// Numeric input conversion is a source-analysis decision. The ABI admission stage does not invent
// a conversion; any source-accepted conversion must have an explicit typed FIR lowering before the
// call can verify. These examples pin the distinction for future conversion work.
// expect: the exact-typed call is eligible for FIR; a non-exact converted call remains rejected
// until the conversion node is represented and independently verified.
func exactNumericArgument(): long = identity<long>(9)
func convertedNumericArgument(value: int): long = identity<long>(value)

// Generic argument count and closedness are required before instance creation.
// expect: source diagnostics, no FirFunction and no LLVM declaration.
func missingArgument(): int = identity()
func tooManyArguments(): int = identity<int, long>(1)

// Unused parameters are not assigned a default type by specialization.
// expect: source inference reports missing evidence; an explicit type argument is a closed call.
func <T> noEvidence(): T = unreachable
func explicitNoEvidence(): int = noEvidence<int>()
func inferredNoEvidence(): int = noEvidence()

// Parameter passing modes remain part of the specialized call signature. Substitution changes the
// parameter's type identity only; it does not turn ref/out into a value parameter or add a copy.
// expect: all FIR slots use `int`; the out local is definitely assigned on normal return.
func <T> writeOut(out target: T, value: T) { target = value }
func <T> swap(ref left: T, ref right: T) {
    val saved: T = left
    left = right
    right = saved
}
func genericOutCall(): int {
    var result: int
    writeOut<int>(out result, 12)
    result
}
func genericRefCall(): int {
    var left = 3
    var right = 8
    swap<int>(ref left, ref right)
    left
}

// expect: the String specialization preserves managed-reference semantics and does not use the
// integer path merely because the parameter ordinal and function name are the same.
func genericStringRef(ref left: String, ref right: String): String {
    swap<String>(ref left, ref right)
    left
}

// The parameter map is ordered by the declaration, not by argument name, source call spelling,
// source file order, or the order in which the worklist discovers a body.
// expect: exact parameter/result vectors are (sbyte, String) and (String, sbyte), respectively.
func <A, B> chooseFirst(first: A, second: B): A = first
func <A, B> chooseSecond(first: A, second: B): B = second
func orderedPairCalls(): int {
    val first = chooseFirst<sbyte, String>(1, "x")
    val second = chooseSecond<String, int>("y", 2)
    first + second
}

// Result-context evidence participates before the instance is reserved. Once selected, the
// resulting instance has the same canonical key as an explicit or argument-inferred call.
// expect: each function call returns the exact declared result type and reuses one closed instance.
func <T> emptyArray(): []T = []
func expectedArray(): []int = emptyArray()
func explicitArray(): []int = emptyArray<int>()
func argumentInt(): int = identity(1)

// An expected type does not convert a generic result after instance selection. It contributes an
// inference bound only when the source inference contract permits it; the final call remains exact.
// expect: the String result is a String instance. A non-String nominal expectation is not boxed.
func expectedString(): String = identity("expected")
class Record {}
func noNominalBoxing(value: Record): Record = identity<Record>(value)

// Array specialization composes through the item and element expressions without erasing GC
// metadata. The source iteration constructs one element slot per body; the target instance stays
// monomorphic for the entire function body.
// expect: []int, []String, and scalar item types are visible in FIR; nested arrays are rejected.
func <T> total(values: []T): int {
    var count = 0
    for val item in values { count += 1 }
    count
}
func totalInts(values: []int): int = total<int>(values)
func totalStrings(values: []String): int = total<String>(values)

// A supported `[]T` ABI does not imply an unrestricted `T[]` layout. Element pointer maps and
// scalar element widths are checked by the closed native type validator for every specialization.
// expect: the one-dimensional arrays below are accepted; rank, nesting, and ref-like element
// layouts outside the stated support matrix are rejected before LLVM module construction.
func arraysAreDistinct(values: []int, labels: []String): int {
    val item = first<int>(values)
    val label = first<String>(labels)
    println(item, label)
    item
}

// Type argument admission and body operation admission are separate checks. `identity<bool>` is
// a valid representation case; arithmetic on bool is still invalid. The same rule applies to
// String, whose managed pointer representation does not define numeric addition or bit operations.
// expect: identity<bool> and identity<String> are valid; plus<bool> and plus<String> are not
// legalized by monomorphization or a shared machine-level pointer/integer slot.
func booleanIdentity(): bool = identity<bool>(false)
func stringIdentity(): String = identity<String>("same source declaration")
func booleanAdditionRejected(): bool = plus<bool>(true, false)
func stringAdditionRejected(): String = plus<String>("left", "right")

// Exact source result and call-edge checks are preserved when a generic function invokes another
// generic function. If the inner instance cannot be verified, the outer instance is not emitted.
// expect: `forward<int>` and `identity<int>` form a closed edge; `forward<String>` uses a distinct
// edge and does not point at the integer function merely because the target origin is identical.
func <T> returnThrough(value: T): T {
    val inner: T = identity<T>(value)
    inner
}
func returnIntThrough(value: int): int = returnThrough<int>(value)
func returnStringThrough(value: String): String = returnThrough<String>(value)

// Return types and checked effects are both substituted. Nominal exception layouts remain outside
// the initial generic native ABI even if source effect analysis can resolve the exception hierarchy.
// expect: unsupported nominal effect specialization is rejected; effect identity is never dropped.
class StorageFailure: Exception {}
func <T> load(value: T): T throws StorageFailure = value
func nominalEffectRejected(value: int): int throws StorageFailure = load<int>(value)

// Output representation selection is deterministic and uses the source type identity. A pointer
// value is never reinterpreted as i64, and unsigned values do not select a signed format operation.
// expect: each call-site target and result type agrees exactly with its specialized function ABI.
func printSigned8(value: sbyte): () { show<sbyte>(value) }
func printSigned16(value: short): () { show<short>(value) }
func printSigned32(value: int): () { show<int>(value) }
func printSigned64(value: long): () { show<long>(value) }
func printUnsigned8(value: byte): () { show<byte>(value) }
func printUnsigned32(value: uint): () { show<uint>(value) }
func printUnsigned64(value: ulong): () { show<ulong>(value) }
func printFloat32(value: single): () { show<single>(value) }
func printFloat64(value: double): () { show<double>(value) }
func printBoolean(value: bool): () { show<bool>(value) }
func printString(value: String): () { show<String>(value) }

// Empty and bottom results are not aliases for an arbitrary generic storage type. A source body
// that completes without a value can only satisfy the unit result; a non-returning body can
// satisfy the caller's expected type through the bottom rule without inventing an ABI value.
// expect: unit is not storable as a parameter/local; bottom produces no return slot or fake value.
func <T> fail(): T = unreachable
func bottomResult(): int = fail<int>()
func bottomStringResult(): String = fail<String>()

// Nullable wrappers alter the root representation and GC null-state contract. They remain outside
// this ABI matrix even when their inner argument is String or a scalar type admitted on its own.
// expect: nullable generic instances are rejected until FIR and Yellow Tea agree on their layout.
func nullableString(value: String?): String? = identity<String?>(value)
func nullableInt(value: int?): int? = identity<int?>(value)
func nullableArray(values: []int?): []int? = identity<[]int?>(values)
func nullableReferenceArray(values: []String?): []String? = identity<[]String?>(values)

// Class-shaped reference types do not share String's runtime descriptor or ownership contract.
// expect: Object, Throwable, Exception, and an application class are not accepted type arguments.
func objectRejected(value: Object): Object = identity<Object>(value)
func throwableRejected(value: Throwable): Throwable = identity<Throwable>(value)
func exceptionRejected(value: Exception): Exception = identity<Exception>(value)
func applicationClassRejected(value: Record): Record = identity<Record>(value)

// An array's direct element type must itself be admitted. The compiler may not infer a missing
// pointer-map description by recursively treating all nominal values as managed references.
// expect: []String and primitive arrays are accepted; [][]T and arrays of nullable/ref-like values
// are rejected without calling into the runtime array allocator.
func nestedPrimitiveRejected(values: [][]int): [][]int = identity<[][]int>(values)
func nestedStringRejected(values: [][]String): [][]String = identity<[][]String>(values)
func refLikeArrayRejectedAgain(values: []Window): []Window = identity<[]Window>(values)

// Same-type constraints still require an implementation proof. Source constraints do not
// introduce an ABI adapter, and an unconstrained body cannot assume methods of an arbitrary T.
// expect: the source candidate must be uniquely applicable and its closed body must pass FIR checks.
func <T> copyValue(value: T): T = value
func constrainedCopy(): int = copyValue<int>(4)

// Generic instance identity is independent of overload discovery order. For a fixed input set,
// sorting source declarations differently must not change the selected type tuple or body content.
// expect: repeated String requests point to one instance; the integer call remains separate.
func deterministicFirst(): String = identity<String>("a")
func deterministicSecond(): int = identity<int>(2)
func deterministicThird(): String = identity<String>("b")

// Diagnostic spans remain call-site spans for unsupported ABI substitutions. A declaration can
// be source-valid while one particular closed instance is outside the current native boundary.
// expect: diagnostics identify the failing call and include its concrete type argument, not a
// generic `T` that forces the user to locate an inferred substitution manually.
func halfCallSite(value: half): half = identity<half>(value)
func quadCallSite(value: quad): quad = identity<quad>(value)
func nestedArrayCallSite(value: [][]int): [][]int = identity<[][]int>(value)

// Native type support may widen by a separately verified ABI addition. Adding a type to the
// language's built-in type list does not by itself extend this accepted matrix.
// expect: these future types require dedicated representation, constant, call, GC, and runtime
// tests before they may become valid generic specialization arguments.
func futureHalf(value: half): half = identity<half>(value)
func futureQuad(value: quad): quad = identity<quad>(value)

// Main remains a nongeneric runtime boundary. Calls from it use closed internal instances without
// changing the process entry signature, command-line array, or SolRT startup/shutdown sequence.
// expect: `main` has no specialization arguments; generic callees have ordinary native symbols.
func genericCallsFromEntry(args: []String) {
    println(identity<int>(7))
    println(identity<String>("ready"))
}

// Closed identity is declaration plus ordered arguments, not an overload name or display string.
// expect: duplicate same-key requests reuse one instance; source order does not change its ABI.
func orderA(): String = identity<String>("a")
func orderB(): int = identity<int>(2)
func orderC(): String = identity<String>("c")

// Type arguments with layouts not admitted by verified FIR fail closed even when source analysis
// can bind them. They must not be represented as pointer-shaped values by default.
// expect: SOL2400 for half and quad instances; no verified FIR.
func halfRejected(value: half): half = identity<half>(value)
func quadRejected(value: quad): quad = identity<quad>(value)

// Nominal types other than the specifically supported standard String ABI are not admitted merely
// because a generic parameter can name them in source analysis.
class Payload {}
func nominalRejected(value: Payload): Payload = identity<Payload>(value)

// Generic owner layout and receiver state are separate from free-function substitution.
// expect: class/member/object generic specialization is outside this matrix and rejected by FIR.
class Box<T> {
    val item: T
    constructor(item: T) { this.item = item }
    func get(): T = item
}
func genericOwnerRejected(box: Box<int>): int = box.get()

// Ref-like values cannot be silently boxed or reinterpreted as the generic pointer ABI.
// expect: a ref struct substitution and every array containing it are rejected.
ref struct Window {}
func refLikeRejected(value: Window): Window = identity<Window>(value)
func refLikeArrayRejected(values: []Window): []Window = echoArray<Window>(values)

// Nullable and nested arrays require layouts and GC maps outside this first native admission set.
// expect: source analysis may resolve the types; FIR reports unsupported type admission.
func nullableRejected(value: String?): String? = identity<String?>(value)
func nestedArrayRejected(values: [][]int): [][]int = identity<[][]int>(values)
func nullableArrayElementRejected(values: []String?): []String? = echoArray<String?>(values)

// Function and tuple shapes are not treated as pointers or erased to Object.
// expect: callbacks, closures, and nonempty tuple type arguments remain unsupported in FIR.
func callbackRejected(callback: (int) -> int): (int) -> int = identity(callback)
func tupleRejected(value: (int, String)): (int, String) = identity(value)

// Array nesting is deliberately rejected even if the immediate outer array is representable.
// expect: unsupported inner pointer map prevents both input and result from reaching LLVM.
func nestedArrayLengthRejected(values: [][]String): int = size(values)
func nestedArrayReadRejected(values: [][]int): []int = first(values)

// Array element access is verified only after substitution establishes a permitted element type.
// expect: `first<int>` and `first<String>` are admitted; generic owner substitutions are not.
func useFirstInt(values: []int): int = first<int>(values)
func useFirstString(values: []String): String = first<String>(values)

// The source type may be valid while a body operation is not valid for all T. Specialization must
// verify the concrete operation and reject an instance whose body operation has no FIR rule.
// expect: int and floating addition are eligible; bool and String addition are not made valid by
// using a generic declaration.
func <T> plus(left: T, right: T): T = left + right
func plusInt(): int = plus<int>(2, 3)
func plusSingle(): single = plus<single>(1.0, 2.0)
func plusBooleanRejected(): bool = plus<bool>(true, false)
func plusStringRejected(): String = plus<String>("a", "b")

// Body locals, array values, and return expressions are checked against the closed instance.
// expect: type mismatch after substitution is rejected; a failed body leaves no partial function.
func <T> badLocal(value: T): int {
    val local: int = value
    local
}
func badLocalString(): int = badLocal<String>("not an integer")

// Generic bodies that use a permitted operation remain valid only for substitutions that satisfy
// the operation's own typed-FIR rule. Specialization does not erase operator resolution metadata.
// expect: an unsupported overload, checked effect, nullable branch, or user conversion fails the
// selected instance; none may be replaced by a raw LLVM instruction with the same spelling.
func <T> negate(value: T): T = -value
func negateInt(value: int): int = negate<int>(value)
func negateString(value: String): String = negate<String>(value)

// An explicit type argument fixes the specialization key before final argument checking. It does
// not force the source argument to be bit-cast, reinterpreted, boxed, or accepted as an exact match.
// expect: valid conversion is represented in typed FIR or rejected; no target signature mismatch
// can pass the independent verifier by trusting the source candidate's prior ranking alone.
func exactSbyte(value: sbyte): sbyte = identity<sbyte>(value)
func exactByte(value: byte): byte = identity<byte>(value)
func exactSingle(value: single): single = identity<single>(value)
func exactDouble(value: double): double = identity<double>(value)

// Every accepted machine value has a defined argument position and result type. The frontend's
// parameter names and source declaration spelling do not determine ABI order after named mapping.
// expect: a named call is lowered using selected parameter indices and keeps source evaluation
// order; the generic instance still records `(int, String)` in declaration order.
func <A, B> selectPair(first: A, second: B): A = first
func namedArguments(): int = selectPair<int, String>(second = "later", first = 1)

// Vararg expansion and defaults are separate call-lowering features. A closed generic argument
// tuple does not authorize missing or expanded ABI slots in this initial subset.
// expect: if source binding accepts these shapes, the FIR admission check emits a targeted
// unsupported diagnostic until the corresponding parameter mapping has a native lowering rule.
func <T> defaulted(value: T = 0): T = value
func usesDefault(): int = defaulted<int>()
func <T> collect(vararg values: T): int = values.length
func usesVararg(): int = collect<int>(1, 2, 3)

// Native ABI admission is performed for every argument in an ordered type tuple. If any one
// argument is unsupported, no partial instance key is published and no earlier supported
// argument is treated as a complete specialization.
// expect: `combine<int, half>` has no FIR body even though `int` is independently supported.
func <A, B> combine(left: A, right: B): A = left
func partialTupleRejected(left: int, right: half): int = combine<int, half>(left, right)

// Parameter constraints are checked on the closed arguments before any function symbol is added.
// The source constraint system remains authoritative; the backend does not infer a constraint
// from a convenient machine representation or from a similarly named runtime helper.
// expect: unsatisfied and indeterminate constraints fail; no symbol is emitted for either case.
interface Numeric {}
struct NumberValue: Numeric {}
func <T: Numeric> constrained(value: T): T = value
func constraintEligible(value: NumberValue): NumberValue = constrained<NumberValue>(value)
func constraintMissing(value: String): String = constrained<String>(value)

// Checked effects remain part of the selected function identity. The native verifier compares the
// exact specialized effect set against source binding, caller budget, and the target FIR metadata.
// expect: no effect list is removed while cloning, and an unhandled checked effect blocks the root.
class NativeEffect: Exception {}
func <T> mayThrow(value: T): T throws NativeEffect = value
func effectBudgeted(value: int): int throws NativeEffect = mayThrow<int>(value)
func effectBudgetMissing(value: int): int = mayThrow<int>(value)

// A generic declaration with no reachable closed call creates no native instance. This does not
// mean its source body is skipped by source diagnostics or that a later call may reuse a stale
// instance from a different compilation/session.
// expect: source analysis checks the declaration; the FIR instance table contains only reachable
// closed calls and is owned by this compilation's TypeSpecialization workspace.
func <T> unusedGeneric(value: T): T = value
func onlyOrdinaryFunction(): int = 1

// Each native compilation owns its specialization table. Two workspace-local type ids that happen
// to have the same integer value are not cross-compilation identity and cannot share cached bodies.
// expect: repeated builds are deterministic within a workspace, while artifacts never persist an
// unvalidated in-memory `ResolvedType` id as a reusable global cache key.
func workspaceLocalA(): int = identity<int>(1)
func workspaceLocalB(): int = identity<int>(2)

// A repeated call nested through an ordinary function still points to the same canonical generic
// target. Reachability is transitive, and adding a nongeneric wrapper does not duplicate the body.
// expect: one `identity<String>` specialization is referenced by both wrappers.
func stringWrapperA(value: String): String = identity<String>(value)
func stringWrapperB(value: String): String = identity<String>(value)

// Calls inside the same generic body may recurse when their substituted argument tuple is equal.
// The recursive edge resolves to the reserved current instance; it does not create a new body on
// each traversal. Expanding substitutions remain subject to the fixed graph and depth budgets.
// expect: equal-key recursion terminates specialization discovery; a growing type chain fails with
// a resource diagnostic before LLVM generation.
func <T> recursiveIdentity(value: T, depth: int): T {
    if depth <= 0 { return value }
    recursiveIdentity<T>(value, depth - 1)
}
func recursiveInt(value: int): int = recursiveIdentity<int>(value, 3)

// A generic function cannot be selected by a runtime type test or an open type parameter. Runtime
// type reflection would require a new source, metadata, security, and ABI contract.
// expect: the compiler specializes statically selected calls only and never emits a dynamic generic
// dispatcher or hidden runtime type token for this signature.
func <T, U> openCaller(value: U): T = identity<T>(fail<U>())

// The following matrix is intentionally closed. Types not listed here must remain unsupported
// until their exact layout, storage, passing, root, return, and operation contracts are defined.
// expect: specialization never guesses that all nonprimitive values are pointers or that all
// pointer-shaped values have interchangeable runtime ownership.
func decimalFuture(value: decimal): decimal = identity<decimal>(value)
func charFuture(value: char): char = identity<char>(value)
func vectorFuture(value: Vector<int>): Vector<int> = identity<Vector<int>>(value)
func objectFuture(value: Object): Object = identity<Object>(value)

// Source-level array nullability, element nullability, and array object nullability are different
// identities. Each requires its own FIR type and runtime root proof; none is normalized to `[]T`.
// expect: nullable array and nullable element cases remain rejected independently.
func nullableContainer(value: ([]int)?): ([]int)? = identity<([]int)?>(value)
func nullableElement(value: []String?): []String? = identity<[]String?>(value)

// Tuple and function parameters need typed layouts and closure environments. They are not accepted
// by erasing their elements to the already-supported String pointer ABI.
// expect: no function pointer, environment pointer, tuple padding, or tuple root map is invented.
func tupleFuture(value: (bool, int)): (bool, int) = identity<(bool, int)>(value)
func functionFuture(value: (String) -> String): (String) -> String = identity<(String) -> String>(value)

// The closed signature is checked recursively: parameter, return, local, call edge, effects, and
// array item must all use the same declaration-owned substitution. A mismatch at one node makes
// the whole function and every caller that reaches it unverifiable.
// expect: no partially emitted target, no body truncation, and no alternate unspecialized symbol.
func <T> returnArray(value: T): []T = [value]
func arrayReturnInt(): []int = returnArray<int>(5)
func arrayReturnString(): []String = returnArray<String>("five")

// When a new scalar is admitted, its tests must cover signedness, width, literals, arithmetic,
// comparison, parameter lowering, result lowering, varargs, printing, array roots and GC movement.
// expect: adding a built-in name alone cannot change this file's normative rejection rows.
func pendingHalfArithmetic(value: half): half = add<half>(value, value)
func pendingQuadArray(values: []quad): []quad = identity<[]quad>(values)

// The same source declaration may have different concrete return representations at independent
// call sites. Entry points remain nongeneric and continue to use the required []String signature.
// expect: `Main.main` is a normal function; its generic callees are closed internal instances.
object Main {
    func main(args: []String) {
        println(identity<int>(42))
        println(identity<String>("Sol"))
    }
}

// Runtime entry arguments are not a special generic type-inference source. The user-written []String
// parameter is ordinary; generic calls in the body still select by their own arguments/results.
// expect: Main.main has no specialization arguments; identity<String> is a separate FIR function.
