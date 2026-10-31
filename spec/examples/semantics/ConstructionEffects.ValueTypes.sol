// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Planned behavior fixture: value construction and zero initialization have separate contracts.
module spec.construction.valuetypes

class ValueFailure: Exception {}

func record(value: int): () {}

// A struct constructor assigns every field before returning a value.
struct Pair {
    val left: int
    val right: int
    constructor(left: int, right: int) {
        this.left = left
        this.right = right
    }
}

func makePair(): Pair = Pair(1, 2)

// A non-null reference field makes a struct non-zero-initializable.
struct NamedPair {
    val name: String
    val count: int
    constructor(name: String, count: int) {
        this.name = name
        this.count = count
    }
}

func defaultNamedPair(): NamedPair {
    // Negative design shape: default<NamedPair>() is rejected.
    NamedPair("name", 1)
}

// A valid explicit constructor can initialize a type that default<T>() cannot zero-initialize.
func explicitNamedPair(): NamedPair = NamedPair("complete", 2)

// A nullable reference field can begin as null under its declared type contract.
struct NullableName {
    var name: String? = null
    constructor() {}
}

func zeroNullableName(): NullableName = default<NullableName>()

// Nested zero-valid fields can participate in recursive zero analysis.
struct Coordinates {
    val x: int
    val y: int
    constructor(x: int, y: int) { this.x = x; this.y = y }
}

struct BoxedCoordinates {
    val value: Coordinates
    constructor(value: Coordinates) { this.value = value }
}

func defaultCoordinates(): Coordinates = default<Coordinates>()
func defaultBoxedCoordinates(): BoxedCoordinates = default<BoxedCoordinates>()

// A nested struct containing a non-null reference is not zero-valid.
struct NonZeroChild {
    val text: String
    constructor(text: String) { this.text = text }
}

struct NonZeroParent {
    val child: NonZeroChild
    constructor(child: NonZeroChild) { this.child = child }
}

func defaultNonZeroParent(): NonZeroParent {
    // Negative design shape: recursive layout analysis rejects zero initialization.
    NonZeroParent(NonZeroChild("valid"))
}

// An explicit parameterless constructor and default<T>() are distinct operations.
struct ExplicitDefaultValue {
    val value: int
    constructor() { this.value = 10 }
}

func callParameterlessConstructor(): ExplicitDefaultValue = ExplicitDefaultValue()
func requestZeroDefault(): ExplicitDefaultValue = default<ExplicitDefaultValue>()

// The explicit constructor may assign a value different from zero.
func explicitAndDefaultHaveDifferentValues(): int {
    val constructed = ExplicitDefaultValue()
    val zeroed = default<ExplicitDefaultValue>()
    constructed.value + zeroed.value
}

// A constructor exception does not yield an incomplete struct value.
struct CheckedValue {
    val text: String
    constructor(text: String) throws ValueFailure {
        if text.byteLength == 0 { throw ValueFailure() }
        this.text = text
    }
}

func constructCheckedValue(text: String): CheckedValue throws ValueFailure {
    CheckedValue(text)
}

// A caller cannot read fields from a value whose constructor threw.
func noValueOnStructThrow(text: String): int throws ValueFailure {
    val value = CheckedValue(text)
    value.text.byteLength
}

// A field assignment that throws before completion leaves the struct invalid and unreturned.
func fieldExpressionThrows(text: String): CheckedValue throws ValueFailure {
    CheckedValue(validateText(text))
}

func validateText(text: String): String throws ValueFailure {
    if text.byteLength == 0 { throw ValueFailure() }
    text
}

// An array literal publishes only after each element expression has a valid value.
func arrayOfPairs(): []Pair = [Pair(1, 2), Pair(3, 4)]

// If one element constructor throws, the array value is not returned partially initialized.
func arrayWithFailingElement(text: String): []CheckedValue throws ValueFailure {
    [CheckedValue("ok"), CheckedValue(text), CheckedValue("later")]
}

// Earlier element side effects remain even if a later element construction fails.
func elementOrderBeforeFailure(text: String): []CheckedValue throws ValueFailure {
    [markElement(1), CheckedValue(text), markElement(2)]
}

func markElement(value: int): CheckedValue {
    CheckedValue(value.to_string())
}

// A fixed-size inline array is valid only when every element has a valid value.
func inlineArrayOfValues(): [2]Pair = [Pair(1, 1), Pair(2, 2)]

