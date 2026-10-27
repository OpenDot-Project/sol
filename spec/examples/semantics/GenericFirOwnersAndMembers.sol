// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative source and FIR-boundary examples; only stateless top-level object methods have a bounded native slice.
module source.spec.examples.semantics.generic_fir_owners_and_members

// Generic owner/member specialization case matrix
// ================================================
//
// These cases define expected source binding and distinguish it from native FIR admission. A
// `Source: Bound` expectation means the example has one resolved declaration and a determinate
// constructed owner/method substitution under the stated rules. It does not prove that the analyzer
// has executed this fixture. `FIR: Rejected(SOL2400)` means the current native source-admission
// boundary must reject a shape that cannot be represented by the verified generic-function slice.
// `FIR: Planned` marks future owner/member specialization requirements.
//
// The contract is detailed in `spec/GenericFirOwnersAndMembers.sol`. Generic call argument mapping,
// overload ranking, effects, flow, nullability, ref-like lifetime, and artifacts have additional
// topic-specific specifications.

// Shared declarations
// --------------------

interface Keyed<K> { func key(): K }

interface Named { func name(): String }

interface HasValue<V> { func read(): V }

class PairValue<A, B>(val first: A, val second: B)

class StringKey(val value: String): Keyed<String> {
    override func key(): String = value
}

class IntValue(val value: int): HasValue<int> {
    override func read(): int = value
}

open class Base<T>(val base: T) {
    open func get(): T = base
}

open class Middle<U>: Base<[]U>([])

class Leaf: Middle<String>()

// 1. A generic owner and a same-spelled method parameter have distinct identities.

class OwnerAndMethod<T>(val ownerValue: T) {
    func <T> methodValue(value: T): T = value
}

func useOwnerAndMethod(owner: OwnerAndMethod<String>): int = owner.methodValue<int>(1)

// expect: owner-method.identity | Source: Bound | owner T=String; method T=int | FIR: Rejected(SOL2400, generic owner body).

// 2. Owner substitution is applied before method inference.

class Mapper<T>(val value: T) {
    func <U> map(convert: (T) -> U): U = convert(value)
}

func useMapper(mapper: Mapper<String>): int = mapper.map({ text -> text.length })

// expect: mapper.owner-method-order | Source: Bound | owner T=String; method U=int | FIR: Rejected(SOL2400).

// 3. Explicit owner arguments are validated before constructor overload selection.

class Crate<T>(val value: T) {
    constructor(value: T, label: String): this(value) { }
}

func explicitCrate(): Crate<String> = Crate<String>("payload")

// expect: crate.explicit-owner | Source: Bound | owner T=String; constructor=primary | FIR: Rejected(SOL2400, generic owner construction).

// 4. Constructor arguments may infer an owner when one unique solution exists.

func inferredCrate(): Crate<int> = Crate(3)

// expect: crate.inferred-owner | Source: Bound | owner T=int | FIR: Rejected(SOL2400, generic owner construction).

// 5. Conflicting constructor evidence does not guess between owner arguments.

func <T> conflictingCrate(first: T, second: String): Crate<T> = Crate(first, second)

// expect: crate.conflict | Source: Rejected or Deferred by inference; no partial owner instance | FIR: NoInstance.

// 6. A generic owner constraint is checked at construction.

class KeyedCrate<K: Keyed<K>, V>(val key: K, val value: V)

func validKeyedCrate(key: StringKey): KeyedCrate<StringKey, int> = KeyedCrate(key, 1)

// expect: crate.owner-constraint | Source: Bound; K=StringKey, V=int; constraints=satisfied | FIR: Rejected(SOL2400).

// 7. A method constraint does not rewrite the owner constraint.

class SeparateBounds<T: Named>(val value: T) {
    func <U: Keyed<U>> select(owner: U): PairValue<T, U> = PairValue(value, owner)
}

func separateBounds(value: SeparateBounds<StringKey>, key: StringKey): PairValue<StringKey, StringKey> =
    value.select(key)

// expect: constraints.separate-owners | Source: Bound; owner T=StringKey, method U=StringKey | FIR: Rejected(SOL2400).

// 8. A same-spelled parameter owned by another declaration is not interchangeable.

