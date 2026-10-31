// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: normative full resource contract; current FIR uses 4096 instances, 16M body nodes, and 128 type levels.
module source.spec.generic_fir_specialization_limits

// Resource and conformance appendix
// ================================
//
// This appendix belongs to GenericFirSpecialization.sol. Its full budgets and fixtures are
// normative requirements; except for the narrower initial FIR caps recorded in the status line,
// they are planned and are not evidence that the compiler implements those limits. Call/FIR-focused
// expectation records are also collected in GenericFirSpecializationCases.sol; that independent
// matrix does not replace these limits.

// 10. Resource bounds and cancellation
// ------------------------------------
//
// All limits are hard correctness boundaries, not performance hints. Reaching one produces a
// resource-limit result for the owning artifact. The implementation must not truncate the worklist
// and then claim that the remaining instance graph is complete.
//
// The following specialization-specific defaults are normative for the first implementation:
//
// - Maximum active generic-instantiation depth: 128 edges from a root instance.
// - Maximum distinct generic function instances: 100,000 per compilation/artifact closure.
// - Maximum specialized FIR/source-body nodes accounted across the closure: 16,000,000.
// - Maximum structural type nodes visited for one signature/body root substitution: 4,096.
// - Maximum canonical type arguments on one generic function: the declaration's finite arity,
//   which must also obey source parser and declaration-analysis limits.
//
// The `TypeSpecialization` interner's maximum of 100,000 newly interned types is an independent
// workspace limit. It is not the specialization-instance budget, and an implementation must not
// subtract one from the other or treat interning as proof that code generation fits. Existing
// inference-specific graph/bound budgets remain independently enforced.
//
// Body-node accounting counts each distinct instance's specialized source/FIR node once, including
// generated adapters required by its source operations. A cached or shared node may reduce actual
// allocation, but cannot lower the semantic accounting charge. This prevents structural sharing
// tricks from making the same logical compile request unbounded.
//
// A nested signature substitution counts each visited constructor, argument, effect, and nominal
// projection node. The implementation checks the limit before descending past it. It cannot
// replace an over-budget type by Object, error, bottom, or an opaque pointer.
//
// Limits may be lowered by an explicit compiler-host policy. They may not be silently raised by a
// project config, source annotation, environment variable, cache record, or untrusted artifact.
// Raising a normative maximum requires a reviewed language/toolchain contract update and dedicated
// resource tests. A lower limit is included in diagnostic evidence and persistent cache identity.
//
// Cancellation is checked during candidate processing, each worklist dequeue, each substituted
// type traversal, body rebinding, verifier traversal, and graph closure. Implementations check at
// least once per 256 accounted nodes and at every potentially unbounded external/native operation.
// Cancellation cannot publish a partially verified graph, replace a previously successful output,
// or produce a success diagnostic with missing instances.
//
// Resource failure in one candidate remains candidate-local until overload semantics determine
// whether that candidate could win. Resource failure after candidate selection fails the selected
// call/artifact; it does not cause overload resolution to choose a less-preferred declaration.
//
// A resource diagnostic records the limit kind, configured limit, observed count, owning generic
// declaration/call origin, and a bounded instantiation chain. It omits process paths and machine-
// local type IDs. If the diagnostic cap is reached, at least one stable primary resource
// diagnostic and a summary count remain visible.


