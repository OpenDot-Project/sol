// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: source capture inventories and deterministic source-only field/lifetime candidates are
// implemented; heap/stack environment construction, closure ABI, and Yellow Tea tracing for
// capturing function values are not yet implemented.
module source.anonymous_function_environment

// Closure value model
// ===================
//
// A function value created by an anonymous function consists semantically of a callable body and
// the lexical values that body captures. A capture is resolved at the function's source location
// and keeps the identity of its declaration. A same-spelled declaration in another scope is a
// different value. Receiver state (`this`, `super`, and implicit instance members) is captured as
// the identity of the nearest owning receiver, not reconstructed by searching for a member name
// when the function later runs.
//
// Creating a function value evaluates the capture inputs and establishes the callable value. It
// does not execute the function body. The expression's checked effects are those of constructing
// the value; checked effects from the body are recorded in its function type and are raised when
// the value is invoked. A closure's source lifetime may outlive the lexical activation that created
// it when the value is returned, stored in an aggregate, or retained by another callable.
//
// This semantic model is independent of physical layout. The compiler may use a code pointer and
// environment pointer, a generated nominal environment, or another verified representation. It
// may choose stack storage only when every use is proven to remain within that storage's lifetime.
// The source-language contract never permits an environment pointer to refer to expired local
// storage.

func returnedValue(seed: int): () -> int {
    val callback = func(): int { seed }
    callback
}

func invokeReturnedValue(): int {
    val callback = returnedValue(42)
    callback()
}

// Immutable values and mutable cells
// ==================================
//
// A captured immutable local (`val`) denotes the value established by its single initialization.
// A captured mutable local (`var`) denotes one shared lexical storage cell. All closures that
// capture that declaration observe the same cell and therefore observe each other's writes. The
// compiler must not independently copy a mutable value into each closure if that would make
// updates invisible across closures.
//
// Capture initialization occurs at the closure creation point. A function value does not re-run
// the local initializer when invoked. A reference-valued `val` capture retains the referenced
// object identity; mutation of that object through another alias remains visible under ordinary
// object semantics. Capturing an immutable binding does not make the referenced object deeply
// immutable.

func counterPair(start: int): () -> int {
    var current = start
    val next = func(): int { current += 1; current }
    val read = func(): int { current }
    val pair: () -> int = func(): int { next() + read() }
    pair
}

func immutableSnapshot(seed: int): () -> int {
    val captured = seed
    val callback = func(): int { captured }
    callback
}

func objectIdentity(item: Box): () -> int {
    val captured = item
    val callback = func(): int { captured.value }
    callback
}

class Box(var value: int) {}

// In `counterPair`, `next`, `read`, and `pair` all observe the same `current` declaration. Replacing
// one environment's field with a copied integer would violate the observable behavior. In
// `immutableSnapshot`, the binding is immutable but the value could still be an object reference;
// no deep-copy guarantee is implied. For `objectIdentity`, the closure keeps the Box identity, and
// a mutation of that Box's `value` property follows normal reference semantics.

// Nested capture propagation
// ==========================
//
// A nested closure may capture a declaration from any lexically enclosing callable. Every
// intermediate closure environment that is needed to make the nested function later callable
// must preserve the path to that same declaration. The intermediate closure need not itself read
// the value. Each nested closure's function type and local symbol table remain independent.
//
// Repeated same-named declarations are resolved before capture propagation. A deeper local
// shadows an outer local for references after the declaration. Environment construction must
// retain the resolved LocalSymbol identity through every intermediate closure and must not
// re-resolve by name at invocation time.

func nested(seed: int): () -> () -> int {
    val middle = func(): () -> int {
        val inner = func(): int { seed }
        inner
    }
    middle
}

func nestedShadow(outer: int): () -> () -> int {
    val middle = func(): () -> int {
        val outer = 7
        val inner = func(): int { outer }
        inner
    }
    middle
}

func nestedParameter(): (int) -> () -> int {
    val outer = func(value: int): () -> int {
        val inner = func(): int { value }
        inner
    }
    outer
}

// The innermost closure in `nested` reads the top-level callable's `seed`; the returned middle
// closure must carry the environment link needed to create or invoke the inner closure later. In
// `nestedShadow`, the inner closure reads the local declared by `middle`, not the outer parameter.
// In `nestedParameter`, the captured `value` belongs to the first anonymous function activation.

