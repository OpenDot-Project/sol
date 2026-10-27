// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative source cases; runtime/layout rows exceed the current native FIR slice.
module examples.semantics.generic_fir_storage_and_roots

import std.collections
import std.hash
import std.order
import std.stdio

class Box<T>(val value: T)

struct Pair<T, U> {
    val first: T
    val second: U
}

ref struct Borrowed<T> {
    val values: ReadOnlySpan<T>
}

func <T> echo(value: T): T = value
func <T> wrap(value: T): Box<T> = Box(value)
func <T, U> pair(first: T, second: U): Pair<T, U> = Pair(first, second)

// Each expect comment describes source meaning and the expected native admission boundary. It does
// not claim the row has been executed by a test runner or validated by a sanitizer.

// 1. Primitive identity has no managed scan edge.
// expect: Source:Bound(T=int); FIR:Admitted(current verified subset); descriptor:scalar/no-reference-slots.
func primitiveIdentity(value: int): int = echo<int>(value)

// 2. A scalar with pointer-like bits remains a scalar value.
// expect: Source:Bound(T=ulong); FIR:Admitted(current verified subset); GC:must not trace integer bits.
func scalarAddressPattern(value: ulong): ulong = echo<ulong>(value)

// 3. A String substitution selects a reference representation.
// expect: Source:Bound(T=String); FIR:Admitted(current verified subset); GC:trace String owner.
func stringIdentity(value: String): String = echo<String>(value)

// 4. The concrete instance tuple is declaration-ordered and owner-sensitive.
// expect: T owner=echo.ordinal0; same spelling in another declaration is a different identity.
func ownerIdentityCheck(left: String, right: String): String = echo<String>(left)

// 5. An array of supported primitive values is a single managed owner plus scalar payload.
// expect: Source:Bound(T=[]int); FIR:Admitted(current one-level-array subset); element-scan:none.
func primitiveArrayIdentity(value: []int): []int = echo<[]int>(value)

// 6. An array of String values is a managed owner plus reference-bearing element slots.
// expect: Source:Bound(T=[]String); FIR:Admitted(current one-level-array subset); root owner and scan elements.
func stringArrayIdentity(value: []String): []String = echo<[]String>(value)

// 7. A loop keeps the selected array owner and item live across body calls.
// expect: Source:Bound; FIR:Admitted for the existing qualified String array loop; roots:array + loaded item.
func sumStringLengths(values: []String): int {
    var result = 0
    for val value in values { result += value.length }
    result
}

// 8. The source array expression is evaluated once and the fixed length is snapshotted.
// expect: Source:Bound; FIR:Admitted only for current scalar/String arrays; generic collection rules do not apply.
func arrayExpressionAndRoot(values: []String): int {
    var result = 0
    for val value in selectArray(values) { result += value.length }
    result
}

// 9. `[]String?` has a non-null container and nullable String elements.
// expect: Source:Bound; element=String?; container=non-null; FIR:Rejected(SOL2400, nullable element layout).
func nullableStringElements(values: []String?): int {
    var result = 0
    for val value in values { if (value != null) { result += value.length } }
    result
}

// 10. Parenthesized element spelling preserves the same nullable-element type.
// expect: Source:Bound; `[](String?)` == `[]String?`; FIR:Rejected(SOL2400, current ABI gap).
func sameNullableElements(values: [](String?)): int {
    var result = 0
    for val value in values { if (value != null) { result += value.length } }
    result
}

// 11. Nullability on the complete array type changes the container, not its element descriptor.
// expect: Source:Bound; `([]String)?`; nullable owner; non-null String slots; FIR:Rejected(SOL2400).
func nullableArrayContainer(values: ([]String)?): int {
    if (values == null) { return 0 }
    values.length
}

// 12. Container and elements can independently be nullable.
// expect: Source:Bound; `([]String?)?`; nullability has two positions; FIR:Rejected(SOL2400).
func nullableContainerAndElements(values: ([]String?)?): int {
    if (values == null) { return 0 }
    var result = 0
    for val value in values { if (value != null) { result += value.length } }
    result
}

// 13. Flow narrowing the array container does not narrow its nullable elements.
// expect: Source:Bound; `values != null` proves owner presence only; element read remains String?.
func containerProofIsNotElementProof(values: ([]String?)?): int {
    if (values == null) { return 0 }
    val first: String? = values[0]
    if (first == null) { 0 } else { first.length }
}

