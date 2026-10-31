// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: local and receiver inventories, conservative escape classification, and source
// admission of proven-nonescaping borrowed captures are implemented. The @escaping parameter
// contract is specified; closure allocation and emission are not.
module source.anonymous_function_captures

// Identity, not spelling
// ======================
//
// A capture is a reference whose resolved lexical symbol is owned by a scope outside the
// anonymous function. The compiler records the LocalSymbol identity, not only the source name.
// Shadowing therefore creates distinct captures even when the spelling is identical. A local
// parameter inside the anonymous function shadows a same-named outer local and is not a capture.
// A property or inherited member is not treated as a local merely because its spelling matches a
// local identifier; member lookup and receiver analysis determine its owner.
//
// A capture inventory is evidence about source binding. It does not allocate an environment,
// choose whether a local is copied or cell-backed, establish a borrow lifetime, or make the
// function value eligible for FIR. A capture-free code pointer and a function value with an
// environment are different runtime representations.

func oneLocal(seed: int): () -> int {
    val callback = func(): int { seed }
    callback
}

func sameSpellingHasDifferentIdentity(seed: int): () -> int {
    val outer = func(): int {
        val seed = 20
        val inner = func(): int { seed }
        inner()
    }
    outer
}

func parameterShadowsOuter(seed: int): (int) -> int {
    val callback: (int) -> int = func(seed: int): int { seed }
    callback
}

// In `sameSpellingHasDifferentIdentity`, the inner function captures the local parameter declared
// inside `outer`; it does not capture `oneLocal`'s `seed`. In `parameterShadowsOuter`, the
// anonymous function reads its own parameter, so its capture inventory is empty. Inventories from
// multiple files use each source path and span with the symbol identity to avoid collisions.

// Access mode and source order
// ===========================
//
// For each captured local, read/write access is the union of all resolved operations in the
// anonymous body. A read of the identifier adds Read. A simple assignment to the identifier adds
// Write. Compound assignment and prefix/postfix mutation add ReadWrite. Passing a local as `ref`
// adds ReadWrite; passing it as `out` adds Write; ordinary by-value use adds Read. Grouping and
// transparent source wrappers do not alter the target symbol identity.
//
// References in one capture entry are ordered by their UTF-8 source byte offset. Separate local
// symbols are never collapsed because they share a spelling. The capture's storage category is
// an advisory source classification: immutable locals are candidate copies, mutable locals need
// shared-cell semantics, ref/out parameters are borrowed locations, and captured local callables
// need callable-environment representation. None is an emitted storage layout.

func readAndWrite(seed: int): () -> int {
    var value = seed
    val callback = func(): int {
        val previous = value
        value += 1
        previous + value
    }
    callback
}

func prefixAndPostfix(seed: int): () -> int {
    var value = seed
    val callback = func(): int {
        ++value
        value++
    }
    callback
}

func capturesOutWrite(out destination: int): () -> () {
    val callback = func() { destination = 7 }
    callback
}

func capturesRefReadWrite(ref count: int): () -> int {
    val callback = func(): int { count += 1; count }
    callback
}

// Nested environment paths
// ========================
//
// If an innermost anonymous function reads a symbol owned by an outer callable, every anonymous
// function scope between the reference and the owning scope records that symbol as an environment
// requirement. The intermediate function does not need to read the symbol in its own body for its
// environment to carry the requirement onward. A parameter owned by the immediately enclosing
// anonymous function is not a capture of that function; it is a capture only of a deeper nested
// function.
//
// Capture entries for each nested function retain the same LocalSymbol identity. They must not be
// recreated by looking up the identifier text in a different lexical scope. If two same-named
// declarations occur at different nesting depths, each inner reference resolves to exactly one
// of them before environment propagation.

func nestedCapture(seed: int): () -> () -> int {
    val middle = func(): () -> int {
        val inner = func(): int { seed }
        inner
    }
    middle
}

func nestedOwnParameter(seed: int): () -> (int) -> int {
    val middle = func(): (int) -> int {
        val inner = func(value: int): int { value }
        inner
    }
    middle
}

func nestedShadow(seed: int): () -> () -> int {
    val middle = func(): () -> int {
        val seed = 10
        val inner = func(): int { seed }
        inner
    }
    middle
}

// Receiver capture is a distinct inventory
// ========================================
//
// An anonymous function can require an enclosing nominal receiver without capturing any local
// symbol. These receiver obligations are queried separately from local captures. `this` and
// `super` identify the nearest owning class, object, or other receiver-bearing declaration.
// Unqualified instance members and accessor `field` also need that receiver. A member inherited
// from a base type retains the resolved declaration candidate and still uses the receiver owned
// by the nearest derived instance.
//
// A same-named function parameter or local binding shadows an unqualified member and avoids a
// receiver capture. `other.member` uses `other`; it does not capture `this` merely because the
// member name exists in the enclosing type. A constructor property is receiver state, not a
// standalone lexical local.

