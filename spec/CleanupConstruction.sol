// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative source contract for lexical cleanup and construction effects.
// Status: syntax is represented by the initial source frontend; complete cleanup CFG,
// checked-effect proof, object publication verification, native unwind, and runtime behavior
// are not implemented. This file specifies requirements and is not a conformance test result.
module source.cleanupconstruction.rules

import std.core

// SCOPE AND CONFORMANCE LANGUAGE
//
// This contract defines two connected source-language areas. The first is `defer`: a statement
// that registers one lexical cleanup body and transfers it to every exit from the owning scope.
// The second is construction: selected constructor calls and implicit initialization operations
// contribute checked effects and establish the required-field invariant before an instance can
// become observable. The two areas meet because cleanup can execute while a constructor is
// unwinding, and because a construction call can fail after earlier argument or initializer
// work has already produced side effects.
//
// The word MUST describes a source compatibility requirement. MUST NOT is a prohibition. SHOULD
// describes a preferred implementation where another representation can preserve the same
// observable contract. A source-analysis result that is incomplete, Deferred, cancelled, or
// resource-limited is not a successful proof. Parser acceptance is not evidence that any semantic
// requirement below is implemented. CST, PSI, AST, and FIR remain the named compiler structures;
// this contract does not add another intermediate representation.
//
// The normative companions are spec/ControlFlow.sol, spec/Exceptions.sol, spec/Objects.sol,
// spec/Delegation.sol, and spec/Structs.sol. The focused narrative is docs/cleanup-construction.md.
// The independent files under spec/examples/semantics/ provide positive and negative source
// shapes. Negative source forms are annotated in comments so syntax-oriented corpus readers can
// still parse each complete fixture. No fixture should be mistaken for a successful runtime run.

// 1. TERMS AND OBSERVABLE ORDER
//
// A lexical scope is the source-defined region that owns declarations and exits as one unit.
// Callable bodies, ordinary blocks, branch bodies, switch-case bodies, catch bodies, and each
// loop-body execution establish distinct cleanup lifetimes. A declaration's indentation alone
// is not its owner; the parsed scope tree and resolved transfer target determine ownership.
//
// A registration point is the execution point immediately after control reaches a `defer`
// declaration. A deferred action is active only after that point. Registration records that the
// action is pending; it does not run the body and does not eagerly evaluate expressions inside
// that body. If execution leaves the owner before reaching the declaration, no action exists.
// If a branch or loop iteration reaches the declaration, that scope instance owns the action.
//
// An exit is a control-flow edge that leaves one or more scopes. The language distinguishes
// ordinary fallthrough, explicit return, exception throw, break, continue, and non-completing
// `!` evaluation. A transfer may leave nested scopes and one edge therefore has an ordered list
// of cleanup scopes to process. The resolved transfer target, not the nearest textual brace,
// determines that list.
//
// Cleanup order is deterministic and observable. Within one scope, later active registrations
// run before earlier active registrations. A scope nested inside another scope finishes its
// cleanup before cleanup for its parent begins. Neither hashing, source-file sort order, partial
// declaration ordering, scheduling, nor garbage collection chooses the order.
//
// Primary failure is the Throwable that was already pending when a cleanup began. If no failure
// was pending, the first cleanup failure becomes primary. Suppressed failures are cleanup
// failures that occur while a primary failure is already selected. They are retained in the
// exact temporal order in which the cleanup actions fail. Suppression never changes which
// primary Throwable a catch or caller receives.

// 2. DEFER FORM AND REGISTRATION
//
// The source form is `defer { statements }`. The body is a statement block, not a function value,
// and it is not invoked at the declaration. It is associated with the nearest lexical scope that
// contains the declaration. A label, nested lambda, nested function, or nested type may introduce
// a separate callable boundary; a defer inside that boundary belongs there and cannot be hoisted
// to an outer owner.
//
// The declaration has no expression value. It cannot appear where a value is required. An empty
// body is legal and is a no-op at exit. A parser or analyzer MUST preserve the body as executable
// syntax until effect and flow analysis have determined which exits can activate it. A formatter
// MUST preserve registration location; moving the declaration across a branch, loop, try body,
// or earlier potential exit changes behavior.
//
// Reaching the declaration is the only registration event. Source text occurring after an
// unconditional return, throw, or `!` expression is unreachable and cannot register an action.
// An action declared in a conditional arm is registered only on edges that enter and reach that
// declaration. It is not synthesized on the other arm to make the number of actions equal.
// Branch joins carry path-specific active-registration facts. If the implementation merges
// paths, it MUST retain enough information to know which action executes on each path.
//
// A loop-body registration is per dynamic execution of the body. It is neither one registration
// for the entire loop nor one registration per syntactic occurrence for the whole function.
// Each iteration's body-scope stack is drained before that iteration's exit reaches the loop
// update/condition, outer loop continuation, or transfer target. A later iteration starts with a
// fresh body scope. A loop header defer in an enclosing callable scope remains active across all
// iterations and executes only when that enclosing scope exits.
//
// Expressions inside the deferred body execute at cleanup time, in ordinary source evaluation
// order. A variable read observes the storage value visible when the action runs, subject to
// ordinary definite-assignment and capture rules. A user who needs a snapshot binds a separate
// immutable local before registering the action. The language does not silently rewrite deferred
// bodies into eager argument evaluation. Side effects from completed cleanup expressions remain
// observable if a later cleanup expression throws.
//
// A deferred body uses names and members that are in scope at the declaration under normal
// lexical lookup and access rules. Registration does not make private members public, extend a
// local's lifetime, or allow an escaping ref-like value. Borrow, ref struct, ref/out, closure
// capture, and storage restrictions apply as if the body were executed at the cleanup point.
// If a captured mutable local is shared under ordinary closure semantics, the cleanup sees the
// shared cell rather than an invented copy. A defer is not a finalizer and does not extend the
// lifetime of an owner or resource past the lexical exit.
//
// A cleanup body MAY call ordinary functions, write in-scope mutable locations, and invoke
// selected property accessors. An ordinary called operation may raise an exception; that runtime
// failure follows the primary/suppressed and checked-effect rules below. An explicit `throw`
// statement whose exception would escape a defer/finally body is forbidden (SOL2254). An explicit
// throw caught by a try/catch wholly contained inside the cleanup body may be permitted because
// it does not escape that cleanup. A cleanup body MUST NOT use return, break, or continue to
// replace the pending transfer or target a scope outside the cleanup body. It MUST NOT create an
// outward transfer by invoking a callback that has a source-level nonlocal transfer form. A local
// nested loop may break or continue within itself if the grammar and transfer resolver can prove
// the target is wholly inside the cleanup body.
//
// A forbidden transfer is an error at its originating source span. The compiler MUST NOT erase
// it, reinterpret it as a normal cleanup completion, or let it overwrite a pending return value.
// An escaping explicit throw in defer/finally is specifically diagnosed as SOL2254. Error recovery
// may continue after reporting the transfer, but an invalid or unresolved cleanup edge MUST keep
// the body unverified and unavailable to native lowering.

