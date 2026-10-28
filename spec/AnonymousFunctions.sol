// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: CST/AST and bounded body analysis are implemented. Closed, exact-ABI noncapturing
// anonymous functions have verified FIR and AOT/ORC execution coverage; environment closures are
// not implemented.
module source.anonymous_functions.rules
import std.stdio

// Anonymous function expressions
// ===============================
//
// An anonymous function is a callable expression with its own parameter identity, body, result,
// and checked-effect contract. It is not a lambda with renamed syntax and it is not a declaration
// whose name is omitted after parsing. Its source origin spans the `func` keyword through the
// complete expression body.
//
// Current forms:
//
//     func(parameter groups) [: result] [throws checked types] { statements }
//     func(parameter groups) [: result] [throws checked types] = expression
//
// The parameter list is required, including `()` for a parameterless function. A block body uses
// the ordinary statement grammar. An expression body has the same result rule as an expression-
// bodied named function. There is no implicit `it`; named parameters are referenced by their
// declared names. Anonymous functions do not use the lambda-only `$0` positional syntax.

val increment: (int) -> int = func(value: int): int { value + 1 }
val doubled: (int) -> int = func(value: int): int = value * 2
val unitAction: () -> () = func() { () }

// Parameter declarations
// ----------------------
//
// Parameters use the ordinary Sol parameter declaration shape, including shared type groups,
// exact ref/out modes, and one optional vararg parameter. Anonymous functions cannot declare
// default values; SOL2226 rejects a recovered default parameter. A contextual function type may
// supply omitted parameter and result types; without that context, an anonymous parameter needs a
// written type. `ref`/`out` mode and storage type are part of the function type and cannot be
// adapted by a function-pointer cast.

val combine: (int, int) -> int = func(left, right: int) { left + right }
val update: (ref int, out int) -> () = func(ref input: int, out output: int) {
    output = input
}

// Anonymous-function values use the fixed parameter shape of their function type. A vararg
// declaration's element type T becomes one []T parameter and does not expand arguments at an
// indirect call. Named/default argument mapping applies only when a call retains a named function
// declaration as its target; a function type does not retain parameter names or default values.

// Contextual typing and result inference
// --------------------------------------
//
// A written result type is checked against explicit returns and the normal block tail. Without a
// written result type, an expected function type supplies the result context; otherwise body
// analysis finds the unique common type of explicit return values and the block tail. A final expression in
// a block returns implicitly. A declaration, assignment, loop, or empty statement does not produce
// a result value. An empty body has result type `()`.
//
// Every reachable normal exit from a non-unit function must produce a value. A bottom-typed
// expression has no normal exit. A non-unit function with a reachable fallthrough path is invalid.
// Explicit `return` targets the nearest anonymous function. It cannot jump out through the
// anonymous function to its enclosing function. Return labels remain bound to their resolved
// callable owner and are never guessed from spelling alone.

val choose: (bool) -> int = func(flag: bool): int {
    if flag { return 10 }
    20
}

val maybe: (bool) -> int = func(flag: bool): int {
    if flag { return 10 }
    // expect: Error when this path reaches the end without an int result.
}

// Result inference has deterministic resource bounds. The current implementation limits a body
// result walk to 65,536 nodes and collects at most 4,096 result paths. Exceeding either bound
// produces an incomplete analysis with SOL2290; it must not hang, silently truncate into an
// apparently successful result, or manufacture an empty tuple result.

// Checked effects
// ---------------
//
// `throws` is part of the anonymous function's resolved type. Checked exception types derive from
// Throwable but not RuntimeException. RuntimeException subtypes are unchecked. If an explicit
// `throws` list is present, it must cover every checked effect raised by the body. Otherwise an
// expected function type supplies the effect budget, or body analysis infers the effect set.
// Effects created when defining a function do not execute at definition time: they become effects
// of an enclosing callable only when it invokes the function value.

func makeFailureHandler(): () -> int throws IOException = func(): int throws IOException {
    fail()
}

func fail(): int throws IOException { return 1 }