class FirstOwner<T>(val value: T)
class SecondOwner<T>(val value: T)

func <T> mix(first: FirstOwner<T>, second: SecondOwner<T>): PairValue<T, T> =
    PairValue(first.value, second.value)

func mixDifferentOwners(first: FirstOwner<String>, second: SecondOwner<int>): PairValue<String, int> =
    PairValue(first.value, second.value)

// expect: owner.identity-not-name | Source: Rejected for mixDifferentOwners; one T cannot be String and int | FIR: NoInstance.

// 9. Partial parts share one owner identity and ordinal list.

partial class PartialBox<K, V> {
    func key(): K = fieldKey
}

partial class PartialBox<K, V> {
    val value: V
}

// expect: partial.owner-identity | Source: Bound after merge; both parts share (PartialBox, ordinal) parameters | FIR: Rejected(SOL2400).

// 10. Reordered partial parameters invalidate the complete owner.

// source part A: partial class Reordered<K, V> { }
// source part B: partial class Reordered<V, K> { }

// expect: partial.order | Source: Rejected(partial generic parameter order mismatch); no member table | FIR: NoInstance.

// 11. A class and struct with the same logical name do not merge.

// source part A: partial class Category<T> { }
// source part B: partial struct Category<T> { }

// expect: partial.category | Source: Rejected(declaration category mismatch) | FIR: NoInstance.

// 12. Partial methods form one member set after owner validation.

partial class PartialMethods<T> {
    func first(): T = value
}

partial class PartialMethods<T> {
    func second(): T = first()
}

// expect: partial.member-set | Source: Bound after merge | owner T=String for PartialMethods<String> | FIR: Rejected(SOL2400).

// 13. Duplicate method identity across partial parts is an error.

// source part A: partial class Duplicate<T> { func get(): T = first }
// source part B: partial class Duplicate<T> { func get(): T = second }

// expect: partial.duplicate-method | Source: Rejected(duplicate member identity); no partial winner | FIR: NoInstance.

// 14. A partial object remains a single object identity.

partial object Settings {
    val left: int = 1
}

partial object Settings {
    val right: int = 2
}

func objectValues(): int = Settings.left + Settings.right

// expect: partial.object | Source: Bound; one singleton owner; no generic object arguments | FIR: current non-generic object subset only if admitted.

// 15. A companion is a distinct singleton owner.

class Container<T> {
    companion object Factory {
        func <U> create(value: U): Container<U> = Container(value)
    }
}

func companionCreate(): Container<int> = Container.Factory.create(1)

// expect: companion.generic-method | Source: Bound; method U=int; owner Container<T> is not captured from an instance | FIR: Rejected(SOL2400).

// 16. A companion cannot guess an enclosing instance's type parameter.

class GenericCompanion<T> {
    companion object Factory {
        // The source contract must reject a free reference to T here unless explicitly owner-bound.
        // func invalid(): T = ...
    }
}

// expect: companion.no-instance-capture | Source: Rejected(unbound owner parameter) | FIR: NoInstance.

// 17. A nested type carries only the parameter identities granted by its declaration scope.

class Outer<T>(val value: T) {
    class Inner<U>(val inner: U) {
        func pair(): PairValue<T, U> = PairValue(value, inner)
    }
}

// expect: nested.owner-scope | Source: Bound only if the nested declaration captures Outer<T> under the declared rule | FIR: Rejected(SOL2400).

// 18. An undeclared outer parameter cannot be recovered by name.

class IndependentOuter<T> {
    class IndependentInner<T>(val value: T) {
        func own(): T = value
    }
}

// expect: nested.shadowed-name | Source: Inner T is independent; no implicit outer T capture | FIR: Rejected(SOL2400).

// 19. Generic class fields require a concrete layout.

class FieldBox<T>(val value: T)

func fieldRead(box: FieldBox<String>): String = box.value

// expect: field.layout | Source: Bound; FieldBox<String>.value is String | FIR: Rejected(SOL2400, generic owner layout).

// 20. Nullable fields preserve nullable identity through the owner map.

class NullableField<T>(val value: T?)

func nullableField(box: NullableField<String>): String? = box.value

// expect: field.nullable | Source: Bound; field type String? | FIR: Rejected(SOL2400, nullable owner layout).