// 14. An element null check does not make another element non-null.
// expect: Source:Bound; separate indices have independent values and facts; FIR:Rejected(SOL2400).
func elementFactsArePerRead(values: []String?): int {
    if (values[0] == null) { return 0 }
    values[1]?.length ?: 0
}

// 15. An array of arrays introduces a reference-bearing inner-array slot.
// expect: Source:Bound; T=[][]int; FIR:Rejected(SOL2400, nested array outside one-level subset).
func nestedArrayIdentity(values: [][]int): [][]int = echo<[][]int>(values)

// 16. An array of nullable array references contains optional owners at each element.
// expect: Source:Bound; element= []int?; container non-null; FIR:Rejected(SOL2400, nested/nullable shape).
func optionalInnerArrays(values: []([]int)?): int {
    var count = 0
    for val inner in values { if (inner != null) { count += inner.length } }
    count
}

// 17. Inline fixed arrays are values, not dynamic managed-array references.
// expect: Source:Bound; `[4]String` has inline reference slots; FIR:Rejected(SOL2400, fixed-array layout).
func fixedStringArray(values: [4]String): [4]String = echo<[4]String>(values)

// 18. A zero-length dynamic array remains a present array object.
// expect: Source:Bound; length=0; owner non-null; current FIR supports empty array literals only in admitted shape.
func emptyPrimitiveArray(): []int = []

// 19. A null array reference is not an empty array.
// expect: Source:Bound; `([]int)? = null` is a nullable owner; FIR:Rejected(SOL2400).
func absentArray(): ([]int)? = null

// 20. A null element is not a null array reference.
// expect: Source:Bound; `[]String?` has a present container and nullable element; FIR:Rejected(SOL2400).
func presentArrayWithNullElement(): []String? = [null]

// 21. An array literal's element type is inferred before generic instance admission.
// expect: Source:Bound(T=[]int); FIR:Admitted only if complete literal, stores, and ABI are supported.
func arrayLiteralIdentity(): []int = echo([1, 2, 3])

// 22. A scalar literal cannot infer an array type by backend preference.
// expect: Source:Bound(T=int); selected echo<int>; FIR:Admitted; no array overload fallback exists.
func scalarLiteralIdentity(): int = echo(3)

// 23. A generic result context may select a concrete String instance.
// expect: Source:Bound(T=String); FIR:Admitted current String representation; source type evidence is authoritative.
func expectedStringResult(): String = echo("rooted")

// 24. A nullable expected result is not a non-null layout proof.
// expect: Source:Bound(T=String?); FIR:Rejected(SOL2400); nullability remains in instance identity.
func expectedNullableString(): String? = echo<String?>(null)

// 25. A generic `Box<T>` field changes scan shape with T.
// expect: Source:Bound; Box<String>.value is a reference; current FIR:Rejected(SOL2400, generic owner/layout).
func stringBox(value: String): Box<String> = wrap<String>(value)

// 26. The same generic field is scalar for an integer instantiation.
// expect: Source:Bound; Box<int>.value is scalar; current FIR:Rejected(SOL2400, generic owner/layout).
func integerBox(value: int): Box<int> = wrap<int>(value)

// 27. Same-name generic owners cannot share one descriptor by spelling.
// expect: Box<T> and Pair<T,U> own different parameter identities; closed layout keys include owner + arguments.
func independentOwners(first: String, second: int): Pair<String, int> = pair(first, second)

// 28. A struct containing two references recursively scans both fields.
// expect: Source:Bound; Pair<String,String> has two reference locations; current FIR:Rejected(SOL2400).
func referencePair(first: String, second: String): Pair<String, String> = pair(first, second)

// 29. Mixed aggregate fields have different scan classifications.
// expect: Pair<String,int> scans field 0 only; scalar field 1 is skipped; current FIR:Rejected(SOL2400).
func mixedPair(first: String, second: int): Pair<String, int> = pair(first, second)

// 30. Copying a struct copies its references shallowly.
// expect: Source:Bound; source copy has two live reference values; both copies' slots are rooted independently.
func copiedReferencePair(value: Pair<String, String>): String {
    var copy = value
    copy.first
}