// 3. SCOPE STACK AND REGISTRATION TABLE
//
// Each runtime scope instance conceptually owns an ordered sequence of registrations. An
// implementation may represent that sequence with a stack, indexed frame area, static cleanup
// ladder, or another verified representation. The representation MUST preserve identity,
// activation, order, and one-time execution. A flat list of source declarations is insufficient
// when a declaration is conditional or executes repeatedly.
//
// At a branch, a true-edge defer and false-edge defer are different action identities even when
// their bodies are textually equal. At a join, the possible active stack is a path-sensitive
// value. A cleanup planner may factor shared prefixes only if it preserves each edge's activation
// bitmap or equivalent state. It MUST NOT execute an action whose registration was skipped.
//
// At nested scope exit, process the current scope's stack in reverse registration order. Only
// after that scope is exhausted may execution continue with the next outer scope in the transfer
// chain. An inner cleanup may mutate an outer local that a later outer cleanup reads; the order
// is therefore semantically significant. A compiler cannot reorder apparently independent
// actions without a proof that their effects commute and that exception behavior is unchanged.
// The default contract is source-observable LIFO order, with no such optimization assumption.
//
// Defer body execution itself does not activate registrations declared inside it before normal
// control reaches them. If a cleanup body contains a nested scope with its own defer, that nested
// action is processed when the nested scope exits during this cleanup. The nested cleanup may
// itself fail; the enclosing action then fails according to the same primary/suppressed rule.
// A defer registered while executing a defer body cannot be appended to a scope that has already
// begun unwinding. Such a declaration is rejected unless it belongs to a distinct nested scope
// entered and exited entirely inside the body. This avoids a mutable-stack append-after-drain
// ambiguity and makes each owner finite.
//
// Once an action starts, it is attempted at most once. If it completes, it is removed from the
// active sequence. If it throws, it is considered attempted and is not retried. Remaining earlier
// actions still run. Re-entry, cancellation, thread interruption, process termination, native
// fail-fast, or GC activity is not a second invocation opportunity. The source contract makes no
// promise that cleanup runs after abrupt process termination, corrupted runtime state, or a
// deliberately unsafe native abort.