// Captures, escape, and receiver identity
// ---------------------------------------
//
// A reference to an enclosing local or receiver is a capture obligation, even when the source
// spelling could also name a property or inherited member. Name binding resolves the local/member
// identity first. A local parameter shadows an instance member; `other.member` uses `other`, not
// the current receiver. Direct and inherited implicit member names, `this`, `super`, constructor
// properties, and accessor `field` require the owning receiver. Nested anonymous functions
// propagate each required local and receiver through every intermediate environment.
//
// `LexicalAnalysis.AnonymousFunctionCaptures` and
// `GetAnonymousFunctionCaptures(function)` report local symbol identity and read/write access.
// `AnonymousFunctionReceiverCaptures` and
// `GetAnonymousFunctionReceiverCaptures(function)` report receiver identity and resolved member
// candidates. Each inventory has an `IsComplete` fact where bounded inherited lookup may be
// incomplete. These are source-analysis facts; they do not define an environment layout.

func capturedValue(seed: int): () -> int {
    val callback = func(): int { seed }
    callback
}

// AnonymousFunctionEscapes/GetAnonymousFunctionEscape(function) separately classifies the source
// uses of a function value. A direct immediate invocation is ProvenNonEscaping. A returned value,
// call argument mapped to an `@escaping` parameter, aggregate element, or heap store is MayEscape.
// Local writes of tracked callable
// expressions or local aliases add identities to a may-alias set; branch alternatives are unioned
// without path-sensitive narrowing. Mutable local alias chains remain ProvenNonEscaping when every
// joined use stays local. Null/untracked writes, capture by another callable, ref/out publication,
// or an unmodeled consumer remains Unknown or MayEscape. Function-valued parameters are
// nonescaping unless annotated `@escaping`; visible callee bodies are checked against that
// declaration contract. An unmodeled call target or argument mapping is not assumed to be
// nonescaping. Escape classification is not a borrow check and does not prove lifetime.

// Body analysis accepts a borrowed or ref-like capture only when all modeled uses prove that the
// closure remains within the owning activation. Return, an `@escaping` parameter, or an unknown
// consumer is rejected. A permitted source capture still does not mean the compiler can emit its
// closure: FIR currently rejects all capture environments.
func rejectedBorrow(ref value: int): () -> int {
    val callback = func(): int { value }
    return callback
    // expect: SOL2262, a returned closure cannot retain caller-owned ref storage.
}

// FIR and native function-pointer boundary
// -----------------------------------------
//
// A noncapturing anonymous function may lower when all of these conditions hold:
//
// - its body analysis is complete and its source expression has a closed function type;
// - the resolved signature exactly matches the current function-pointer ABI;
// - it is nested only in a closed module-level function or eligible stateless top-level object
//   method;
// - it has no local or receiver captures, nested closure environments, open generic targets,
//   default-argument mapping, or source obligations without proof. A vararg declaration is
//   admitted only through its fixed `[]T` function-type parameter; an indirect call does not expand it.
//
// FIR creates one synthetic function per source anonymous-function node and one typed function
// reference. The verifier matches source origin, parameter slots, result type, effects, calls,
// operators, and returns before issuing VerifiedFirProgram. Indirect calls retain exact parameter
// order and ref/out modes. `out` parameters must be assigned on every normal exit. A checked effect
// remains in the function type and is checked by the caller.
//
// The emitted value is a raw code pointer only. It has no receiver environment, local cell, managed
// reference, Yellow Tea scan shape, cross-thread ownership rule, or cross-process representation.
// Exact-ABI nullable function values are admitted as nullable code-pointer slots and arrays. The
// underlying anonymous function remains noncapturing; null comparison and a source-proven flow
// refinement are required before indirect invocation. `!!` is source-valid but native assertion
// lowering is not implemented. Capturing values, bound methods, variance adapters, and nested
// closure environments remain rejected by FIR until their environment, lifetime, ABI, and GC
// contracts are implemented. A nested lambda or anonymous-function expression with no capture may still be
// lifted independently: each source identity and ABI is verified like a function expression at
// the enclosing lexical depth. “Nested closure” here means a nested callable that needs an outer
// local or receiver; it is not a ban on nesting a capture-free code pointer inside another body.

object Main {
    func main(args: []String) throws IOException {
        val addOne: (int) -> int = func(value: int): int { value + 1 }
        val addTwo: (int) -> int = func(value: int): int = value + 2
        println(addOne(41), addTwo(40))
    }
}

