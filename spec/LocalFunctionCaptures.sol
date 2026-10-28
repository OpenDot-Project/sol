// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: lexical captures, value-escape facts, and source-only local environment candidates are
// implemented. Body analysis rejects a borrowed/ref-like capture unless the local function is
// proven nonescaping. Receiver inventories are separate from local-symbol captures. Capture
// allocation, closure ABI, and native lowering remain unimplemented.
module source.local_function_captures

// Capture identity
// ================
//
// A local named function is a `func` declaration nested in another callable body. A reference in
// its body to a local or parameter declared in an outer lexical scope resolves to the exact
// `LocalSymbol` identity, not just the identifier spelling. Shadowing therefore changes the
// referenced symbol, and a parameter declared by the local function is not an outer capture.
// Module-level and type-member functions are not local functions for this inventory.

func captureParametersAndLocals(seed: int): int {
    var count = 0
    func addSeed(): int {
        count += seed
        count
    }
    addSeed()
}

func parameterShadowsOuter(item: int): int {
    func identity(item: int): int = item
    identity(item)
}

func memberFunctionIsNotLocal(): int {
    func topLevelValue(): int = 1
    topLevelValue()
}

// In `captureParametersAndLocals`, the local function has two capture entries: `count` with
// ReadWrite access and the outer `seed` with Read access. The local function parameter in
// `parameterShadowsOuter` resolves to its own symbol, so that reference is not a capture of the
// outer `item` parameter.

// Read/write access
// =================
//
// Access is the union of resolved operations performed through references to one captured symbol.
// A plain read is Read; `=` is Write; compound assignment and increment/decrement are ReadWrite.
// Passing a captured local as `ref` is ReadWrite, and `out` is Write. Grouping around the location
// does not change symbol identity. This access summary does not encode definite assignment, prove
// the enclosing function has run, or permit a ref/out location to escape.

func parameterModes(ref value: int, out destination: int) {
    func mutate() {
        value += 1
        destination = value
    }
    mutate()
}

func refOutCalls(ref value: int, out destination: int) {
    func nested() {
        consume(ref value)
        fill(out destination)
    }
    nested()
}

func consume(ref value: int) {}
func fill(out destination: int) { destination = 0 }

// `mutate` records `value` as ReadWrite and `destination` as Write. `nested` records `value` as
// ReadWrite and `destination` as Write because those names are passed using their explicit modes.
// The inventory retains `LocalSymbol.PassingMode` separately from capture access; it does not turn
// a borrowed caller location into a value copy or closure field.

// Nested local-function boundaries
// ================================
//
// If an innermost local function references a declaration outside several nested local functions,
// each crossed local-function boundary records that same symbol identity. Intermediate functions
// need the environment path even when their own body does not read the value. A function's own
// recursive name also resolves to the same local function symbol and appears in the lexical
// inventory; a call from the enclosing function is a separate reference outside that function
// boundary.

func nested(seed: int): int {
    func middle(): int {
        func inner(): int = seed
        inner()
    }
    middle()
}

func recursiveLocal(steps: int): int {
    func descend(value: int): int {
        if value == 0 { return 0 }
        descend(value - 1)
    }
    descend(steps)
}

// `nested` records `seed` for both `middle` and `inner`, using one LocalSymbol. In `recursiveLocal`,
// the `descend` reference inside its own body is an inventory entry for the local function symbol;
// the call in `recursiveLocal`'s outer body does not cross the nested function boundary.

// Scope and representation boundaries
// ====================================
//
// `LexicalAnalysis.LocalFunctionCaptures` is ordered by function source path/span and local symbol
// identity. `GetLocalFunctionCaptures(function)` accepts only a function node owned by that
// lexical-analysis snapshot and returns an empty list for an owned function with no local captures.
// The query records source symbol use only. It does not describe receiver/member requirements,
// classify escape, select stack versus heap storage, compute field offsets, produce Yellow Tea root
// descriptors, or authorize FIR/native closure emission.

func moduleLevel(seed: int): int = seed

class Box(val field: int) {
    func member(): int = field

    func withLocal(): int {
        func local(): int = field
        local()
    }
}

// The module-level and `Box.member` functions are excluded because they are not local named
// functions. `withLocal`'s local function may have receiver requirements, which are recorded in
// `LocalFunctionReceiverCaptures` separately from LocalSymbol captures.