// 4. EXIT-KIND TRANSFER RULES
//
// 4.1 Ordinary fallthrough. Evaluate all statements before the scope boundary. At the boundary,
// drain the current scope. If all actions complete normally, continue at the first statement
// after the scope. If an action throws, stop the ordinary continuation and process remaining
// actions with the new primary failure pending. The normal value of a surrounding expression is
// preserved only if its required cleanup completes without creating a primary failure.
//
// 4.2 Return. Evaluate the explicit return expression exactly once before leaving the first
// exited scope. Store the resulting value in a pending-return slot whose lifetime and GC roots
// are valid throughout cleanup. If cleanup completes normally, deliver that value to the caller.
// If cleanup throws, the return is cancelled and the cleanup failure escapes as primary. If an
// earlier failure was already active, a return cannot be its pending transfer; ordinary return
// is not legal in an exception-unwind path except as part of a language construct explicitly
// defined to recover. Cleanup cannot replace one return value with another. `return` from a
// cleanup body is rejected.
//
// 4.3 Throw. In ordinary source code, evaluate the thrown expression once and establish its
// non-null Throwable identity before cleanup starts. That object is primary while every exited
// scope is drained. An exception raised by a called operation during cleanup is appended as
// suppressed; the original remains the one delivered to a matching catch or caller. The cleanup
// stack continues after each failure. If a called cleanup operation raises with no prior failure,
// its exception becomes primary. A later called cleanup operation that raises is suppressed under
// the first cleanup failure and cannot replace it. An explicit throw that would escape cleanup is
// SOL2254 and is not a legal way to produce these runtime cleanup failures; a wholly local catch
// inside cleanup may handle its own explicit throw.
//
// 4.4 Break. Resolve the target loop before computing exited scopes. Drain only scopes between
// the transfer point and target loop boundary. The target loop's own enclosing scope remains
// active. If a break leaves an iteration body, that iteration's defers run before control reaches
// the loop exit. A counted-loop update is skipped for break. Cleanup failure cancels the break
// and propagates the failure through outer scopes.
//
// 4.5 Continue. Resolve the target loop, drain scopes left by the transfer, then perform the
// loop's prescribed continuation step. For a counted loop, update evaluation follows body cleanup
// and precedes the next condition. For a collection loop, cursor advancement follows iteration
// cleanup. Cleanup failure cancels continue; no update or next-item operation occurs after the
// failure. An inner continue does not drain outer-loop or callable scopes that remain active.
//
// 4.6 Bottom expression. Evaluation of an expression of type `!` has no normal successor. If it
// throws an ordinary Throwable, active scopes unwind using the throw rule. If it aborts through a
// documented process/runtime fatal path, the language cannot promise cleanup. The type `!` alone
// does not prove which runtime mechanism implements non-completion; effect and ABI metadata must
// preserve a throw edge when a checked Throwable can be produced.
//
// 4.7 Return after finally. In a try/finally construction, the selected try or catch path first
// creates its pending exit; the finally block executes as an ordered cleanup region before that
// exit is delivered. A return in finally is forbidden. An explicit throw that escapes finally is
// also forbidden as SOL2254; an ordinary call inside finally may raise and is composed under the
// primary/suppressed policy. A defer scoped inside the try body is drained as that body exits
// before the finally body starts. A defer scoped around the whole try remains active until after
// finally completes. This ordering follows nested scope ownership, not whichever syntax node a
// lowering pass visits first.
//
// 4.8 Exceptional edges during evaluation. If an operation throws before the program reaches a
// later defer declaration, that later action is inactive. If an ordinary operation called from
// an already active defer body raises, the body is considered failed and the next earlier
// registration runs. A locally caught explicit throw does not leave the cleanup body. A
// throw during registration itself is not a separate operation in the simple block form; if a
// future registration form evaluates operands, those operands must complete before activation.
//
// A compiler MUST construct an exceptional successor at the exact potentially throwing
// operation. Assignments completed before the operation remain visible to cleanup; assignments
// after the operation do not. An out parameter assigned in cleanup is complete at a pending
// normal return only after cleanup has completed. If cleanup throws, the caller receives no
// normal-return definite-assignment guarantee for that out location.

// 5. CLEANUP EFFECTS AND FAILURE COMPOSITION
//
// The checked effect set includes every cleanup action that can execute on the edge under
// analysis. For a conditional registration, an action contributes only on paths where the
// registration is active; at a merge, the union includes each action that can be active on some
// escaping path. An action whose body is unreachable or whose owner is never exited on a path
// does not contribute to that path's escaping effects. A conservative union is permitted when
// path precision is unavailable, but it MUST NOT omit a possible checked effect.
//
// Cleanup effects are evaluated in actual execution order. They remain effects even when a
// primary exception already exists: suppressed recording changes the delivered exception object,
// not the static fact that a cleanup can throw. The checked effect set of the enclosing callable
// includes a cleanup failure that can become primary on a normal-exit path. If only a suppression
// path is reachable, the effect still needs representation in metadata and diagnostics so tools
// can report the failure. A checked effect is covered by an identical or nominal supertype in the
// callable's `throws` budget. RuntimeException descendants remain unchecked, but participate in
// suppression and catch matching.
//
// Catch coverage is based on the primary exception delivered after the protected cleanup chain.
// A catch surrounding the entire try/finally can observe a cleanup failure that replaces normal
// completion; it observes the original primary when cleanup failures were suppressed under it.
// A catch inside the protected region cannot catch a failure that the region's outer cleanup has
// not yet produced. A handler body has its own exits and cleanup scopes. A cleanup failure in a
// catch body propagates outward under the same rules.
//
// If a scope exits normally and its newest defer calls an operation that raises checked type A,
// A becomes primary. Older defers still run. If an older defer then calls an operation that raises
// checked type B, B is suppressed under A. If an
// operation was already throwing type P when cleanup starts, all cleanup failures are suppressed
// under P. Source code may catch P and inspect its suppressed collection; it cannot receive B as
// the primary result merely because B happened last.
//
// The runtime MUST keep the primary Throwable rooted while cleanup runs and while suppression is
// recorded. It MUST preserve failure identity and deterministic ordering. Suppressed storage uses
// a bounded allocation-aware policy so recording a failure does not recursively depend on an
// unbounded allocation in low-memory handling. If the recording reserve is exhausted, the
// implementation follows a documented bounded fallback and records that detail is incomplete;
// it MUST NOT overwrite the primary or claim that an unrecorded Throwable was successfully
// retained. Source-level effect checking still accounts for all possible failures regardless of
// runtime recording capacity.
//
// Foreign exceptions do not bypass this protocol. A C, C++, C++/CLI, CLR, Ruby, LLVM runtime,
// or operating-system error must be converted at a verified boundary into a Sol Throwable or an
// explicit status carrier. Arbitrary foreign unwinding through a Sol cleanup frame is outside the
// contract. Native code generation must preserve cleanup/personality/landing-pad semantics for
// every supported Sol exception edge before emitting code for a body with cleanup.