// Normative declaration and type contract
// =======================================
//
// The expression grammar is:
//
//     anonymous_function = "func" "(" [ parameter_group { "," parameter_group } [ "," ] ] ")"
//                          [ ":" type ] [ throws_clause ] ( block | "=" expression )
//     throws_clause      = "throws" type { "," type }
//
// In a function-type parameter, the comma before the next named parameter group terminates the
// function type's throws list. The next group may begin with `ref`, `out`, or `vararg`; a following
// thrown type continues the throws list only when no named parameter group follows it.
//
// A semicolon-only body is not an anonymous-function body. Recovery may still retain an
// AstAnonymousFunction with `Body.Kind == Missing`, but recovery nodes are not valid executable
// source. The AST origin begins at `func`, includes the complete signature and body, and is kept
// stable through AST JSON, source correspondence, and diagnostics.
//
// A parameter group follows the ordinary function-parameter declaration shape:
//
//     [ annotation ] [ "ref" | "out" | "vararg" ] name { "," name } ":" type
//
// One group creates one parameter symbol per name. Every name in a group has the same written type
// and passing mode, but each parameter has independent identity and definite-assignment state.
// For example, `first, second: int` is two `int` parameters, not one tuple parameter. Parameter
// names are unique across all groups in one anonymous function.
// A duplicate name, more than one vararg parameter, or a multi-name vararg group is rejected with
// SOL2005, matching the parameter-shape validation used for named declarations.
//
// A parameter may omit its type only when an expected function type supplies the corresponding
// parameter type. A nullable expected function type supplies the same underlying signature while
// the anonymous-function expression itself remains non-null; ordinary conversion then lifts the
// value to the nullable target. Without that context, an omitted parameter type has no resolved
// function signature and body analysis remains incomplete or reports an error. Passing mode
// always comes from source syntax; it must exactly match the expected function type and is never
// inferred from context. Value parameter conversion follows ordinary assignability; `ref` and
// `out` require the exact storage type. A written result type and checked `throws` set must also be
// compatible with the expected function type.

val typedGroup: (int, int, String) -> String = func(left, right: int, suffix: String) {
    (left + right).to_string() + suffix
}

val contextSuppliesParameterType: (int) -> int = func(value) { value + 1 }
val nullableContextSuppliesParameterType: ((int) -> int)? = func(value) { value + 1 }

// Default parameters and variable arguments
// ------------------------------------------
//
// Anonymous functions cannot declare default values. The parser may retain a written default in
// the recovered AST so later parameters and the body remain available to tools, but source analysis
// reports SOL2226 on that parameter and FIR cannot admit the invalid expression. Named function
// declarations retain their own default-expression contract. A call can use that contract only
// when binding selects the declaration directly; a value of function type does not carry it.
//
// A function type carries arity, parameter modes and types, result type, and checked effects; it
// does not carry parameter names or default-expression metadata. Calling through an anonymous-
// function value therefore supplies every parameter in the function type. This includes a stored
// value and a parenthesized or immediately invoked anonymous-function expression.
//
// At most one `vararg` parameter is permitted and it has one declared name. Its body type is `[]T`
// for a declaration element type `T`. A vararg parameter is a single `[]T` parameter in the
// resulting function type: an indirect call supplies one array value and neither expands separate
// elements nor applies a spread marker. Anonymous functions cannot attach a default value to the
// vararg parameter. Parameters after a vararg use named arguments at declaration-call sites. A
// verified anonymous function with a vararg declaration uses one ordinary `[]T` parameter in its
// fixed function type; its body reads the array value by the declared parameter name. Indirect
// calls pass one array value and do not expand or spread elements.

func declarationDefault(value: int = 40): int { value + 2 }
val declarationVararg: ([]int) -> int = func(vararg values: int): int { values.length }

// Current implementation status: the parser and AST retain recovered default syntax and vararg
// declarations. Source analysis rejects anonymous-function defaults with SOL2226. For vararg, the
// resolved function signature contains one `[]T` parameter, BodyAnalysis checks the array-valued
// body parameter, and native FIR verifies and lowers this fixed-arity function value. The compiler
// must not reinterpret an indirect array parameter as an expanded argument list.