// 31. A scalar bit pattern that resembles a pointer does not create a managed edge.
// expect: Source:Bound; Pair<ulong, ulong> scan program has no reference offsets; FIR aggregate is rejected.
func scalarPairIsNotRoot(value: Pair<ulong, ulong>): ulong = value.first

// 32. Nullable struct storage needs a tag and conditional payload scan.
// expect: Source:Bound; Pair<String,int>? scans field 0 only when present; current FIR:Rejected(SOL2400).
func nullableAggregate(value: Pair<String, int>?): String? = value?.first

// 33. An empty enum variant contributes no payload references.
// expect: Source:Bound; tag selects an empty payload; a descriptor never scans inactive bytes.
func emptyEnumVariant(value: OptionString): bool = value.isNone

// 34. A reference-bearing enum scans only its active payload.
// expect: Source:Bound; active Some<String> payload is traced; None/unselected bytes are ignored.
func referenceEnumVariant(value: OptionString): String? = value.valueOrNull

// 35. A generic enum descriptor is concrete per selected type argument.
// expect: Option<int> and Option<String> have distinct payload scan shapes; current FIR:Rejected(SOL2400).
func genericOptionValue(value: Option<String>): Option<String> = echo<Option<String>>(value)

// 36. Generic default construction cannot assume zero is valid for every T.
// expect: Source:Bound only when defaultability rules prove T; non-null reference fields reject zero default.
func <T> genericDefaultValue(): T = default<T>()

// 37. `T:Object` does not box a primitive to invent a reference layout.
// expect: constraint `T:Object` rejects int; no boxing conversion is implied by GC classification.
func <T: Object> objectOnly(value: T): T = value

// 38. Interface conversion of a value type requires a boxed representation.
// expect: Source:Bound only with an applicable boxing rule; current FIR:Rejected(SOL2400, boxing ABI absent).
func <T: Named> interfaceBox(value: T): Named = value

// 39. A managed object field store publishes its reference through the insertion barrier.
// expect: Source:Bound; value may be white during concurrent marking; barrier publishes owner-to-value edge.
func storeObjectField(owner: Node, value: Node): () {
    owner.next = value
}

// 40. A managed reference array store uses the element's concrete reference shape.
// expect: `[]String` slot write is a managed reference store; runtime barrier required.
func storeStringElement(values: []String, index: int, value: String): () {
    values[index] = value
}

// 41. A nullable reference element write still uses a barrier for non-null values.
// expect: Source:Bound; `[]String?` can store null or a String edge; current FIR:Rejected(SOL2400).
func storeNullableElement(values: []String?, index: int, value: String?): () {
    values[index] = value
}

// 42. A scalar array write does not invoke a reference insertion barrier.
// expect: Source:Bound; `[]int` has no element references; current FIR supports qualified scalar array stores.
func storeScalarElement(values: []int, index: int, value: int): () {
    values[index] = value
}

// 43. Replacing a reference must protect the new edge before publication.
// expect: value remains rooted across evaluation and barrier; old edge removal cannot lose live temporaries.
func replaceNode(owner: Node, value: Node): () {
    owner.next = computeNode(value)
}

// 44. Compound mutation of a nested reference-containing struct requires field-aware barriers.
// expect: Value copy, concrete field path, owner root, and write barrier are all required.
func updateNestedReference(owner: AggregateOwner, value: String): () {
    owner.pair.first = value
}

// 45. A new object can be initialized without barriers only under a private unpublished rule.
// expect: construction may skip ordinary edge publication until the fully initialized object is published.
func constructBeforePublish(value: String): Node = Node(value)

// 46. Escaping `this` defeats the unpublished-object construction shortcut.
// expect: callback/callee publication makes fields visible; initialized reference fields use ordinary barriers.
func leakDuringConstruction(): Node = LeakingNode()

// 47. Collection insertion makes the stored value a strong managed edge.
// expect: `Vector<String>` is not implemented; normative backing storage traces every live entry.
func vectorInsert(values: Vector<String>, item: String): () {
    values.add(item)
}

// 48. Vector capacity bytes beyond count are not live element roots.
// expect: collector scans initialized slots only; uninitialized slack is never interpreted as references.
func vectorSpareCapacity(values: Vector<Node>): int = values.capacity - values.count

// 49. Removing a collection element clears or marks its slot inactive.
// expect: removed value becomes collectible after no other edge remains; tombstone policy is specified per type.
func vectorRemove(values: Vector<Node>, index: int): () {
    values.removeAt(index)
}