open class BaseCounter {
    open val initial: int = 0
    open func next(): int = initial + 1
}

class Counter(val initial: int): BaseCounter() {
    val callback: () -> int = func(): int { this.initial }

    func inheritedReceiver(): () -> int {
        val callback = func(): int { next() }
        callback
    }

    func shadowMember(initial: int): () -> int {
        val callback = func(): int { initial }
        callback
    }

    func anotherReceiver(other: Counter): () -> int {
        val callback = func(): int { other.initial }
        callback
    }

    func backingFieldCapture(): () -> int {
        val callback = func(): int { field }
        callback
    }
}

object GlobalFunctions {
    func stateless(): () -> int {
        val callback = func(): int { 42 }
        callback
    }
}

// The implicit call to `next()` in `inheritedReceiver` requires the Counter receiver; the member
// declaration itself belongs to BaseCounter. `shadowMember` reads its parameter, not the property.
// `anotherReceiver` captures the separate `other` local if needed by an eventual closure layout,
// while the anonymous-function receiver inventory remains empty. `backingFieldCapture` requires
// the Counter receiver because the backing field is storage belonging to that property.

// Escape uses and local aliases
// ============================
//
// `AnonymousFunctionEscape` describes source uses, separately from local and receiver captures.
// `ProvenNonEscaping` means every modeled use stays within the current activation according to
// this classifier. `MayEscape` means a source operation permits publication outside that
// activation. `Unknown` means the alias/control-flow/consumer shape is not modeled strongly enough
// to prove either classification. Unknown must never be promoted to non-escaping by assuming a
// callback does not retain its argument.
//
// Direct invocation of a freshly created value is ProvenNonEscaping. Returning the value, passing
// it to an `@escaping` function-valued parameter, storing it in a field, or placing it in an
// aggregate is MayEscape. Passing it to a function-valued parameter without `@escaping` follows
// that declaration's no-escape contract; the callee body must be checked against the contract. A
// local write whose RHS is a tracked lambda, anonymous function, or local alias adds that callable
// identity to the target's may-alias set. The graph follows LocalSymbol identity and unions branch alternatives
// without selecting one path. Mutable locals and their aliases can therefore remain
// ProvenNonEscaping when every joined use is local storage, local aliasing, or local invocation.
// Null or otherwise untracked writes, ref/out publication, a nested callable that captures the
// alias, or an unsupported use remains Unknown or MayEscape. This is source-use evidence only; it
// does not prove closure storage or ABI safety.
//
// An unused value initialized into an immutable local does not by itself escape. The initializer
// is still recorded as local storage evidence. An alias that is later returned or passed to an
// unknown callback remains escape-capable even if an earlier use invoked it locally.

func immediateInvocation(): int {
    val callback = func(value: int): int { value + 1 }
    callback(41)
}

func localAliases(seed: int): int {
    val original = func(): int { seed }
    val firstAlias = original
    val secondAlias = firstAlias
    secondAlias()
}

func returnedValue(seed: int): () -> int {
    val callback = func(): int { seed }
    callback
}

func passedToEscaping(@escaping callback: () -> int) {
    consume(callback)
}

func consume(@escaping callback: () -> int) {}

func passedToNonescaping(callback: () -> int): int {
    invoke(callback)
}

func invoke(callback: () -> int): int { callback() }

func aggregateStorage(seed: int): []() -> int {
    [func(): int { seed }]
}

func mutableAlias(seed: int, replace: bool): () -> int {
    var callback = func(): int { seed }
    if replace { callback = func(): int { 0 } }
    callback
}

func stableMutableAlias(seed: int): int {
    var callback = func(): int { seed }
    val alias = callback
    alias()
}

func stableMutableLambdaAlias(seed: int): int {
    var callback = { seed }
    callback()
}

// Expected source-use classification (not an emitted-lifetime promise):
//
// - `immediateInvocation`: ProvenNonEscaping, one ImmediateInvocation witness.
// - `localAliases`: ProvenNonEscaping, local-storage, alias and invocation witnesses.
// - `returnedValue`: MayEscape, a ReturnValue witness through the callable tail.
// - `passedToEscaping`: MayEscape, a CallArgument witness because `consume` explicitly permits
//   publication of the function value.
// - `passedToNonescaping`: the call follows `invoke`'s nonescaping declaration contract; it does
//   not become MayEscape merely because the value crosses a call boundary.
// - `aggregateStorage`: MayEscape, an AggregateElement witness.
// - `mutableAlias`: MayEscape because its returned value can be published; both branch values are
//   retained in the joined may-alias set.
// - `stableMutableAlias` and `stableMutableLambdaAlias`: ProvenNonEscaping when all bound uses
//   are local aliases/invocations and no write, publication, or unsupported consumer is present.
//
// The returned capture list is not itself the escape classification. A capture-free function can
// still be returned or stored in an aggregate. A capturing function can be invoked immediately
// and remain local in the source-use classifier. Runtime support must independently establish
// environment ownership, roots, mutation identity, and lifetime.