// Result selection and normal completion
// ---------------------------------------
//
// Result type selection uses this order:
//
// 1. A written `: T` fixes the result contract; every explicit return value and normal block tail
//    is checked against `T`.
// 2. Otherwise, a contextual function type supplies its result type.
// 3. Otherwise, body analysis gathers explicit `return` values and the block tail (or the single
//    expression-body value), then requires one unique common resolved type.
// 4. If the body has no value-producing candidate, its result is the empty tuple `()`.
//
// A valueless `return` contributes `()`; it does not invent `Unit`. A declaration, assignment,
// loop, or empty statement contributes no value. If the expected result is `()`, a reachable tail
// expression is evaluated and its value discarded. This preserves its effects and side effects.
// If a non-`()` result is required, a reachable block fallthrough must end in a value-producing
// tail. Every reachable normal return path must also produce a compatible result. A `!` expression
// has no normal continuation and therefore is not a missing return path.
//
// Explicit return belongs to the nearest anonymous function body. It cannot transfer to an
// enclosing named function by skipping the anonymous-function boundary. A return value is checked
// using the same expected-result and effect rules as a tail value. By-reference returns and return
// labels are retained or diagnosed according to their source binding; the current native anonymous-
// function subset rejects labeled and by-reference returns because it does not prove their ABI and
// lifetime contract.
//
// “Common type” means one unique type selected by the supported intrinsic conversion relation after
// nullable normalization. Declaration order is not a tie breaker. If no unique type can be proved,
// analysis reports/defer the result instead of guessing. The result-inference walker is iterative,
// cancellation-aware, bounded to 65,536 visited body nodes and 4,096 result candidates, and reports
// SOL2290 when either budget is exceeded. It never returns a successful truncated result.

val expressionBodyResult: (int) -> int = func(value: int) = value + 1
val emptyResult: () -> () = func() { }
val explicitExits: (bool) -> int = func(flag: bool): int {
    if flag { return 1 }
    2
}

// The public BodyAnalysis inventory distinguishes source identity from lowering. For an owned,
// resolved anonymous function with an analyzed body, `AnonymousFunctionBodies` and
// `GetAnonymousFunctionBody(function)` return a summary with:
//
// - the resolved function type;
// - the number of abstract reachable normal exit states, not a count of runtime executions;
// - whether a reachable fallthrough state remains;
// - per-out-parameter definite-assignment results over every normal exit state; and
// - an `IsComplete` flag, false when this analyzed subtree contains a known diagnostic or deferred
//   proof obligation.
//
// A foreign AST node is rejected by the query. An unresolved signature or missing/recovered body
// has no body summary. These flow facts do not prove that a function value is nonescaping, that an
// out address is safe to retain, or that FIR can emit it. A normal function value may have no
// reachable normal exits (for example, every path throws or reaches `!`); out assignment is then
// universal over the empty set of normal exits, while the checked exception/termination behavior
// remains independently represented.

// Checked exception effects
// -------------------------
//
// The function type's effect set is part of its identity. Checked effect types derive from
// `Throwable` but not `RuntimeException`; `RuntimeException` and its descendants are unchecked.
// If `throws` is written, it is the function's declared checked-effect contract and every checked
// effect raised by the body must be covered. If it is omitted, an expected function type supplies
// the permitted effect budget; without such a context, body analysis infers the effect set from
// uncaught checked effects. An invalid effect type is never treated as an unchecked type merely
// because its hierarchy is unresolved.
//
// Defining an anonymous function does not execute its body and does not add the body's checked
// effects to the creating function. Invoking the resulting function value contributes its declared
// or inferred effects at the call site. Catch and cleanup flow inside the anonymous body determines
// which effects escape that body. Creating the function inside a try region does not make a later
// invocation part of that try region. Indirect calls preserve exceptional edges and their definite-
// assignment snapshots.

// A callable declaration or expression also does not execute its body while the enclosing flow
// graph is being analyzed. Potential-throw discovery for a try body stops at nested function,
// lambda, and anonymous-function boundaries. Likewise, declaring a callable inside a scope with
// pending cleanup does not invalidate the enclosing scope's mutable-local facts merely because the
// callable body contains a call. The callable body's effects and flow are checked independently;
// they become relevant to the enclosing graph only along a proven invocation edge. This prevents an
// uncalled nested body from manufacturing a catch path or a cleanup-state mutation.

open class IOException: Exception {}
func readValue(): int throws IOException { return 1 }
val checkedCallback: () -> int throws IOException = func(): int { readValue() }