// 50. Hash keys remain rooted while present in a table.
// expect: HashMap<String,Node> requires both key and value scan shapes; current std.collections is pending.
func hashMapRoots(values: HashMap<String, Node>, key: String, item: Node): () {
    values[key] = item
}

// 51. Hash tombstones do not retain removed references accidentally.
// expect: a tombstone is not a live key/value slot; precise descriptor honors occupancy metadata.
func hashRemoval(values: HashMap<Node, Node>, key: Node): () {
    values.remove(key)
}

// 52. Ordered maps still require precise key/value roots.
// expect: TreeMap<String,Node> uses Ordering for keys and traces live keys and values, not tree links alone.
func treeMapRoots(values: TreeMap<String, Node>, key: String, item: Node): () {
    values[key] = item
}

// 53. Queue wraparound does not change which slots are live.
// expect: Queue<Node> scans active sequence only; cleared slots outside [head,count) are not roots.
func queueRoots(values: Queue<Node>, item: Node): () {
    values.enqueue(item)
}

// 54. Deque endpoints retain values until removal completes.
// expect: Deque<String> traces active slots through front/back wraparound; mutation must preserve barriers.
func dequeRoots(values: Deque<String>, item: String): () {
    values.pushFront(item)
}

// 55. Stack capacity is not the same as stack liveness.
// expect: Stack<Node> traces active count; popped values do not remain artificially reachable.
func stackRoots(values: Stack<Node>, item: Node): () {
    values.push(item)
}

// 56. A priority queue stores comparator and payload values with separate semantics.
// expect: PriorityQueue<Node> traces payloads; ordering metadata and comparator effects are independently owned.
func priorityQueueRoots(values: PriorityQueue<Node>, item: Node): () {
    values.enqueue(item)
}

// 57. Span is ref-like and cannot be an ordinary managed-array element.
// expect: `[]Span<int>` rejected by ref-like storage rule; array owner roots cannot legalize the view.
func arrayOfSpan(values: []Span<int>): int = values.length

// 58. A Span over a managed array keeps the base array alive while the view is live.
// expect: Source:Bound; borrow provenance=managed array; root=array owner; current FIR:Rejected(SOL2400).
func spanOverArray(owner: []String): int {
    val view: ReadOnlySpan<String> = owner.asReadOnlySpan()
    Gc.collect()
    view[0].length
}

// 59. A Span over stack storage needs frame-lifetime proof instead of a GC root.
// expect: Source:Bound only within originating scope; collector registration does not extend stack lifetime.
func spanOverStack(): int {
    val local = [10, 20, 30]
    val view: Span<int> = local.asSpan()
    view[0]
}

// 60. Copying a span header preserves its origin and does not copy its buffer.
// expect: copied view has same owner identity, offset, length, mutability, and lifetime.
func copiedSpanHeader(view: Span<int>): Span<int> = view

// 61. A slice composes a checked offset with its existing origin.
// expect: nested span bounds remain within the original buffer; integer overflow is rejected.
func spanSlice(view: Span<int>, offset: int, length: int): Span<int> = view.slice(offset, length)

// 62. ReadOnlySpan cannot store through its own access path.
// expect: write is source-rejected; aliases through a writable Span may still mutate backing storage.
func readonlySpanWrite(view: ReadOnlySpan<int>, value: int): () {
    view[0] = value
}

// 63. Span mutation through reference elements uses the managed barrier.
// expect: `Span<String>` borrow itself is not a root; its managed-array owner is rooted and write is barriered.
func mutateSpanReference(view: Span<String>, value: String): () {
    view[0] = value
}

// 64. A ref struct may contain a Span only while the aggregate remains stack scoped.
// expect: Borrowed<String> is ref-like; descriptor does not convert it to an ordinary heap object.
func borrowedAggregate(view: ReadOnlySpan<String>): Borrowed<String> = Borrowed(view)

// 65. An ordinary class cannot store a ref-like view.
// expect: Source:Rejected(ref-like-heap-field); no collector descriptor can make this safe.
func escapingBorrowedAggregate(view: Span<String>): SpanOwner = SpanOwner(view)

// 66. A closure cannot capture a scoped span into an escaping environment.
// expect: Source:Rejected(ref-like-capture); rooting the closure would not keep a stack origin valid.
func captureSpan(view: ReadOnlySpan<String>): () -> String = { view[0] }