// 6. CONSTRUCTION: SELECTED OPERATIONS AND ORDER
//
// A construction expression selects one constructor using ordinary overload and argument
// binding rules. Explicit arguments evaluate once from left to right in source order. Only after
// those values have completed normally are missing defaults evaluated once in declaration order.
// A supplied argument suppresses that parameter's default expression. Effects from an argument
// or selected default are part of the construction expression's checked effect set. If argument
// evaluation fails, later arguments, defaults, allocation-visible initialization, and constructor
// body work do not run.
//
// For a supported non-partial class construction, the language order is:
// 1. evaluate explicit constructor arguments left-to-right;
// 2. evaluate selected missing defaults in declaration order;
// 3. establish the allocation and root state without publishing the new object;
// 4. invoke the selected explicit base constructor;
// 5. assign primary val/var property parameters to their storage;
// 6. initialize interface by targets in base-header declaration order;
// 7. evaluate instance field/property initializers and init blocks in declaration order;
// 8. execute the selected constructor body;
// 9. verify every required field and backing slot is valid;
// 10. publish the complete instance to the caller or another permitted owner.
//
// A construction exception at steps 1-8 exits without publishing the partial instance. Effects
// and side effects already completed are not rolled back. Any resource acquired before failure
// must be protected by a valid lexical cleanup whose lifetime includes the failing edge. The
// partially initialized storage remains private to the runtime construction frame until its
// references and resources can be safely discarded. GC sees the correct initialized-field map;
// it does not scan uninitialized non-null reference slots as if they contained valid references.
//
// For a supported struct or ref struct, the same required-field invariant applies before the
// value is returned or stored. Its exact storage is value semantics; assignment may copy only
// after construction has produced a valid value. `default<T>()` remains a separate zero-value
// operation and is legal only for the compiler-proven zero-initializable type set. A constructor
// does not change `default<T>()` into a constructor call.
//
// Primary parameters can be read as local argument values when evaluating base arguments.
// Their property storage is not assigned until the base constructor completes. The plan MUST
// distinguish constructor-only parameters from `val` and `var` property parameters.
// A secondary `this(...)` constructor delegates to one selected
// constructor in the same type and invokes the primary initialization chain exactly once. The
// secondary body runs only after delegation completes. A base-class constructor call is explicit
// and occurs before derived instance initializers. Base effects are inherited by the derived
// construction expression; a derived constructor cannot erase an effect by omitting it from its
// own source contract.
//
// Chaining graph rules: every `this(...)` edge resolves to one constructor with a compatible
// argument mapping; cycles are rejected. A constructor cannot call both `this(...)` and a base
// constructor directly. It cannot defer the base call until after an initializer or body
// statement. A class with no explicit base-call syntax follows the declared implicit-base rule
// only if a valid accessible parameterless base constructor exists; this fallback cannot select
// an overload with optional parameters. Explicit super(...) uses ordinary argument/default
// mapping. A ref struct follows its declared value-type construction rule and does
// not synthesize a reference-class base chain. Unsupported chaining forms receive diagnostics
// and do not fall back to source order or overload declaration order.

// 7. REQUIRED FIELD VALIDITY AND PUBLICATION
//
// A required instance field or non-null backing slot begins unassigned in the constructor flow
// state. It becomes assigned only after its selected initializer, assignment, or property init
// accessor has completed normally. Reading it before assignment is invalid. Publishing `this`,
// passing it to an unknown method, storing it into globally reachable state, returning it, or
// allowing a callback to observe it before all required state is valid is forbidden unless an
// explicit verifier proves the operation cannot expose or read the incomplete object.
//
// This-escape analysis includes direct calls, virtual dispatch, delegate initialization,
// lambdas, event subscription, native handles, nested constructor calls, and property accessors.
// A method call made on `this` during construction is not automatically safe because it may be
// virtual or reentrant. A final/private call can be permitted only under a proof of receiver
// non-escape, field read safety, and no user-visible observation. A failed proof is a diagnostic
// or Deferred result, never an assumption that the method is pure.
//
// An initializer expression may read earlier initialized fields if the source semantics expose
// them in that phase. It may not read a later required field. The source order of declarations
// within a non-partial initialization part is observable and fixed. An initializer that throws
// leaves its destination unassigned, and no later initializer or constructor body statement runs.
// A property accessor effect and any getter/delegate hook used by an initializer belong to the
// construction effect budget.
//
// Reference fields are scanned by GC only when their storage state is valid. Native stack maps,
// object descriptors, and partial-construction metadata agree on which slots have been
// initialized. A moving collector or safepoint during an initializer must not lose a previous
// assigned reference or treat poison/zeroed memory as a valid Sol object. This is an ABI/runtime
// requirement as well as a source definite-assignment rule.
//
// A class instance becomes source-visible only after construction normal completion and required
// field proof. A struct value becomes readable only after every field needed by its type has a
// valid value. Non-null reference fields do not become valid by allocation zeroing. Nullable
// fields may start at null when the type contract allows it. Primitive fields follow the language
// default and assignment rules for that storage category.