// Capture identity, receiver capture, and borrow rejection
// --------------------------------------------------------
//
// Capture binding uses resolved symbol identity. An inner anonymous function parameter shadows an
// outer parameter with the same spelling and is not a capture of the outer symbol. A captured
// mutable local denotes shared storage; it is not a per-invocation copy. A captured immutable local
// is a value-copy candidate, but its resolved representation may itself be a managed reference.
// Capturing a local function records that function's own lexical environment requirement.
//
// Receiver requirements have a separate inventory from local captures. Explicit `this`/`super`,
// constructor-property references, implicit direct or inherited instance members, and accessor
// `field` are associated with the owning nominal type and the resolved member candidates. A local
// parameter shadows an inherited property. A qualified `other.member` uses `other` as its receiver
// and is not an implicit capture of the current receiver. Bounded inherited lookup reports
// incompleteness; an empty member list after a limit is not proof that no member exists.
//
// Nested anonymous functions propagate each required outer local/receiver through every
// intermediate environment that must forward it. Local and receiver inventories preserve source
// order and are immutable snapshots. The storage category is a source-analysis candidate only; it
// does not define a closure field, cell allocation, Yellow Tea descriptor, barrier, or root.
//
// Ref/out parameters and reference locations are borrowed caller storage. An anonymous function
// may capture them only when every modeled use proves that the value cannot outlive the owning
// activation; otherwise body analysis reports SOL2262. A ref-like value (including a ref struct or
// a value which may contain a borrow) follows the same rule. Source analysis does not infer safety
// from an unknown alias or consumer. Even a source-proven local environment remains outside the
// current FIR closure-emission subset.

// Escape classification is independent of capture and body-flow analysis. For one source function
// identity, `AnonymousFunctionEscapes`/`GetAnonymousFunctionEscape(function)` reports source-use
// witnesses and one of:
//
// - `ProvenNonEscaping`: an immediate call, an unused local value, or a tracked may-alias graph
//   whose every modeled use stays local (including mutable/branch writes and local invocation);
// - `MayEscape`: return, call argument mapped to an `@escaping` parameter, aggregate element,
//   or heap/receiver storage;
// - `Unknown`: null/untracked write, capture by another callable, or a context whose lifetime rule
//   is not modeled. Ref/out publication remains conservative and is not treated as a local alias.
//
// Escape facts are conservative source-use classification. They do not prove that a pointer is
// stack-safe, that a callee cannot retain an argument, or that the native backend may emit a
// function value. A function-valued parameter is nonescaping unless its declaration has
// `@escaping`; visible bodies are checked against this contract. The compiler must not turn an
// unknown target or argument mapping into `ProvenNonEscaping` to admit a code-generation shortcut.

// Nested noncapturing function values in verified FIR
// ---------------------------------------------------
//
// A nested anonymous-function expression does not by itself imply a closure. When an inner
// anonymous function captures no local or receiver, has a complete closed signature, and meets all
// other FIR restrictions, it can be independently lifted to its own synthetic function and the
// outer body contains a typed reference to that target. The inner function is checked as its own
// source node. A nested function that captures an outer local or receiver requires an environment
// and is rejected. A capture in an inner function cannot be ignored merely because the outer
// function itself has no capture.
//
// The current native slice accepts only noncapturing anonymous functions with an exact supported
// function-pointer ABI, inside a closed module-level function or eligible stateless top-level
// object method. Nested anonymous expressions may be present only when every emitted function
// independently passes these checks. Each synthetic FIR function has exactly one source identity;
// its function type, parameter slots and modes, result, checked effects, body returns, calls,
// operators, and source origins are verified before `VerifiedFirProgram` is created.
//
// The code pointer is a raw callable address. It has no receiver environment, local cell, managed
// reference, Yellow Tea scan shape, ORC unit ownership, cross-thread rule, or process-transfer
// representation. Capturing closures, receiver-bound methods, variance or effect adapters, closure
// arrays, borrowed-environment pointers, and native exports of a Sol closure remain rejected or
// deferred. Exact-ABI nullable function pointers use zero for null and pointer-width array slots;
// they do not create a closure environment. AOT and ORC must consume the same verified source contract;
// backend mode cannot widen source acceptance.
//
// The exact-ABI subset has tests for block and expression bodies, contextual result inference,
// generic closed targets, ref/out parameter slots, checked effects, and nested noncapturing
// functions. Native integration verifies AOT executable behavior and process-isolated ORC behavior
// against the same output. Those tests qualify only the implemented subset and do not qualify the
// deferred closure/runtime contracts above.