// 67. An immediate synchronous callback needs a non-escape proof.
// expect: permitted only if callee does not retain callback and returns before view scope ends.
func immediateSpanCallback(view: ReadOnlySpan<String>): String = useNow({ view[0] })

// 68. A span cannot be sent to a background process or retained across a process boundary.
// expect: Source:Rejected(ref-like-cross-process); guest heap and host/guest pointers are unrelated.
func sendSpanToWorker(view: Span<String>): () {
    spawnWorker(view)
}

// 69. A span cannot be retained by asynchronous native I/O after the caller returns.
// expect: requires explicit copy/owned buffer or strong handle contract; naked span pointer is invalid.
func retainSpanInNativeCall(view: ReadOnlySpan<byte>): () {
    startAsyncIo(view)
}

// 70. Ref/out references to a managed field root the owner, not an interior address.
// expect: writable location tracks owner + field offset; collector never scans the interior pointer as an object.
func refField(owner: Node): () {
    setNext(ref owner.next)
}

// 71. A ref/out local remains rooted through a generic call and later collection.
// expect: concrete `T=String`; caller and callee frame maps keep addressable storage visible.
func refGenericLocal(): String {
    var local = "rooted"
    replace<String>(ref local)
    Gc.collect()
    local
}

// 72. Ref/out type equality is exact; widening conversions are not locations.
// expect: `ref short` cannot specialize `ref T` as int; Source:Rejected(exact-location-type).
func refCannotWiden(): () {
    var narrow: short = 1
    replace<int>(ref narrow)
}

// 73. `out` references require assignment on every normal return and retain their concrete type.
// expect: out local roots are initialized/updated according to the callee frame contract.
func outString(): String {
    var result: String
    produce<String>(out result)
    result
}

// 74. A generic by-value reference result becomes a caller root before another safepoint.
// expect: call result String remains rooted from return until stored/returned or proven dead.
func callThenCollect(owner: Node): String {
    val result = owner.makeString()
    Gc.collect()
    result
}

// 75. A reference temporary used after another argument can allocate remains rooted.
// expect: left-to-right argument evaluation roots the first result across evaluation of the second.
func twoAllocatingArguments(): Pair<String, String> = pair(makeString(), makeString())

// 76. A branch-local reference is rooted only along reachable live edges.
// expect: stack map accounts for joins and kills without treating stale slot bits as a strong root.
func branchRoot(flag: bool, first: String, second: String): String {
    val chosen = if (flag) first else second
    Gc.collect()
    chosen
}

// 77. Loop backedges must publish live array and item roots before polling.
// expect: root spill/handshake precedes collection participation at every supported backedge.
func loopRoots(values: []String): () {
    for val value in values { consumeAndCollect(value) }
}

// 78. A returned ref cannot outlive a stack local.
// expect: Source:Rejected(ref-to-local-escape); rooting the frame forever is not a lifetime solution.
func invalidLocalRef(): ref String {
    var local = "local"
    return ref local
}

// 79. Captured mutable values use a shared managed cell when closures are implemented.
// expect: two escaping closures share one cell; environment and cell references are traced.
func captureMutable(): (() -> int, () -> int) {
    var count = 0
    val next = { count += 1; count }
    val read = { count }
    (next, read)
}

// 80. A copied captured val is shallow when it contains a managed reference.
// expect: closure captures String reference; environment scan includes it until closure is dead.
func captureImmutable(value: String): () -> String = { value }

// 81. Closure code pointers are not managed heap references.
// expect: stack map/descriptor scans environment owner; machine code address is not traced.
func closureHasOneManagedEdge(callback: () -> String): () -> String = callback

// 82. Unwind state can extend a generic reference's lifetime through defer.
// expect: This source shape remains native-rejected today; future frame map includes cleanup captures.
func <T> cleanupRetainsGeneric(value: T): () throws IOException {
    defer { writeLog(value) }
    operationThatThrows()
}

// 83. A pending Throwable is a strong runtime owner across a native exception carrier.
// expect: Throwable is transferred exactly once; copies/rethrows preserve identity and keep message/cause roots.
func <E: Throwable> genericExceptionRoot(error: E): ! throws E {
    throw error
}