// 21. Generic struct identity does not imply class identity.

struct Point<T> {
    val x: T
    val y: T
}

func pointRead(point: Point<int>): int = point.x

// expect: struct.owner | Source: Bound; owner T=int; value semantics retained | FIR: Rejected(SOL2400, generic value layout).

// 22. Generic ref struct storage requires a region proof.

ref struct Borrowed<T> {
    val value: ReadOnlySpan<T>
}

func borrowedRead(value: Borrowed<int>): int = value.value[0]

// expect: refstruct.owner | Source: Bound only in an allowed region | FIR: Rejected(SOL2400, ref-like generic storage).

// 23. The anti-constraint does not authorize heap escape.

func <T> identityRefLike(value: T): T where T: allows ref struct = value

class HeapHolder<T>(val value: T) where T: allows ref struct {}

// expect: allows-ref-struct.storage | Source: Rejected(ref-like value stored in heap owner) | FIR: NoInstance.

// 24. Inherited owner arguments are substituted across every base edge.

func inheritedLeaf(leaf: Leaf): []String = leaf.get()

// expect: inheritance.projected-result | Source: Bound; Leaf -> Middle<String> -> Base<[]String>; result []String | FIR: Rejected(SOL2400, generic owner method).

// 25. Inherited generic methods preserve both owner and method maps.

open class BaseMapper<T> {
    open func <U> map(value: T, fallback: U): PairValue<T, U> = PairValue(value, fallback)
}

class StringMapper: BaseMapper<String>()

func inheritedMethod(mapper: StringMapper): PairValue<String, int> = mapper.map("x", 1)

// expect: inheritance.owner-method | Source: Bound; T=String, U=int | FIR: Rejected(SOL2400).

// 26. A generic diamond deduplicates one identical constructed base.

interface Root<T> { func root(): T }
interface Left<T>: Root<T> { }
interface Right<T>: Root<T> { }
class Diamond<T>: Left<T>, Right<T> { }

// expect: inheritance.same-projection | Source: Bound; one Root<String> projection for Diamond<String> | FIR: Rejected(SOL2400).

// 27. Conflicting constructed base projections are not resolved by traversal order.

// source: interface RootPath<T> { }
// source: interface LeftPath: RootPath<String> { }
// source: interface RightPath: RootPath<int> { }
// source: class InvalidDiamond: LeftPath, RightPath { }

// expect: inheritance.conflicting-projection | Source: Rejected(inconsistent constructed base identity) | FIR: NoInstance.

// 28. Override matching substitutes owner arguments before signature comparison.

open class GenericBase<T> {
    open func get(): T = value
}

class StringBase: GenericBase<String>() {
    override func get(): String = "value"
}

// expect: override.owner-substitution | Source: Bound; base result T projects to String | FIR: Rejected(SOL2400, generic owner dispatch).

// 29. Same spelling and arity are insufficient to be an override.

open class WrongBase<T> {
    open func convert(value: T): T = value
}

class WrongOverride: WrongBase<String>() {
    // This declaration does not override if parameter mode, method generic arity, or projected
    // parameter type differs, even if the source name is the same.
    // override func convert(value: int): int = value
}

// expect: override.signature | Source: Rejected(no compatible override target) | FIR: NoInstance.

// 30. An inaccessible member cannot supply generic evidence.

class HiddenMethod<T> {
    private func <U> choose(value: U): U = value
}

func outsideHidden(owner: HiddenMethod<String>): int = owner.choose(1)

// expect: visibility.inference | Source: Rejected(inaccessible member); private candidate contributes no U=int evidence | FIR: NoInstance.

// 31. Accessor visibility is separate from property visibility.

class RestrictedProperty<T>(initial: T) {
    public var value: T { private set } = initial
}

// expect: visibility.accessor | Source: public read of T may bind; external write is rejected | FIR: Rejected(SOL2400).

// 32. Generic property reads do not execute the getter during inference.

class Getter<T>(val stored: T) {
    var result: T { get = stored }
}

func readGetter(value: Getter<String>): String = value.result

// expect: property.inference | Source: Bound from declared getter/result type; no speculative getter execution | FIR: Rejected(SOL2400).

// 33. Setter specialization uses the same closed owner as the getter.