// Receiver capture
// ================
//
// A closure that uses `this`, `super`, an unqualified instance member, a constructor property, or
// accessor `field` retains the nearest receiver identity needed to perform that operation. An
// inherited member call uses the same derived receiver with normal virtual-dispatch rules. A
// captured receiver does not become an independent copy of the object. Explicitly qualified
// access through another object retains that object expression's value and does not implicitly
// retain the current receiver.
//
// If an instance member name is shadowed by a local or parameter, the local binding wins and no
// receiver is captured for that reference. Access to a static-less `object` declaration follows
// ordinary object member rules; no hidden instance receiver is fabricated for a stateless
// top-level object method. The source-only environment planner represents this requirement with
// `ReceiverCapture`, separate from a borrowed ref/out local. A ref-like receiver that may escape
// remains an unknown lifetime candidate because its receiver storage cannot be retained by an
// ordinary heap environment.

open class Base {
    open func value(): int = 1
}

class Child: Base() {
    val property: int = 42

    func boundReceiver(): () -> int {
        val callback = func(): int { this.property + super.value() }
        callback
    }

    func inheritedReceiver(): () -> int {
        val callback = func(): int { value() }
        callback
    }

    func shadowReceiver(value: int): () -> int {
        val callback = func(): int { value }
        callback
    }

    func otherReceiver(other: Child): () -> int {
        val callback = func(): int { other.property }
        callback
    }
}

// Capture timing and control flow
// ===============================
//
// A function value captures the lexical binding selected when its expression is evaluated.
// Branches that create different closures produce different values with the same or compatible
// function type. Assigning one branch's value to another does not merge the environments. A
// mutable local is captured as a shared cell only for closures whose resolved references target
// that local declaration.
//
// A closure is not invoked by a try/catch merely because its creation is inside the protected
// region. Its checked effects are charged only when called. `finally` executes according to
// ordinary control-flow semantics; a closure declared inside a finally block is not invoked unless
// source code calls it. Capture creation itself must still preserve any references needed by the
// new environment across allocations or safepoints.

func branchCaptures(flag: bool, first: Box, second: Box): () -> int {
    var callback: () -> int = func(): int { first.value }
    if (flag) { callback = func(): int { second.value } }
    callback
}

func createdBeforeMutation(seed: int): () -> int {
    var value = seed
    val callback = func(): int { value }
    value = seed + 1
    callback
}

func deferredThrowingBody(): () -> int throws IOException {
    val callback = func(): int throws IOException { readFile() }
    callback
}

open class IOException: Exception {}
func readFile(): int throws IOException { return 0 }

// `createdBeforeMutation`'s returned function reads the shared mutable cell, so it observes the
// later assignment. The `deferredThrowingBody` factory does not itself execute `readFile`; the
// returned function type carries IOException and invocation raises it if uncaught.

// Borrowed storage and ref-like values
// ====================================
//
// A `ref` or `out` parameter denotes borrowed storage owned by the caller. The current language
// contract forbids capturing that storage into an anonymous function because the function value
// could outlive the caller's addressable location. The same restriction applies to ref-like values
// whose representation is stack-bound or contains a borrow. Passing a captured value as `ref` or
// `out` does not change its origin or extend its lifetime.
//
// The restriction applies through nested environments. An inner closure cannot capture a ref
// parameter from an enclosing closure, and an intermediate closure cannot make an otherwise
// invalid borrowed capture safe. A future scoped-callback feature would need an explicit
// non-escape contract, exact lifetime relation, backend ABI, and verifier proof; ordinary function
// types do not imply such a contract.

func rejectedRefCapture(ref value: int) {
    val callback = func(): int { value }
}

func rejectedOutCapture(out value: int) {
    val callback = func() { value = 1 }
}

ref struct BorrowedBuffer {}

func rejectedRefLikeCapture(buffer: BorrowedBuffer) {
    val callback = func(): BorrowedBuffer { buffer }
}

// Stack and heap lifetime
// =======================
//
// Escape classification is a source-analysis aid, not the closure lifetime contract. If a
// function value escapes the creating activation, all captured locals and receiver identities
// remain valid for the value's lifetime. If the value is proven not to escape, a backend may still
// choose heap storage; stack placement is permitted only after a verified use/lifetime proof.
// Conversely, a source-analysis result of Unknown is not permission to use stack storage.
//
// Alias chains carry the same closure identity. Reassigning a mutable alias does not mutate a
// closure's captured cells; it changes which closure value that variable holds. A closure and a
// mutable cell are separate objects semantically. If two closures capture the same local, their
// environments refer to the same cell even if the closures themselves are copied into separate
// variables or aggregates.