// 84. Suppressed exceptions are reachable only through the live primary Throwable chain.
// expect: bounded suppression metadata owns each Throwable reference and rejects cycles per runtime contract.
func suppressedExceptionRoots(primary: Exception, secondary: Exception): Exception {
    primary.addSuppressed(secondary)
    primary
}

// 85. A generic descriptor key includes the closed type tuple and layout version.
// expect: Box<int> and Box<String> cannot alias a descriptor with incompatible scan shape.
func descriptorTupleIdentity(): () = registerDescriptor<Box<int>>()

// 86. A descriptor mismatch is a runtime compatibility error, not a request to scan conservatively.
// expect: invalid version/type/offset is rejected before reading payload words.
func invalidImportedDescriptor(): () = loadLibraryWithDescriptorMismatch()

// 87. Partial generic layout is independent of file enumeration order.
// expect: all compatible parts merge under one owner; conflicting field/type parameters reject pre-layout.
func partialTypeIdentity(value: PartialBox<String>): String = value.value

// 88. Recursive managed references are finite layouts.
// expect: Node.next is a reference edge; cycles are collectible after all roots are removed.
func recursiveNode(value: Node): Node? = value.next

// 89. Recursive by-value structures without a finite size are invalid.
// expect: layout graph cycle with no managed-reference edge is rejected before descriptor emission.
func invalidInlineCycle(value: InfiniteValue): InfiniteValue = value

// 90. A failed collection resize does not publish a half-initialized root graph.
// expect: old storage remains valid until commit; new backing storage is either complete or unreachable.
func resizeFailure(values: Vector<Node>): () {
    values.ensureCapacity(computeCapacity())
}

// 91. An allocation safepoint must publish all references live after the allocation.
// expect: construction result is rooted before a subsequent allocating call can trigger collection.
func allocationSequence(value: String): Pair<String, String> = pair(allocateCopy(value), allocateCopy(value))

// 92. Runtime root publication and barrier memory order form one collector protocol.
// expect: mutator cannot acknowledge a handshake with an unprotected post-barrier reference.
func concurrentPublication(owner: Node, value: Node): () {
    owner.next = value
    Gc.collect()
}

// 93. Host CLR objects are never guest Yellow Tea roots.
// expect: no cross-process object handle; compiler transport uses bytes/scalars/arguments/exit status only.
func hostGuestBoundary(value: String): () = executeOrcGuest(value)

// 94. Guest object addresses are never valid compiler CLR references.
// expect: ORC guest returns a process status and serialized diagnostics, not a managed object pointer.
func guestCannotReturnObjectAddress(): int = runGuestAndReadExitCode()

// 95. Static SolRT linkage does not merge independent collectors.
// expect: linkage controls deployment; runtime ownership and collector instance are specified separately.
func staticRuntimeDoesNotShareObjects(value: Node): () = linkWithStaticSolrt(value)

// 96. Runtime memory pressure cannot change a source type's scan classification.
// expect: allocation failure or collection policy does not erase a reference edge or nullable tag.
func pressureDoesNotEraseType(value: String?): String? = value

// 97. AOT and ORC must agree on layouts for the same verified source instance.
// expect: backend changes execution mode only; closed type identity, descriptor semantics, and root lifetime match.
func backendIndependentInstance(value: String): String = echo<String>(value)

// 98. A generic C export must use an explicit concrete wrapper.
// expect: `echo<T>` is not itself a C ABI symbol; wrapper type and runtime closure are versioned.
func <T> genericIsNotCExport(value: T): T = value

// 99. A container cannot request an instance from an external open declaration today.
// expect: .solib schema 2 does not define generic instantiation payloads or consumer-owned type IDs.
func <T> externalInstantiation(value: T): T = externalOpenGeneric<T>(value)

// 100. Serialization cannot mint a root descriptor or VerifiedFirProgram capability.
// expect: JSON inspection schema and native descriptor ABI are separate versioned contracts.
func jsonCannotCreateDescriptor(): () = consumeSerializedFirMetadata()

// 101. Unsupported nested storage rejects before native emission.
// expect: generic nested arrays and nested value layouts require their complete descriptor and root proof.
func <T> nestedLayoutBoundary(value: [][]T): [][]T = value

// 102. Collection implementation status is independent of type spelling.
// expect: importing std.collections or naming Vector<T> does not provide allocation, hash, order, or GC code.
func collectionsArePending(values: Vector<int>): int = values.count