// 20. Conformance fixtures
// ------------------------
// Every implementation phase must include positive and negative source-level fixtures with
// assertions over selected declaration, exact substitution, specialization key, FIR identity,
// verifier outcome, diagnostic span, and output behavior where native execution is supported.
// Parser acceptance alone is not a semantic oracle. Positive examples below are normative future
// behavior; until the implementation status changes, they are not expected to emit native code.
// A. Identity and exact specialization
// ------------------------------------
//     module fixture.identity
//     func <T> identity(value: T): T = value
//     object Main {
//         func main(args: []String): () {
//             val number: int = identity(42)
//             val text: String = identity("sol")
//             std.stdio.println(number, text)
//         }
//     }
// Expected: one `identity<int>` instance and one `identity<String>` instance, each with its exact
// closed FIR signature and body. Both roots select the generic declaration before specialization.
// The input order of calls does not affect canonical instance ordering. Native output is expected
// only if the String and integer operations in this source set pass current verifier/runtime rules.
// B. Reuse across same key
// ------------------------
//     func <T> echo(value: T): T = value
//     func demo(): int {
//         val first = echo(1)
//         val second = echo(2)
//         return first + second
//     }
// Expected: both calls target one `echo<int>` FIR function. There is one source body specialization
// and two call-site origins. Replacing the two literals with incompatible inferred types must
// produce distinct instance keys or a source type error; it cannot merge them by display name.
// C. Explicit arguments and bounds
// --------------------------------
//     interface Named { func name(): String }
//     func <T: Named> getName(value: T): String = value.name()
//     val value = getName<MyNamed>(myNamed)
// Expected: explicit T is fixed to MyNamed, then every bound and body operation is checked.
// Changing MyNamed so it no longer satisfies Named rejects the call before FIR. It cannot create
// an unconstrained instance or use a previous successful cached instance.
// E. Stable recursive generic key
// -------------------------------
//     func <T> recurse(value: T, more: bool): T {
//         if (more) return recurse(value, false)
//         return value
//     }
// Expected: the recursive call resolves to the already reserved `recurse<T-concrete>` node; the
// graph contains one instance and one back edge. Native acceptance still depends on all body CFG
// rules and on T's closed ABI type.
// F. Type-growing recursion limit
// -------------------------------
//     func <T> grow(value: T, more: bool): () {
//         if (more) grow([value], false)
//     }
// Expected: if source inference and array types allow the growing call chain, every distinct tuple
// creates a distinct key until a documented depth, instance, or node budget is reached. The
// artifact fails with a bounded resource diagnostic. The compiler does not mark the chain closed
// after seeing the function name once. This fixture must use bounded test settings so CI remains
// finite and the observed limit is asserted.
// I. Constraint failure must not reuse cache
// ------------------------------------------
// Build a valid source snapshot where T satisfies an interface constraint and populate an
// instance/cache entry. Then change the constraint implementation or source dependency so the type
// no longer satisfies the bound while keeping the function display signature the same.
// Expected: changed semantic content invalidates the affected entry. The second build reports the
// constraint failure and emits no artifact. A cache hit cannot override declaration analysis.
// J. Unsupported nullable ABI
// ---------------------------
//     func <T> nullable(value: T?): T? = value
//     val result: int? = nullable(1)
// Expected: source analysis may solve the substitution if nullable inference supports it. The
// current native verifier rejects nullable native slots/results with the correct FIR/native
// diagnostic. No pointer sentinel or zero-value encoding is invented by generic specialization.
// M. Reordered named arguments
// ----------------------------
//     func <A, B> pair(first: A, second: B): (A, B) = (first, second)
//     val result = pair(second = "s", first = 4)
// Expected: A=int and B=String by parameter mapping, while source expressions retain source
// evaluation order. The tuple body remains unsupported in native storage/return until its exact
// ABI and FIR rules are admitted; inference success alone cannot emit the result.
// N. Ref/out exact storage
// -----------------------
//     func <T> replace(ref target: T, value: T): () { target = value }
// Expected: ref target and value must produce one exact eligible concrete T. Conversion or boxing
// into the ref location is forbidden. If the concrete body or native root/write barrier cannot be
// verified, the instance is rejected before code generation.
// O. Checked effect substitution
// ------------------------------
//     func <T, E: std.core.Throwable> fetch(source: T): T throws E
// Expected: effect type E is substituted as a canonical closed Throwable identity and verified as
// part of the caller's checked budget. The unordered effect set is not zipped by list position.
// Current native source-EH limitations reject an instance requiring unsupported propagation or
// handling. No catch edge may be dropped to make FIR appear complete.
// P. Input-order determinism
// --------------------------
// Compile one explicit source set in forward and reverse file order, with warm and cold caches.
// Expected: identical selected overloads, closed key set, FIR function order/identity policy,
// diagnostics, origin mappings, and artifact metadata. Any separately qualified byte-reproducible
// artifact must also have identical bytes. Hash-table order and worker scheduling are varied.
// Q. Cancellation atomicity
// -------------------------
// Cancel while the worklist is processing a deeply nested but finite specialization graph.
// Expected: cancellation propagates promptly, no verified capability or successful output is
// published, staging output is cleaned or safely retained for diagnostics, and a subsequent fresh
// compilation is unaffected. Cancellation is not reported as an invalid source program.