// 8. PARTIAL TYPES AND INITIALIZATION ORDER
//
// All parts of one partial nominal declaration are merged only when their module, nesting,
// nominal identity, generic arity, base contract, and constraints agree. Every declaration must
// carry the partial marker. Duplicate primary constructors and incompatible field declarations
// are errors. This merge does not imply that filesystem order becomes semantic order.
//
// To avoid nondeterministic side effects, at most one partial part may contain side-effectful
// field initializers or init blocks. Other parts may declare uninitialized fields, properties,
// methods, or nested types. The one initialization part has its own source declaration order.
// The complete constructor must assign every required field across all parts before publication.
// A second part with an initializer or init block is rejected even if a particular build happens
// to sort it consistently. Manifest order, path spelling, case-folding, Git enumeration, and
// parallel parser completion are not initialization-order mechanisms.
//
// The canonical field layout for partial structs is the specified logical field order from the
// object/ABI contract, not source-file order. That layout rule does not make initialization order
// arbitrary: initialization side effects still belong to the designated part. Non-partial struct
// field order follows its declaration contract. A serializer, ABI emitter, and GC descriptor
// must consume the resolved logical layout rather than re-sorting fields independently.
//
// Any unsupported partial initialization combination must fail before code generation. Silently
// ignoring an initializer, running only the first parsed part, or choosing a different order on
// Windows/Linux is incompatible behavior.

// 9. CHECKED EFFECTS AT CONSTRUCTION BOUNDARIES
//
// A class constructor may declare checked effects with `throws`. The declared budget covers its
// body, field initializers, init blocks, selected base constructor, selected delegated
// constructor, property initializers/accessors, and every required initialization operation.
// The set is based on resolved calls, defaults, generic substitution, accessors, and checked
// exception subtype identity. Unchecked RuntimeException descendants need no budget but remain
// observable failures.
//
// At a call site, the construction effect consists of the selected constructor contract plus
// explicit argument effects, selected missing-default effects, and required runtime initialization
// effects. Only operations that can execute on that source path contribute. A constructor which
// throws IOException does not force callers to declare a sibling ParseException. A budget of
// Exception covers checked descendants under Exception, not unrelated Throwable variants.
//
// An effect from a base constructor is not hidden by a derived constructor. If the base effect is
// covered by the derived declaration's supertype budget, the derived call site may use that
// contract. If it is uncovered, the derived declaration is invalid. A derived type cannot
// override constructor effects via a virtual method rule; constructors are selected operations,
// not virtual dispatch slots. Constructor overloads that differ only in `throws` do not form
// distinct overloads.
//
// Module-level values, object declarations, and companion initialization use explicit lazy state
// and declared initialization effects. States are Uninitialized, Initializing, Ready, and Failed.
// The first access may run initialization. Every access site is checked against the declared
// effect contract even if the value happens already to be Ready. Failure is cached according to
// the runtime contract and remains visible; a repeated read is not silently treated as success.
// Same-thread initialization cycles are diagnosed or fail through the declared initialization
// exception policy. Cross-thread waits must be safepoint-aware and must not publish half-state.
//
// A checked effect is not erased by adding a catch in a factory unless the catch's type actually
// covers it and the handler itself completes under its own effect budget. Wrapping is explicit:
// construct a new exception with the original cause, then declare/catch the new checked type.
// Source API metadata serializes constructor, object-initializer, accessor, and checked subtype
// information. Missing or incompatible metadata is incomplete, not pure.

// 10. INITIALIZATION WITH DELEGATES AND ACCESSORS
//
// A delegated property initializer evaluates its delegate expression once at its declaration
// point. If `provideDelegate` is selected, it runs once during initialization and its checked
// effect belongs to the owner construction contract. The resulting descriptor/value is stored
// according to the delegation metadata. Later reads invoke the selected `getValue`; later writes
// invoke `setValue`. Those accessor effects are not moved into the constructor just because the
// property is delegated.
//
// `SolProperty` is declaration metadata describing the property symbol, owner, type, and accessor
// contract. It is not a mutable storage slot for each owner's current property value. A delegated
// initializer may receive a property descriptor and a reference to an owner only under the
// this-escape and field-validity proof. A local/top-level delegation with no owner receiver does
// not create an instance-construction escape, but it retains its own lazy/module state contract.
//
// A custom getter invoked during construction can throw before the destination field is assigned.
// A setter/init accessor marks assignment only after it returns normally. A compound property
// update is not a constructor assignment unless the flow proof establishes that it initializes
// the exact required backing slot; calling an arbitrary setter on `this` is not sufficient.
// Access-control and overload selection happen before effect accumulation. Rejected candidates'
// effects do not leak into the constructor budget.
//
// Delegation capture cannot make an unready instance safe by storing it in a descriptor and
// claiming that reads happen later. Escape analysis must prove no eager callback, descriptor
// hook, native registration, or concurrent observer can reach `this` before publication.