func writeGetter(value: Getter<String>, item: String): () {
    value.result = item
}

// expect: property.setter | Source: Rejected because `result` has no setter | FIR: NoInstance.

// 34. Compound property assignment preserves one receiver evaluation.

class Counter<T>(var value: T)

func compoundCounter(counter: Counter<int>, delta: int): () {
    counter.value += delta
}

// expect: property.compound | Source: Bound only with a valid getter/operator/setter sequence; each step has its own effect | FIR: Rejected(SOL2400).

// 35. Generic indexer argument mapping is call-like.

class Indexed<T> {
    operator func get(index: int): T = element
    operator func set(index: int, value: T): () { }
}

func indexRead(value: Indexed<String>): String = value[0]

// expect: indexer.read | Source: Bound; owner T=String; selected getter result String | FIR: Rejected(SOL2400).

// 36. Indexed update evaluates indices once and selects a closed setter.

func indexWrite(value: Indexed<String>, index: int, item: String): () {
    value[index] = item
}

// expect: indexer.write | Source: Bound; getter/setter semantics depend on operation; setter value String | FIR: Rejected(SOL2400).

// 37. Indexer result is not automatically a ref/out location.

func invalidIndexerRef(value: Indexed<String>, index: int): () {
    // expect: indexer.ref | Source: Rejected unless the indexer contract exposes stable storage | FIR: NoInstance.
    // consume(ref value[index])
}

// 38. Operator resolution uses an operand owner, not an unrelated module function.

class Measure<T>(val value: T)

func addMeasure(left: Measure<int>, right: Measure<int>): Measure<int> = left + right

// expect: operator.owner | Source: Bound only if the `plus` operator is declared in the permitted owner/companion | FIR: Rejected(SOL2400).

// 39. A generic constraint does not synthesize an operator implementation.

func <T: std.order.Ordering> compareOnly(left: T, right: T): int = left.compareTo(right)

// expect: operator.no-synthesis | Source: Bound if `compareTo` is in the constraint; `left + right` remains invalid absent an operator contract.

// 40. Delegated properties select protocol members after substitution.

class Delegate<T>(val value: T) {
    operator func getValue(thisRef: Object?, property: SolProperty<T>): T = value
    operator func setValue(thisRef: Object?, property: SolProperty<T>, next: T): () { }
}

class Delegated<T>(value: T) {
    var item: T by Delegate(value)
}

func delegatedRead(value: Delegated<String>): String = value.item

// expect: delegate.generic-owner | Source: Bound only when getValue/setValue resolve on Delegate<String>; runtime names are not lookup | FIR: Rejected(SOL2400).

// 41. Delegated getter and setter effects are separate.

class EffectfulDelegate<T, E: Throwable>(val value: T) {
    operator func getValue(thisRef: Object?, property: SolProperty<T>): T throws E = value
    operator func setValue(thisRef: Object?, property: SolProperty<T>, next: T): () throws E { }
}

// expect: delegate.effects | Source: Getter and setter contribute substituted effects at their respective access sites | FIR: Rejected(SOL2400).

// 42. Virtual generic dispatch requires a validated slot identity.

open class VirtualBase<T> {
    open func <U> map(value: T, fallback: U): PairValue<T, U> = PairValue(value, fallback)
}

class VirtualChild: VirtualBase<String>() {
    override func <U> map(value: String, fallback: U): PairValue<String, U> = PairValue(value, fallback)
}

// expect: dispatch.generic-slot | Source: Bound after checking override slot and effects | FIR: Rejected(SOL2400, virtual generic slot).

// 43. Interface dispatch retains constructed interface arguments.

class StringKeyed: Keyed<String> {
    override func key(): String = "key"
}

func interfaceKey(value: Keyed<String>): String = value.key()

// expect: dispatch.interface | Source: Bound; slot owner is Keyed<String> | FIR: Existing nongeneric dispatch support only; generic owner admission remains rejected.

// 44. Ref/out requires exact substituted storage.

class RefCell<T>(var value: T) {
    func update(out result: T): () { result = value }
}

func refCell(cell: RefCell<int>, result: int): () {
    cell.update(out result)
}

// expect: refout.owner | Source: Bound only if `result` is writable and exactly int | FIR: Rejected(SOL2400).