// The source-only environment planners expose ordered field candidates for lambdas and anonymous
// functions. Receiver candidates precede local captures; local fields use resolved declaration
// identity and source order. The ordinal is only a deterministic inventory position. When no
// source capture fields are required, the planner reports `None`; this says no environment state
// is needed by the inventory and does not claim a code-pointer ABI. A `Stack` or `Heap` lifetime
// candidate is not an allocation choice: it has no byte offsets, alignment,
// object-header shape, callable ABI, Yellow Tea descriptor, root map, or write-barrier proof.
// Missing body/type/lookup facts, unknown escape use, an escaping borrowed capture, or an escaping
// ref-like receiver keeps the candidate `Unknown`. `HasVerifiedAbi` and `HasVerifiedLayout` remain
// false for these source plans.

func escapingCell(seed: int): () -> int {
    var value = seed
    val read = func(): int { value }
    value += 1
    read
}

func sharedCellPair(seed: int): [](() -> int) {
    var value = seed
    val read = func(): int { value }
    val increment = func(): int { value += 1; value }
    [read, increment]
}

func reassignFunctionAlias(seed: int, replace: bool): () -> int {
    var callback = func(): int { seed }
    if replace { callback = func(): int { seed + 1 } }
    callback
}

// Yellow Tea and managed reference tracing
// ========================================
//
// Any heap closure environment that contains managed references must be visible to Yellow Tea's
// tracing collector. The environment descriptor must describe every reference-bearing field and
// preserve the exact layout used by code generation. An environment reachable from a managed
// object, static root, thread root, or other live closure root must remain reachable through
// collection. A raw native code pointer contains no environment reference and does not satisfy
// this requirement.
//
// Assigning a managed reference into a heap-resident mutable captured cell must use the same
// write-barrier contract as any other managed heap store. Collection during environment
// initialization or closure invocation must see an initialized, valid descriptor and all live
// references. A closure allocation that can safepoint must keep its capture values rooted until
// the environment takes ownership of them.
//
// The collector's implementation strategy does not alter observable capture semantics. Whether
// collection is generational, region-aware, concurrent, incremental, or stop-the-world, a live
// closure must retain its captured object graph and mutation must preserve shared-cell identity.
// Runtime verification and stress tests must cover closure allocation, mutation, promotion,
// concurrent/parallel collection rules, exceptions, and reclamation after the last closure root
// disappears.

class Holder(var value: String) {}

func heapReferenceCapture(holder: Holder): () -> String {
    val callback = func(): String { holder.value }
    callback
}

func heapCellReferenceCapture(holder: Holder): () -> String {
    var current = holder
    val callback = func(): String { current.value }
    callback
}

// `heapReferenceCapture` retains the Holder identity. `heapCellReferenceCapture` retains a
// mutable cell that itself points to Holder. Updating the captured cell to a different Holder
// would change the closure's later observation; updating the old Holder's property is ordinary
// object mutation. Both the environment and the cell/reference edge need correct tracing.

// Host, Guest, and process boundaries
// ==================================
//
// A closure environment belongs to the heap and process that allocated it. A Host managed object
// reference is not a Guest reference and cannot be scanned by the Guest collector using the
// Guest's layout assumptions. A Guest closure cannot escape its isolated process as an in-process
// pointer. Crossing the Host/Guest boundary requires an explicit RPC/serialization or handle
// protocol that preserves lifetime, authorization, failure, and ownership semantics. Ordinary
// function-type conversion does not transfer a closure between heaps or processes.
//
// A code pointer and an environment pointer are process-local values. They are not serializable
// package data, registry data, or persistent storage. A package manager or build cache must never
// persist them as artifacts. A Sol function value cannot be sent to another process merely by
// copying its bits; a future remote callback mechanism would be a separate API and ABI.
//
// Process isolation applies in both directions. The Host GC must not traverse arbitrary Guest
// pointers, and the Guest GC must not traverse Host pointers. A bridge must hold the appropriate
// runtime handle on the owning side, and release it according to the bridge contract. Until that
// representation exists, source code cannot pass capturing function values across an isolated
// runtime boundary.

// Current native implementation boundary
// =======================================
//
// Body analysis and LexicalAnalysis can report local captures, receiver captures, and conservative
// escape uses. The current native anonymous-function path emits only closed, noncapturing code
// pointers with an exact function type. It does not allocate stack or heap environments, does not
// synthesize mutable cells, and does not add Yellow Tea descriptors or barriers for closure
// objects. Capturing function values are rejected by FIR before LLVM code generation.
//
// Source validity, capture semantics, escape classification, and native emission are separate
// stages. An unsupported closure must remain a clear FIR diagnostic; the compiler must not erase
// captured references, reinterpret a code pointer as a closure, create an unrooted native object,
// or emit a function pointer with a mismatched environment ABI.

object Main {
    func main(args: []String) {
        val callback: (int) -> int = func(value: int) { value + 1 }
        println(callback(41))
    }
}