// 103. A ref-like anti-constraint permits a candidate but not an escape.
// expect: `allows ref struct` is necessary for scoped use and insufficient for heap fields/capture/return.
func <T> constrainedBorrow(value: T): T where T: allows ref struct = value

// 104. A stack view does not become safe because its backing bytes resemble an array object.
// expect: provenance remains an explicit frame/local owner; unmanaged byte range is not a managed root.
func stackProvenance(view: Span<byte>): int = view.length

// 105. An interior offset is checked against the owner and window.
// expect: base object is rooted; offset arithmetic is bounded and cannot wrap to another object.
func interiorBounds(view: Span<int>, index: int): int = view[index]

// 106. Borrowed view mutation uses the owner barrier only when the concrete element has references.
// expect: Span<int> skips reference barrier; Span<String> publishes a String edge.
func elementSpecificBarrier(numbers: Span<int>, strings: Span<String>): () {
    numbers[0] = 4
    strings[0] = "value"
}

// 107. A stack map for a generic aggregate records nested offsets after concrete substitution.
// expect: Pair<String,int> differs from Pair<int,String>; map shape follows concrete fields, not parameter names.
func concreteAggregateMap(value: Pair<String, int>): String = value.first

// 108. An inactive nullable payload is not scanned.
// expect: Some/None or nullable-value tag is validated before any nested payload scan.
func nullablePayloadState(value: Pair<String, int>?): bool = value != null

// 109. A root slot must not outlive its owning frame unless explicitly transferred to a handle.
// expect: frame pop removes its roots exactly once after cleanup and exception ownership transfer.
func frameScope(value: String): () = useWithinFrame(value)

// 110. An explicit strong handle can retain a value across native ownership only under runtime rules.
// expect: handle owns exactly one runtime instance; close/release coordinates active users.
func nativeStrongHandle(value: String): () = withStrongHandle(value)

// 111. A weak handle is not a strong root until successful promotion.
// expect: weak lookup validates address plus allocation identity before publishing a rooted result.
func weakHandlePromotion(value: Node): Node? = weakHandle(value).resolve()

// 112. Collection scans do not traverse closed handle history as strong roots.
// expect: closed handle records are not roots; stale weak addresses are validated without dereference.
func closedHandleIsNotRoot(value: Node): () = closeHandle(value)

// 113. Generated root liveness is not proven by a source-level local name.
// expect: SSA/register copies and reused slots need verified safepoint maps and spill ordering.
func registerCopyAcrossPoll(value: String): String {
    val another = makeString()
    Gc.collect()
    consume(another)
    value
}

// 114. A result used after a generic call is live even if the callee's source spelling is identical.
// expect: each concrete instance's call edge and return root is validated by its closed tuple.
func genericResultAcrossPoll(value: String): String {
    val result = echo<String>(value)
    Gc.collect()
    result
}

// 115. Loop item reassignment does not write back to an array.
// expect: local copy may become a new reference; backing slot remains unchanged without indexed store.
func iterationItemIsCopy(values: []String, replacement: String): () {
    for var item in values { item = replacement }
}

// 116. Explicit element reassignment does update the managed array edge.
// expect: `values[index] = replacement` is bounds checked and uses insertion barrier.
func indexedElementMutation(values: []String, replacement: String): () {
    values[0] = replacement
}

// 117. HashMap key/value generic parameters have independent layout identities.
// expect: HashMap<String,Node> uses reference scans for both; HashMap<int,Node> skips key references.
func mapDifferentTypePositions(left: HashMap<String, Node>, right: HashMap<int, Node>): () {
    consume(left)
    consume(right)
}

// 118. Tree constraints do not imply traceability.
// expect: Ordering decides key comparison; descriptor still follows the concrete key/value layout.
func <K: Ordering, V> orderingDoesNotDescribeGc(map: TreeMap<K, V>): int = map.count

// 119. Hash constraints do not imply pointer-free storage.
// expect: Hashable values may contain managed references; collection scan shape uses concrete descriptor.
func <K: Hashable, V> hashableMayReference(map: HashMap<K, V>): int = map.count

// 120. Final contract: every specialized storage edge has exactly one verified owner path.
// expect: if analysis, descriptor, roots, barriers, lifetime, or artifact agreement is missing, reject before commit.
func <T> completeGenericRootContract(value: T): T = value