// default<[N]T>() depends on element zero-validity, not on constructor availability.
func defaultInlineArray(): [2]Coordinates = default<[2]Coordinates>()

// Non-zero-defaultable element types cannot be synthesized into zeroed array contents.
func invalidDefaultInlineArray(): [2]NamedPair {
    // Negative design shape: default<[2]NamedPair>() is rejected.
    [NamedPair("a", 1), NamedPair("b", 2)]
}

// Array allocation and initializer callbacks cannot expose partially initialized elements.
func fillArrayAfterValidation(): []NamedPair {
    [NamedPair("one", 1), NamedPair("two", 2)]
}

// A nullable value wrapper can represent absence even when its payload is not zero-valid.
func absentNonZeroStruct(): NamedPair? = null

// Nullable payload storage is inactive when the nullable tag is null.
func nullPayloadDoesNotReadUninitializedFields(): int {
    val maybe: NamedPair? = null
    if maybe == null { return 0 }
    maybe.count
}

// A nullable struct copy preserves its active value tag and payload.
func copyNullableStruct(value: NamedPair?): NamedPair? = value

// A ref struct has the same required-field invariant and an additional escape limit.
ref struct RefPair {
    val left: String
    val right: String
    constructor(left: String, right: String) {
        this.left = left
        this.right = right
    }
}

func makeRefPair(): RefPair = RefPair("left", "right")

// A ref struct cannot be boxed to bypass its stack-bound lifetime.
func boxRefPair(value: RefPair): Object {
    // Negative design shape: boxing a byref-like value is rejected.
    value.left
}

// A ref struct cannot be stored in an ordinary heap array.
func arrayOfRefPairs(value: RefPair): []RefPair {
    // Negative design shape: []RefPair storage is rejected without an allowed ref-like owner.
    []
}

// A ref struct cannot escape through an ordinary heap field.
class RefPairOwner {
    val pair: RefPair
    constructor(pair: RefPair) { this.pair = pair }
}

// A local ref struct remains valid within its stack lifetime.
func localRefPairLength(): int {
    val pair = RefPair("a", "b")
    pair.left.byteLength + pair.right.byteLength
}

// A ref struct passed by value is copied under its declared value semantics.
func readRefPair(value: RefPair): int {
    value.left.byteLength + value.right.byteLength
}

// A struct assignment copies a valid value; it cannot be used to publish a partial source.
func copyCompletePair(value: Pair): Pair = value

// A default local must be both zero-valid and definitely assigned.
func defaultLocalPair(): int {
    val value = default<Pair>()
    value.left + value.right
}

// An uninitialized local of a non-zero-defaultable type cannot be read.
func uninitializedNamedLocal(): int {
    var value: NamedPair
    // Negative: the local has no valid value before explicit assignment.
    value.count
}

// Explicit assignment establishes the whole struct value atomically at source level.
func assignNamedLocal(): int {
    var value: NamedPair
    value = NamedPair("complete", 3)
    value.count
}

// A throwing RHS does not assign the destination struct local.
func failedNamedLocalAssignment(text: String): int throws ValueFailure {
    var value: NamedPair
    value = makeNamedPair(validateText(text))
    value.count
}

func makeNamedPair(text: String): NamedPair = NamedPair(text, 4)

// Nested field writes do not create a complete value until all required members are valid.
struct NestedFieldValue {
    val child: NamedPair
    val label: String
    constructor(child: NamedPair, label: String) {
        this.child = child
        this.label = label
    }
}

func createNestedFieldValue(): NestedFieldValue {
    NestedFieldValue(NamedPair("child", 5), "parent")
}

// A normal constructor can replace default<T>() only when explicitly called.
func explicitValueIsNotDefault(): int {
    val constructed = Coordinates(6, 7)
    val defaultValue = default<Coordinates>()
    constructed.x + constructed.y + defaultValue.x + defaultValue.y
}

// The GC traces only active reference fields of a valid struct payload.
func referencePayloadLayout(value: NamedPair?): String? {
    if value == null { return null }
    value.name
}

// Specification note: constructor execution does not redefine zero-initialization eligibility.
// Specification note: arrays publish only after every element is a valid value.
// Specification note: ref-like values obey field validity plus no-box/no-heap-escape rules.
// Specification note: nullable tags govern whether a non-zero-defaultable payload is active.
// Specification note: GC layout and source field analysis use the same resolved value layout.