// 11. ANALYSIS, METADATA, AND DIAGNOSTICS
//
// The flow graph represents each cleanup registration and deactivation edge. State at a program
// point includes reachable continuation, assigned local identities, assigned required fields,
// active cleanup stack state, pending transfer kind/target/value, outstanding checked effects,
// and uncertainty. Exceptional successors originate at each possibly throwing operation.
// Loop analysis models the body scope once per iteration and reaches a bounded fixed point at the
// header. Scope exits lower through ordered cleanup edges, not a textual post-pass that appends
// all defers to the function epilogue.
//
// Constructor analysis tracks an unpublished receiver separately from published references.
// It records which fields are valid at each initializer and call, selected base/this chain edges,
// and the effect contribution of each operation. Exceptional edges preserve assignments and side
// effects that completed before the throw. They do not gain assignments from operations that
// would have run later. A failure edge does not assert that an `out` parameter was assigned unless
// a separate checked contract says so.
//
// FIR verification independently checks that every source transfer has a native cleanup edge,
// that all selected effects are covered, that the constructor chain is acyclic and ordered, and
// that publication occurs only after all required fields are valid. It validates root maps,
// cleanup landing pads/personality data, and foreign exception boundaries before LLVM lowering.
// A source-only analyzer may offer navigation and diagnostics, but its facts do not authorize
// AOT or ORC code generation until FIR proof is complete.
//
// Diagnostics SHOULD distinguish at least: unreachable defer registration; invalid cleanup
// transfer; missing active-registration state at a join; cleanup effect outside the callable
// budget; failed suppression/root proof; duplicate or cyclic constructor delegation; missing base
// constructor; invalid base/this combination; read of a required field before assignment; missing
// required field on normal construction; possible `this` escape; invalid partial initializer
// part; uncovered base/initializer checked effect; and incomplete metadata. Diagnostic source
// spans point to the registration, transfer, field read/write, selected construction site, or
// missing contract that caused the failed proof. Error recovery does not make a failed constructor
// or cleanup body eligible for verified FIR.

// 12. IMPLEMENTATION STATUS AND NON-GOALS
//
// This document specifies intended language behavior. The current initial release is a CST
// frontend and does not provide semantic analysis, complete AST/FIR verification, LLVM code
// generation, native cleanup lowering, Windows exception personality integration, runtime
// exception delivery, or a runnable program. A parser accepting `defer`, `throws`, constructors,
// or initializers establishes syntax/tree behavior only. Existing source-analysis subsets do not
// demonstrate complete exceptional CFG or constructor publication proofs.
//
// The contract requires later work to connect cleanup/effect source facts to verified FIR and to
// preserve them through LLVM 23.1.3 code generation. Native code must not be produced from a body
// whose cleanup order, exception edges, root state, required fields, or effect metadata are
// incomplete. Runtime behavior, a passing unit suite, sanitizer run, or fuzz result must be
// reported only after the corresponding component exists and has actually run.
//
// Not specified here: finalizer ordering, process-crash recovery, arbitrary asynchronous
// cancellation semantics, cross-thread ownership of a partially built object, foreign exception
// personality interoperability beyond explicit boundaries, or dynamic reflection that mutates
// required fields after publication. These require their own public contract before support is
// claimed. No hidden `static` member, implicit effect propagation, implicit constructor
// forwarding, or source-file-order initialization rule is introduced by this contract.

// 13. REVIEW CHECKLIST FOR LANGUAGE AND BACKEND CHANGES
//
// A source-language change affecting defer MUST answer: what scope owns the action; what exact
// execution point activates it; which transfers drain it; whether the body is evaluated eagerly
// or at exit; how nested cleanup composes; how checked effects are retained; and what happens
// when cleanup itself throws. The change MUST include positive and negative independent Sol
// examples and source diagnostics for invalid transfers. Any change in observable ordering is a
// language compatibility change, not a backend-only optimization.
//
// A source-language change affecting construction MUST answer: which constructor is selected;
// when explicit arguments and defaults execute; where base and this delegation occur; in what
// order field initializers and init blocks execute; how all required slots become valid; when the
// instance may escape; which initializer and base effects reach callers; and what the exceptional
// edge observes. Partial declarations MUST state which part owns ordered initialization.
//
// A backend change MUST provide proof that return values and primary exceptions are rooted during
// cleanup, that cleanup code is emitted for each source exit exactly once, that suppression never
// replaces the primary, that remaining cleanup continues after a failure, and that stack maps
// reflect initialized fields. Tests of an IR string or parser tree alone are insufficient for
// native exception semantics. Sanitizer, checked build, and end-to-end runtime claims must identify
// the exact target/runtime artifact and actual executed command.
//
// A metadata change MUST preserve nominal exception identities, constructor effect budgets,
// initializer origins, generic substitutions, cleanup effect origins, partial type identity,
// and compiler/schema compatibility. Consumers must reject unknown or incomplete effect records
// rather than defaulting them to an empty set. Documentation and status reports must distinguish
// specified behavior, source-analyzer coverage, FIR verification, emitted native code, and
// observed runtime behavior.