// 45. A derived generic projection cannot widen a ref/out location.

func invalidRefProjection(cell: RefCell<short>, result: int): () {
    // expect: refout.no-widen | Source: Rejected(exact ref/out type identity; short is not int) | FIR: NoInstance.
    // cell.update(out result)
}

// 46. Generic owner effects substitute through member calls.

class Reader<T, E: Throwable>(val value: T) {
    func read(): T throws E = value
}

func <E: Throwable> readWithEffect(reader: Reader<String, E>): String throws E = reader.read()

// expect: effects.owner-argument | Source: Bound; E remains owner parameter until caller closes it | FIR: Rejected(SOL2400).

// 47. A selected member is not replaced after native rejection.

class Selected<T> {
    func <U> choose(value: U): U = value
}

func selectedNoFallback(value: Selected<String>): int = value.choose(1)

// expect: no-fallback | Source: Bound; choose<U> selected with U=int | FIR: Rejected(SOL2400); no unrelated module overload fallback.

// 48. Source member binding does not prove a closed member body.

class OpenRunner<T>(val value: T) {
    func run(): String = value.describe()
}

// expect: body.rebinding | Source: May be bound through T's declared bound | FIR: Rejected(SOL2400) until body is rebound for a concrete owner.

// 49. Generic owner metadata cannot be dropped from an artifact.

// A public `Crate<T>.get` cannot become an exported C symbol `get` without a closed wrapper and a
// target-specific ABI contract. A `.solib` must record any closed exported instance explicitly.

// expect: artifact.open-export | Source: Bound declaration | FIR: Rejected(open generic export); no erased symbol.

// 50. Different artifacts do not erase the owner/member boundary.

// Internal AOT and ORC instances use the same verified source owner and method identity. `bin`,
// `cdylib`, `staticlib`, and `solib` require separate export and linking qualification. Dynamic or
// `--static-solrt-lib` runtime choice does not make a generic owner layout available.

// expect: artifact.kind | Source: Artifact-independent member binding | FIR: Rejected until concrete owner/member ABI is admitted.

// 51. Resource exhaustion cannot publish a partial generic member table.

// If partial merging, inherited projection, or candidate traversal exceeds its resource bound, the
// lookup result remains incomplete. The compiler does not choose the first visible method and then
// continue to specialization as if the candidate set were complete.

// expect: resource.partial-lookup | Source: Incomplete(resource diagnostic) | FIR: NoInstance.

// 52. Cancellation discards unpublished member-instance work.

// If cancellation arrives while composing owner/method substitutions or building the instance
// graph, no half-built `VerifiedFirProgram` is returned and no artifact is committed.

// expect: cancellation.transaction | Source: OperationCanceled | FIR: NoProgram.

// 53. Owner/member diagnostics retain multiple source origins.

// A failing override points first to the derived declaration and notes the projected base member.
// A failing constructor call points first to the call and notes the owner constraints and selected
// constructor. A generic body error points to the operation and records the closed instance key.

// expect: diagnostics.origin-chain | Source: Stable spans and owner identity | FIR: No diagnostic from a forged instance.

// 54. Current capability and future contract remain separate.

// Supported today: closed module-level generic free functions and generic methods on stateless,
// non-generic top-level objects. Object method calls directly name the singleton or are unqualified
// within that same object; no receiver slot is emitted. This does not support local/computed object
// receivers, stateful or nested/companion owners, generic owner layouts, constructors, properties,
// accessors, indexers, operators, open owner bodies, virtual/interface dispatch, or external exports.

// 55. Bounded receiver-free object method specialization.
// -------------------------------------------------------
object GenericMethodFixture {
    func <T> identity(value: T): T = value
    func <T> relay(value: T): T = identity<T>(value)
}

// Source: Bound; FIR: Verified when closed type arguments and body operations are admitted.
// Calls such as GenericMethodFixture.relay<String>(value) and the unqualified nested identity<T>
// call are statically tied to the object declarations. The existing pure omitted-default plan may
// capture earlier value arguments. No `this`, object state or dynamic dispatch enters the function ABI.

// expect: status | Source: Explicit | FIR: A current `SOL2400` rejection is not a language-level rejection.