// Borrowed and ref-like values
// ============================
//
// A ref/out parameter is caller-owned storage with a borrow lifetime. A closure may capture that
// storage only when every modeled use proves that the value stays inside the current activation,
// such as a local alias that is invoked before the owner returns or a call to a checked
// nonescaping-parameter contract. Returning the closure, passing it to an `@escaping` parameter,
// or any unknown alias/consumer prevents that proof and is rejected. Ref-like local values and
// receivers follow the same rule. Source analysis does not infer that an unknown callback is safe
// merely because its body is available.
//
// A ref/out parameter declared inside the anonymous function is an ordinary parameter of that
// function's own body; it is not a capture of an outer activation. Capturing it in a deeper
// anonymous function is a separate borrowed capture and is rejected by the same rule.

func nonescapingRefCapture(ref input: int): int {
    val callback = func(): int { input }
    callback()
}

ref struct BorrowedValue {
    val value: int = 42
}

func nonescapingRefLikeCapture(input: BorrowedValue): int {
    val callback = func(): int { input.value }
    callback()
}

func rejectedRefCapture(ref input: int): () -> int {
    val callback = func(): int { input }
    return callback
    // expect: SOL2262; a returned closure cannot retain caller-owned ref storage.
}

func rejectedOutCapture(out output: int): () -> () {
    output = 0
    val callback = func() { output = 42 }
    return callback
    // expect: SOL2262; escaping output storage is borrowed even after its initial assignment.
}

func callbackParameter(out output: int): () -> () {
    val callback = func(out value: int) { value = 42 }
    callback
}

func nestedBorrowCapture(ref input: int): () -> () -> int {
    val middle = func(): () -> int {
        val inner = func(): int { input }
        inner
    }
    middle
}

// Bounded lookup and deterministic results
// ========================================
//
// Capture and escape queries belong to one immutable LexicalAnalysis snapshot. Passing an AST node
// from another snapshot is an API ownership error; the analyzer must not compare source spans or
// names as a substitute for node identity. Empty queries for an owned function return an empty,
// deterministic result. Results from a multi-file analysis are ordered by normalized source path,
// byte span, and stable local identity so they do not depend on caller file enumeration order.
//
// Inherited receiver-member lookup is bounded. If the hierarchy walk reaches its documented
// budget, the receiver inventory is returned with `IsComplete == false`. An empty `Members` list
// with an incomplete inventory does not prove that no inherited member is captured. FIR must
// reject a source function whose receiver proof is incomplete.
//
// Cancellation is checked during symbol binding, nested-environment propagation, receiver lookup,
// and alias graph traversal. A cancelled analysis must terminate with the caller's cancellation
// token; it must not return a success-shaped partial inventory.

class DeepReceiverBase {
    func inherited(): int = 1
}

class DeepReceiverChild: DeepReceiverBase {
    func captureReceiver(): () -> int {
        val callback = func(): int { inherited() }
        callback
    }
}

func sourcePathOrder(first: int): () -> int {
    val callback = func(): int { first }
    callback
}

func ownedQuery(): () -> int {
    val callback = func(): int { 0 }
    callback
}

// Runtime and FIR boundary
// ========================
//
// Current native code pointers contain a target address and no environment pointer. Therefore a
// function with a local capture, receiver capture, nested closure environment, or borrowed capture
// is not eligible for this ABI, regardless of whether its source escape classification is
// ProvenNonEscaping. A lifetime classification is not a stack-allocation proof. An immutable
// local could be copied into a stack environment only after the FIR and verifier establish where
// the environment is created, every use, and every return boundary. A mutable local needs one
// shared cell when multiple closures observe it. Returned or aggregate values need managed
// environment rooting and Yellow Tea tracing.
//
// Before closure emission, the runtime contract must specify the environment descriptor, field
// alignment, reference scanning, barrier behavior, destructor/finalization rules, thread
// transfer, process boundaries, and exception unwinding. Host GC ownership cannot be inferred
// from a Guest value, and a raw code pointer cannot carry either heap's environment by itself.
// Until those rules are implemented and verified, capture analysis remains advisory and capturing
// anonymous functions are rejected by FIR at their original source expression.

object Main {
    func main(args: []String) {
        val callback: (int) -> int = func(value: int) { value + 1 }
        println(callback(41))
    }
}