// 14. CLEANUP IDENTITY, ORDER, AND OPTIMIZATION INVARIANTS
//
// Every registration has a stable semantic identity consisting of the declaration symbol, the
// lexical owner, and the dynamic owner instance. A loop declaration has one syntax identity but
// a new dynamic registration for each iteration that reaches it. Two branches with identical
// cleanup source are separate identities. Compiler caching may share a compiled action body;
// it MUST NOT merge activation state or change which owner drains the registration.
//
// An action identity also has a source origin suitable for diagnostics, PSI navigation, debug
// metadata, and effect provenance. If one inlined body has multiple source origins, metadata
// preserves the source mapping needed to report the call site and the cleanup declaration. A
// synthesized cleanup block cannot use a generic hidden source span that points at the end of
// the function for every error.
//
// Optimizations may eliminate a no-op cleanup only after proving that its body has no observable
// evaluation, no checked or unchecked failure, no volatile/native interaction, no required
// debug-visible side effect, and no language-defined finalization hook. The empty body is
// trivially removable. A call that appears to return `()` is not trivial unless its selected
// contract proves the required properties. A pure expression with possible checked exception
// is not removable. A redundant write can still affect aliasing, volatile state, or exceptions.
//
// Reordering actions is forbidden by default. A future optimizer may reorder only if it proves
// observational equivalence across: shared mutable locations, aliasing, user accessors,
// allocations, safepoints, checked effects, RuntimeException behavior, suppression ordering,
// resource release, and debugger-visible source mapping. Even if two actions always both fail,
// exchanging them can change the primary failure. Even if they both succeed, exchanging writes
// to aliased state can change the value observed by an older action.
//
// A cleanup action cannot execute twice because of inlining, duplicated exceptional edges,
// deoptimization, re-entry into a handler, or GC relocation. Native lowering can outline a
// cleanup block, duplicate machine instructions across mutually exclusive edges, or use a
// personality cleanup pad, but the dynamic action count and order must match the single source
// registration. Debug stepping may show cloned machine code, but it may not cause cloned runtime
// executions.
//
// A return value pending during cleanup is a live root if its type contains managed references.
// It remains the same value even if cleanup mutates the local from which the return expression
// was computed. If cleanup throws, the pending value is discarded after it is no longer needed
// to resume execution. The exception primary and all bounded suppressed references must remain
// rooted through cleanup and delivery. A moving collector updates those roots using the same
// descriptors used by ordinary native frames.
//
// Cleanup is synchronous under this contract. It does not introduce implicit async suspension,
// task scheduling, cancellation, process-exit registration, or nondeterministic finalizer order.
// If a future feature adds async cleanup, that feature must define suspension safety, cancellation
// precedence, execution context, and captured-lifetime rules separately; this synchronous stack
// must not be retroactively interpreted as async disposal.

// 15. CONSTRUCTION METADATA AND API COMPATIBILITY
//
// The resolved constructor symbol records parameter names, passing modes, generic arguments,
// default-expression origins, checked effect budget, accessibility, base/this target identity,
// and the initialization part that owns side effects. The type symbol records merged partial
// part identities, the selected field layout, required-field set, initialization order, and
// whether its value can be zero-initialized. These are semantic contracts, not facts recovered
// from a mangled native symbol or printed signature.
//
// A public default expression is part of call behavior. Changing its expression can change
// evaluation effects or visible values even if the constructor binary signature is unchanged.
// Changing a parameter name can break named construction calls. Changing a constructor's
// checked effect budget can break source callers. Moving an initializer between partial parts,
// changing logical field order, changing nullability, or changing a base target can alter source,
// ABI, GC, and serialization compatibility. API review compares these resolved identities.
//
// A compiled distribution that exposes a public constructor must carry enough source or typed
// metadata for consumers to bind arguments, enforce defaults, check effects, and verify required
// fields. Metadata without a constructor contract is marked incomplete. It cannot be interpreted
// as a no-throw constructor, an empty initializer, a zero-valid type, or a verified no-escape
// target. Schema/compiler ABI identity must match the consumer's accepted format.
//
// Generic constructor effects are substituted with the selected closed type arguments before
// checked coverage. Type aliases normalize to resolved nominal identity. A generic error
// parameter constrained to Throwable can be thrown and the concrete specialization carries its
// resolved checked/unchecked classification. A metadata reader that cannot reconstruct the
// hierarchy returns an error or Deferred proof, never an empty effect set.
//
// Object and module initialization metadata is attached to the resolved logical binding. The
// binding stores its checked initializer effects, state model, initializer source origin, cached
// failure contract, and reference-root requirements. A Ready cache fact is runtime state; it
// does not rewrite the compile-time public effect contract. A Failed cache fact does not erase
// the original failure type or suppression data.

// 16. REQUIRED CONFORMANCE VECTORS
//
// A complete source-analysis suite MUST include a positive and negative pair for every rule
// family, and preserve source span and diagnostic identity. At minimum, the cleanup vectors cover:
// - declaration reached versus declaration skipped by branch, return, throw, and loop condition;
// - multiple registrations in one scope, nested scopes, repeated loop bodies, and sibling blocks;
// - normal completion, expression return, explicit return, checked throw, unchecked throw, break,
//   continue, and bottom-valued operations;
// - return-expression side effects before cleanup, out assignment during cleanup, and a pending
//   reference result rooted through an allocation/safepoint;
// - one or several exceptions raised by cleanup calls, with and without an existing primary,
//   including nested scopes; and SOL2254 for an escaping explicit cleanup throw;
// - checked effect coverage, catch placement, RuntimeException classification, and generic effect
//   substitution;
// - forbidden outward transfer from a cleanup body and permitted transfer inside a local nested
//   loop that does not target the enclosing scope;
// - finally/defer lexical ordering, action attempted exactly once, and continuation cancellation.
//
// Constructor vectors cover selected argument order and defaults; base and this delegation;
// cycle/ambiguity rejection; base-before-derived ordering; field/init-block declaration order;
// failure at each evaluation prefix; required-field branch joins; this-escape through direct,
// virtual, closure, delegate, reflection, global, and native paths; cleanup of constructor-owned
// resources; partial type initializer uniqueness; struct zero-initialization; object lazy state;
// and effect metadata at every caller boundary.
//
// A syntax fixture proves only that the parser and tree representation accept a form. A semantic
// fixture proves only the analyzer behavior it asserts. FIR verification tests must show ordered
// cleanup edges, exception states, active registration identity, constructor target, initialized
// slots, and checked effect coverage. Native tests must execute both normal and exceptional paths
// on the supported Windows target and inspect the observed primary/suppressed order. These
// evidence classes are reported separately; one cannot stand in for another.