// Local named-function values and escape uses
// ===========================================
//
// Each local named-function declaration is paired with its resolved LocalSymbol identity. Same-
// spelled declarations in sibling or nested scopes remain independent. The analyzer does not join
// by spelling or infer the result of overload resolution from source order. It records every local
// function, including an unused function with no captured locals.
//
// A name used directly as an AstCall target is DirectInvocation. Calls in the declaration's own
// body include recursion and remain local invocations. A direct invocation is not a function-value
// escape. Returning the function name as a value, placing it in an array/tuple, or storing it into
// a nonlocal field is a publication-capable use and classifies the function as MayEscape. Call
// arguments are mapped to the resolved overload's parameter contract. A known ordinary
// function-valued parameter is nonescaping; an explicit @escaping parameter is MayEscape. If the
// call or mapping cannot be proved before overload binding, the argument remains conservatively
// escape-capable or unknown.
//
// Local aliases are propagated through resolved LocalSymbol identities, including grouping,
// chains, mutable writes, cycles, and branch joins. A chain used only for local invocation or passed
// to a known nonescaping parameter remains ProvenNonEscaping. Returning an alias, passing it to an
// @escaping/unknown consumer, storing it in an aggregate/field, or capturing it in another callable
// contributes that use to the originating local function. Unknown mutation or unsupported flow
// remains Unknown. A modeled publication dominates unknown uses and yields MayEscape.

func directLocalCalls(seed: int): int {
    func read(): int = seed
    read()
}

func returnLocalFunction(seed: int): () -> int {
    func read(): int = seed
    return read
}

func publishLocalFunction(seed: int) {
    func read(): int = seed
    publish(read)
    val callbacks = [read]
}

func aliasLocalFunction(seed: int) {
    func read(): int = seed
    val first = read
    val alias = ((first))
    alias()
}

func forwardAlias(seed: int): () -> int {
    func read(): int = seed
    val alias = read
    return alias
}

func borrowedAlias(ref value: int): int {
    func read(): int = value
    val alias = read
    alias()
}

func retainLocalFunction(seed: int) {
    func read(): int = seed
    val callback = { read() }
    callback()
}

// `directLocalCalls` and `aliasLocalFunction` are ProvenNonEscaping because all invocations stay
// in local storage. `forwardAlias` is MayEscape because the returned alias publishes `read`.
// `borrowedAlias` may borrow caller storage because its alias invocation remains local. The nested
// callback example remains Unknown because that callable retains the binding.

// Borrowed capture rule
// ====================
//
// Body analysis permits a local named function to capture ref/out storage or a ref-like value only
// when all modeled uses prove it nonescaping. Direct calls and unused local functions can meet that
// rule. Returning the function, publishing it, or leaving its lifetime unknown reports SOL2262 at
// the captured source references. This source proof does not imply that a native environment can
// currently be allocated; FIR still rejects capturing local functions.

// Source-only local function environment candidates
// =================================================
//
// `LocalFunctionEnvironmentPlanner` combines the resolved `LocalFunctionCapture` entries with
// local-function escape uses and body-analysis local types. Fields are ordered by declaration source
// path/span and symbol identity. Candidate storage is BorrowedLocation for ref/out parameters,
// NestedCallableEnvironment for captured local functions, SharedMutableCell for mutable locals,
// and ValueCopy for immutable locals. Missing body or type facts remain explicit uncertainties.
//
// An empty capture inventory receives a None candidate. A completed direct-call-only environment
// with capture fields can receive a Stack candidate; a completed environment with a modeled
// publication and capture fields can receive a Heap candidate. These names are analysis candidates,
// not allocations. An unknown escape, incomplete receiver lookup, unavailable owner type, or an
// escaping borrowed/ref-like capture forces Unknown. Explicit this/super, implicit instance members,
// inherited member candidates, and constructor-property access become separate receiver fields
// owned by the nearest nominal declaration. Nested local-function boundaries each carry the same
// receiver identity when an inner function needs it. ReceiverCapture remains source-only: a class
// receiver needs a future Yellow Tea managed-reference root, and an escaping ref-like receiver
// keeps lifetime Unknown. Receiver fields remain separate from lambda and anonymous-function
// receiver inventories.
//
// Environment fields do not define native offsets, alignment, code-pointer representation, object
// headers, Yellow Tea descriptors, roots, or barriers. `HasVerifiedAbi` and `HasVerifiedLayout`
// remain false. These source results do not authorize FIR or native lowering.

class CallbackOwner(val value: int) {
    func create(): () -> int {
        func read(): int = value
        return read
    }
}

ref struct BorrowedValue { val value: int }

func escapingBorrowedCapture(ref value: int): () -> int {
    func read(): int = value
    return read
}

func escapingRefLikeCapture(value: BorrowedValue): () -> int {
    func read(): int = value.value
    return read
}