// 17. CONSTRUCTOR PLAN AND FIELD-STATE DETAILS
//
// Constructor binding is completed before any constructor body executes. The resolved plan
// includes the selected declaration, source-to-parameter mapping, explicit argument evaluation
// order, omitted default list, base/this target, and substituted effect contract. A failed or
// ambiguous target is not represented as a constructor with no effects. Named arguments alter
// the parameter mapping, not the source order in which their expressions run.
//
// Each required field has a flow state at a program point: Unassigned, Assigned, or Unknown.
// Unknown is not equivalent to Assigned. At a branch join, a field is definitely assigned only
// if every reachable normal predecessor assigns that exact field. An exceptional predecessor
// that leaves the constructor does not participate in the normal publication join, but its
// completed writes remain relevant to cleanup, GC roots, and failure diagnostics. A loop with a
// zero-iteration path cannot establish a required assignment made only in its body.
//
// Property backing storage is identified separately from the property symbol. A successful
// init accessor may establish the backing slot only when the accessor metadata resolves to that
// exact slot and its body returns normally. Calling an ordinary setter on another receiver is
// not assignment to the current constructor's required field. A delegated property has descriptor
// metadata plus delegate storage; reading its value is not equivalent to assigning an ordinary
// field of the same name.
//
// Constructor code must keep an unpublished receiver distinguishable from every other Object
// reference. Passing the receiver as an argument, storing it in a field or array, returning it,
// closing over it, publishing a callback, invoking an unknown virtual/interface method, exposing
// it to a delegate hook, registering it with native code, or passing it to reflection that can
// observe its instance state are potential escape edges. A call-site proof must use the selected
// declaration and exact body/effect metadata. A method name such as `initialize` or `validate`
// is not evidence that it is harmless.
//
// A constructor may invoke a verified local helper while fields remain unset only if analysis
// proves that the exact target is non-escaping, cannot read the unset slots, cannot reenter or
// expose the receiver, and returns under the normal effect contract. If dynamic dispatch can
// select an override, the proof must cover the permitted runtime target set. If target metadata
// is incomplete, the edge is unverified. Private visibility alone does not prove no escape when
// the body can store `this` or call another externally visible operation.
//
// A `this(...)` delegation edge enters another constructor in the same nominal type before the
// delegating body. A base edge enters the selected base constructor before the derived
// initialization part. The constructor graph is acyclic, each source constructor has one chain,
// and one completed object uses one base chain. The primary initialization sequence is not
// repeated after returning from a secondary body. A constructor cannot invoke itself indirectly
// through a factory and pretend the resulting object is the same partially initialized receiver.
//
// Constructor visibility does not bypass field proof. A private constructor can still fail to
// initialize required fields; a public factory can call it only if the private declaration is
// accessible in that context. An object initializer has its own lazy state rather than a class
// constructor target, but it follows the same no-partial-publication invariant and checked-effect
// coverage at access sites.

// 18. EFFECT ORIGIN AND EXCEPTIONAL PREFIX DETAILS
//
// For an expression `T(a(), b(), c = d())`, effect provenance records each operation in
// evaluation order and associates it with the selected constructor. If `a()` throws, neither
// `b()` nor `d()` nor allocation occurs. If `b()` throws, `a()` remains completed and `d()` does
// not execute. If all explicit values complete and `c` is omitted, its selected default runs
// before base construction. Named parameter placement does not move the expression evaluation
// point. Default-expression metadata identifies its declaration origin and the parameter whose
// omission activated it.
//
// A base constructor's effect origin is carried through the derived constructor chain. Catching
// a base effect in the constructor body is legal only if source evaluation order places that
// catch around the base invocation; a later body try cannot retroactively catch an exception
// thrown before the body begins. Therefore the chain contract must expose its effect at the
// derived construction declaration and at the caller. A language revision that adds a syntax
// form to catch base construction must specify its exact placement before implementation.
//
// An initializer effect is attached to the declaration and the selected instance path that
// executes it. A field initializer does not run when its constructor call is never entered. An
// initializer skipped after a prior failure does not contribute to that particular concrete
// execution, though its effect remains part of the static constructor contract if it is reachable
// on any valid initialization path. Analysis may be conservative, but it cannot union effects
// from unrelated rejected overloads or unreachable declarations without identifying that
// conservatism as such.
//
// At a catch, the incoming state is the state at the operation's exceptional edge after all
// preceding argument, initializer, or cleanup operations that completed. It does not include
// future body statements. If a constructor-owned defer executes while a constructor body throws,
// its successful writes and effects occur before the exception leaves that constructor. If the
// cleanup also throws, the constructor's already selected primary remains primary and cleanup
// effect identity remains available as suppressed metadata.
//
// These details are observable through exception identity, side effects, cleanup order, public
// `throws` budgets, and whether an object or value is returned. A backend that preserves only the
// set of exception names but not the ordered exceptional prefix is not equivalent. A source
// analyzer that reports a broad effect budget without recording operation origins may help a
// caller conservatively, but it cannot claim the complete effect proof required by verified FIR.
// Construction source diagnostics: SOL2274 rejects an invalid chain target; SOL2275 rejects
// same-instance delegation cycles; SOL2276 rejects multiple partial initialization parts;
// SOL2277 rejects invalid constructor/helper dependencies. Missing source evidence remains
// Deferred. Native construction lowering and receiver ABI remain outside this source proof.
