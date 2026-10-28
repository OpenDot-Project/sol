<!-- SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com> -->
<!-- SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception -->
# Generic owners and member specialization

This guide explains how generic nominal types and their members fit into the FIR specialization
boundary. The normative source contract is in
[GenericFirOwnersAndMembers.sol](../../spec/GenericFirOwnersAndMembers.sol); the associated
[example matrices](../../spec/examples/semantics/GenericFirOwnersAndMembers.sol) and
[GenericOwnerBodyRebinding.sol](../../spec/examples/semantics/GenericOwnerBodyRebinding.sol) record
expected source binding separately from native admission.

## Current implementation boundary

Source analysis can resolve generic member candidates, substitute constructed receiver types along
base edges, collect candidate-local inference evidence, and check nominal constraints. That work
answers source questions such as which member is selected for a `Box<String>` receiver and whether
its declared signature is applicable. An internal on-demand query also rebinds a member body for a
direct closed generic owner; the scope and limits are listed below.

Verified generic FIR has a narrower scope. It specializes closed, directly called module-level
generic free functions and generic methods declared by a stateless, non-generic, module-level
`object`, provided the source call directly names that singleton or is an unqualified call within
the same object. Eligible object parts contain only implemented functions; they have no bases,
initialization, constructors, properties, or other state. Eligible methods have no virtual or
operator dispatch, annotations, ref return, or `this`/`super` use. The method body is emitted with a
receiver-free ABI, and both the builder and verifier validate the source receiver shape. A
local/computed receiver is rejected even when source analysis can bind it. Omitted defaults use the
existing pure, non-vararg call-plan rules: explicit values are evaluated once in source order and a
default may read only an earlier value parameter captured by the call. Defaults after `ref`/`out`
parameters remain outside FIR.

This does not emit generic class, struct, ref struct or companion layouts, nor generic constructors,
properties, accessors, indexers, operators, virtual slots, or generic runtime descriptors. A
source-selected member outside the FIR slice receives an explicit native-admission rejection. The
compiler must not substitute a similarly named free function or a lower-ranked overload.

### Source-only member body views

`BodyAnalysis.GetGenericMemberBody` creates a source-only body view from the original member AST and
one exact owner/method argument pair. It does not copy the open body's winning calls and patch type
names. It constructs one simultaneous substitution from canonical owner parameters and canonical
method parameters, checks constraints, then reruns body binding so overloads, expressions and flow
facts are recomputed in the closed context.

| Request condition | Current result |
|---|---|
| Owner is a direct, canonical, top-level generic class, struct, ref struct or interface | Eligible for a source body view |
| Method belongs directly to that merged owner and has a source body | Eligible; method type arguments must match its full declared arity, including an empty vector for a nongeneric method |
| Owner or method argument is open, belongs to another analysis workspace, or has the wrong vector length | Rejected before body publication |
| Owner constraint fails in any compatible partial part | Invalid with `SOL2221`; body is not treated as bound |
| Method constraint fails after owner substitution | Invalid with `SOL2221`; body is not treated as bound |
| Constraint proof is indeterminate | Deferred; no successful cache entry |
| Requested receiver is an inherited projection or the declaration is nested | Rejected by this bounded query |
| Body binding is canceled, has diagnostics, or retains unverified nodes | Not cached as a successful result |
| Body is complete and Bound with no diagnostics or unverified nodes | Cacheable by source method plus ordered owner and method type identities |

The query exposes the constructed owner, both ordered argument vectors, summary, expressions, calls,
and body facts for analysis clients inside the compiler. It is not a public NuGet SDK contract and
ordinary member call analysis does not yet request it automatically. It does not emit FIR or change
the `SOL2400` native boundary. A source-bound call on a generic class remains non-executable until
the regular source pipeline consumes the proof and the independent FIR/runtime gates are implemented.

Rebinding is a necessary but incomplete stage for native member support. Generic properties,
accessors, operators, dispatch, borrowing and escape can add obligations that are not certified by
this query's current direct-body scope. A closed owner can change overload applicability, selected
calls, effects, flow, nullability and definite assignment; simply substituting a signature never
proves those facts. The runtime still needs owner layout, receiver ABI, object descriptors, precise
GC maps, barriers, exception cleanup and separate AOT/ORC qualification.

## Owner and parameter identity

Every generic parameter has a canonical declaration owner and an ordinal. Its written name is used
for source diagnostics; it is not its identity. A class parameter named `T` and a method parameter
also named `T` remain distinct even when both have the same constraints.

| Parameter kind | Owner identity | Ordinal meaning |
|---|---|---|
| Nominal parameter | Canonical class, struct, ref struct or supported nested type declaration | Position in that declaration's parameter list |
| Callable parameter | Canonical method or free-function declaration | Position in that callable's parameter list |
| Nested type parameter | Canonical nested type declaration, with explicitly defined outer capture | Position in the nested declaration's own list |
| Partial declaration parameter | Canonical merged partial type owner | Position shared by every compatible part |

An implementation uses owner and ordinal when comparing type parameters, applying substitutions,
forming overload identities, and constructing instance keys. Source spelling, formatted type text,
file order, and declaration traversal order cannot merge identities.

For a method on `Box<T>` called with `U`, the selected member has two maps:

1. The owner map replaces `Box`'s parameters from the constructed receiver.
2. The method map replaces the selected method's parameters from explicit arguments or inference.

Both maps apply to the original resolved type graph. Inserting a replacement from one map must not
cause that replacement to be rewritten by another map. The concrete method signature, return type,
effects and nested type constructors must all use the composed result.

An open receiver parameter can expose members through declared bounds during source analysis. Its
candidate set is not yet a closed native instance. The enclosing generic body must be rebound after
the receiver becomes concrete; no open owner parameter may remain in verified FIR.

The internal key for a member must retain more than a printed signature. At minimum it distinguishes
the following identities:

| Item | Required identity components | Why the distinction matters |
|---|---|---|
| Nominal owner | Module/source declaration identity and validated partial merge | Same simple type name in another module is unrelated |
| Owner parameter | Canonical owner plus ordinal | A renamed or shadowed `T` is not a different type parameter |
| Member declaration | Canonical owner plus member declaration identity | Same spelling in a partial part may be a duplicate, not an overload |
| Method parameter | Canonical method plus ordinal | The method's `T` is separate from the enclosing class's `T` |
| Constructed owner | Canonical nominal declaration plus ordered closed arguments | `Box<String>` and `Box<int>` are different members/layouts |
| Inherited projection | Base declaration plus complete substituted route result | Two routes may agree or conflict after substitution |
| Accessor operation | Property identity plus getter/setter operation | Read and write have separate visibility and effects |
| Indexer operation | Indexer identity plus read/write mode and mapped indices | A getter result is not automatically writable storage |
| Operator target | Operator family, declaring owner, arity and selected declaration | An unrelated function cannot stand in for an operator |
| Runtime dispatch slot | Canonical slot plus closed owner/method signature | Erased slots can mismatch the verified call ABI |

These identity components remain stable when diagnostics print source aliases or when two constructed
types happen to have the same machine representation. ABI equality does not imply source type
equality, overload equivalence, or interchangeable runtime descriptors.

## Partial type identity

Compatible `partial class`, `partial struct`, `partial ref struct` and `partial object` declarations
form one canonical owner before member lookup. All parts must agree on the module, source name,
category, generic arity, parameter order, compatible constraints and type-wide modifiers. A class
part cannot merge with a struct part. Reordering `K, V` to `V, K` changes the parameter contract and
is invalid.

Members are collected only after the owner group validates. Duplicate method, property, indexer,
operator or constructor identities are reported against their source declarations. A setter is not
silently attached to a property from another part by repeating its name. Primary-constructor
parameters, initialization statements and required-member obligations follow the single-owner
construction contract.

Partial parts share parameter IDs by canonical owner and ordinal. Adding or removing a source file
must not change the meaning of a generic argument. Filename sorting and parser order are not
semantic tie-breakers. If a part invalidates the owner, analysis must not publish a complete member
table assembled from only the remaining parts.

The source layer has an initial partial declaration merge path. This does not prove complete
cross-file generic constraints, generated members, initializer ordering, every override, or runtime
layout. Those requirements remain explicit in the normative contract.

## Nested and companion owners

A nested declaration has its own identity. Whether it captures containing type parameters is a
language rule that must be represented in the resolved owner path. A type parameter cannot be
captured merely because an outer and inner parameter share the spelling `T`.

If a nested type captures its owner, its constructed identity must retain both the outer and inner
arguments, such as `Outer<String>.Inner<int>`. Runtime layout, constraints, member signatures and
reflection policy cannot discard the outer argument when those facts depend on it. If a future
rule defines a nested type as non-capturing, its identity remains independent from the containing
instance.

A companion is a separate singleton declaration, not a synthesized `static` member table. It does
not acquire a particular enclosing instance's generic arguments. Any owner-bound companion feature
would need explicit source syntax and a stable owner identity. The current native slice does not
create generic nested layouts or companion descriptors.

## Constructors

Construction selects a nominal owner first, then a constructor from that owner's candidate set.
Constructors are not inherited ordinary members. Owner arguments are explicit or inferred according
to the generic inference contract; they are substituted before constructor parameter mapping and
overload ranking. If the constructor form has its own method type parameters, those have a separate
owner and constraint set.

The chosen constructor retains parameter names, passing modes, defaults, vararg mapping, effects,
delegation target and source origins. A valid call also has to satisfy primary-constructor,
base-construction, field-initialization and required-property rules. Matching argument types alone
does not establish that construction is valid.

`object` has a canonical singleton identity. Its parts merge as one owner; a call does not create a
per-type-argument object instance. Generic constructor specialization is outside the current native
slice because generic owner layout and initialization have not been verified there.

## Methods and overload selection

The source method candidate set is formed after receiver classification, visibility checks and
constructed-base projection. Owner substitutions are applied to candidate signatures before method
inference. Each candidate receives isolated argument mappings, explicit arguments, input/exact/result
evidence, constraint results, conversion ranks, defaults, vararg decisions and effect information.
Only the selected source candidate publishes its facts.

Method overload identity includes declaration/owner identity, method generic arity, ordered
parameter types after owner substitution, and parameter passing modes. Return type, parameter names,
defaults and effects are validated under their own compatibility rules; they do not let the compiler
merge distinct method declarations. An inaccessible method cannot win or contribute type evidence.

After one source overload is selected, native rejection does not reopen overload ranking. If the
winner is a generic member outside FIR's supported boundary, the compiler reports that boundary.
It does not fall back to a free function, a less-specific method or a candidate that lost source
ranking.

One useful way to review a source candidate is to retain its complete inference record until
selection:

| Candidate fact | Example | Commit point |
|---|---|---|
| Receiver construction | `Box<String>` | After receiver type and accessible member group are known |
| Owner map | `Box.T -> String` | Before checking method parameter applicability |
| Argument map | Named, positional, spread, default, ref/out slots | Before gathering type evidence |
| Method map | `U -> int` | After explicit and inferred evidence closes the candidate |
| Constraint outcome | `U: Comparable<U>` | Before the candidate becomes applicable |
| Conversion ranks | Per source argument | After all candidate parameters are specialized |
| Effect set | Declared and reachable checked effects | After target selection and body analysis |
| Source identity | The exact `AstFunction` or member symbol | When one source overload wins |

A candidate that is deferred, ambiguous, inaccessible, or invalid must not publish a partially
inferred `U` or owner map to another overload. A native lowering error occurs after source selection
and cannot change these inference records.

## Inheritance and overrides

Each generic base edge carries a substitution from the current owner to a constructed base. Lookup
traverses those constructed identities. For `Leaf -> Mid<String> -> Base<[]String>`, an inherited
`read(): T` is observed as `read(): []String`. In an open `Mid<U>` body, it remains `[]U` until the
body is rebound for a closed owner.

If multiple routes reach the same base declaration with the same constructed arguments, the
projection is one identity. Reaching one declaration with incompatible constructed arguments is
not resolved by picking the first path. Invariant arguments are not widened to manufacture a common
base.

Override matching first projects the base method into the derived owner. It then checks method
name, generic arity, substituted parameter types and passing modes. Return covariance, visibility
and checked-effect narrowing are separate compatibility checks. A same-spelled method with a
different owner, generic arity or projected parameter identity is not an override.

Source member analysis includes generic receiver projection and initial override handling, but
verified generic owner dispatch is not in the current FIR slice. A selected virtual or interface
member is not executable until its slot identity and concrete target have been independently
verified.

## Properties and accessors

A generic property retains one canonical declaration identity, a substituted value type, and
separate getter/setter operation contracts. Property visibility and accessor visibility are
independent. A read contributes getter effects; a write contributes setter effects. Compound update
uses the documented receiver stabilization and getter/operator/setter evaluation order.

The resolver uses the property's declared result or accessor signature for inference. It does not
invoke a getter to discover a generic type. An immutable property or a property without a setter is
not writable storage. A getter result is an ordinary value unless the language explicitly defines a
location-producing accessor; it is not implicitly a valid `ref` or `out` argument.

The concrete owner layout determines whether a generic backing field is legal and how it is traced.
Reference owners need object field layout and GC descriptors. Value owners need concrete value
layout and copy rules. Ref structs need lifetime and escape proofs. The current FIR subset does not
generate these generic owner layouts.

Delegated properties select `getValue` and `setValue` at compile time using the substituted
delegate/receiver/property types. Runtime string-name lookup is not member resolution. Getter and
setter effects remain attached to their respective operation; a failed setter does not erase prior
side effects. Generated protocol calls preserve the source property and selected-operator origins.

## Indexers

An indexer is a call-like member. It has a receiver, ordered index arguments, possible default or
vararg mapping where the declaration grammar permits those forms, passing modes, visibility,
effects and generic parameters. Owner arguments are substituted before indexer-specific inference.

Read access selects a getter and returns its specialized result type. Write access selects a setter
for the specialized value type. Compound update evaluates the receiver and each index expression
once, reads once, applies the chosen operator and writes once. Effects attach to the selected getter,
operator and setter separately.

The result of a getter is not automatically an address. `ref`/`out` indexed access is legal only if
source binding proves a stable writable location and the backend can preserve the address through
the required temporaries. The current native generic slice has no generic indexer lowering.

## Operators

User-defined operator lookup is constrained to the operand owner and the placement declared for
that operator family, including the corresponding companion where required. It does not search all
module functions for a matching name. A generic constraint such as an ordering interface does not
invent an addition implementation.

Operator binding uses the closed owner arguments, method type arguments, ordered operands and passing
modes. Checked arithmetic preference, overflow behavior, nullable lifting and assignment location
are separate rules. A selected source operator whose body or layout is outside FIR is rejected after
selection; the compiler does not substitute an intrinsic with different semantics. Generic operator
bodies require per-instance rebinding just like ordinary methods.

## Ref-like owners, `ref`/`out`, and escape

`where T: allows ref struct` permits the ref-like type category in the generic source argument
position. It does not permit heap storage, capture, boxing, return beyond a region, array storage or
passing through an API that lacks the needed lifetime guarantee. Each operation needs an escape and
borrow proof for its concrete instance.

`ref` and `out` use exact storage type identity after owner and method substitutions. No widening,
boxing, nullable unwrapping or covariance converts a location. A getter, operator result, synthesized
copy, unstable receiver, or safe-call result is not a writable address. For `out`, assignment must
hold on every normal return in the closed method control-flow graph.

A ref struct's storage and receiver lifetimes apply across every call and temporary. A nominal
constraint check alone cannot certify that a specialized ref-like body does not escape. The current
generic free-function slice admits only its explicitly verified passing/layout shapes and does not
make owner-member borrowing executable.

## Effects and flow facts

Owner and method substitutions apply to declared checked effects and function types. The selected
constructor, getter, setter, indexer, operator, delegate protocol or virtual target contributes its
own effects at the operation site. An override may narrow effects only under the source exception
hierarchy contract.

Flow and definite assignment are computed from reachable operations and exceptional edges. An
abstract member body that binds through a type parameter bound may select a different accessor,
operator or override after substitution. Therefore the complete analysis must run again for each
closed owner/method tuple before FIR. Replacing `E` in an effect list while reusing stale body
bindings does not complete that proof.

## Runtime representation and Yellow Tea

A closed generic class or struct layout records field order, size, alignment, value/reference
semantics, nullable positions and nested descriptors. Stack maps and heap descriptors must agree on
which fields and spills are managed references. A collector cannot treat arbitrary generic payload
bits as object pointers. Different arguments can change the layout and reference map.

Arrays, nullable values, function objects, nested collections, handles and exceptions each have
their own runtime representation. Generic specialization cannot invent one. Yellow Tea root maps,
write barriers and descriptors must be produced from the exact concrete layout. Host and guest
collectors in separate processes do not share raw object addresses or roots.

No new generic layout or GC descriptor is introduced by the current FIR work. The existing
[Yellow Tea runtime ABI guide](../yellow-tea/compiler-runtime-abi.md) remains a separate contract;
this guide does not claim collector qualification for generic owners.

The implementation should treat representation decisions as a joint contract among type identity,
layout and the collector:

| Source shape | Layout question | Collector/runtime question |
|---|---|---|
| `Box<int>` | Is `int` stored inline and with what alignment? | Is the slot scalar and excluded from tracing? |
| `Box<String>` | Is the String a reference or a value wrapper? | Which exact field is visited, and who owns the String root? |
| `Box<String?>` | How is the null-capable reference encoded? | Is null a valid slot value and still scanned safely? |
| `Pair<T, U>` | Do T and U change padding or field offsets? | Does each concrete field need a different root bit? |
| `ref struct Span<T>` | Can the receiver or fields escape the stack region? | Is the underlying pointer pinned, tracked, or prohibited from heap storage? |
| `Box<() -> String>` | Is the function a code pointer, closure object or pair? | Which environment references are traced? |

These rows illustrate questions that a future verifier must settle for each supported representation;
they are not layout promises for the current runtime. A collector-safe representation also requires
publication barriers, mutation barriers, stack map generation and concurrency rules. Correct field
offsets alone are insufficient when another thread can observe partially initialized storage.

If a closed value is passed to a process-isolated ORC guest, its descriptor and references remain
guest-owned. A host-side generic type identity cannot be used as a raw guest pointer or as a way to
share a Yellow Tea root. The transfer boundary needs a serialized or reconstructed descriptor that
is validated against the guest's runtime ABI.

## Dispatch and artifact boundaries

A source binding records the selected declaration and whether it is statically bound, virtual,
interface-dispatched, companion-qualified or indirect. Generic dispatch must preserve the canonical
owner/method substitutions in its slot identity. Erasure is permitted only if calling convention,
layout, effects, runtime type requirements and GC descriptors are all compatible.

The current native FIR subset does not lower generic virtual methods, generic interface maps,
generic closures or reified runtime descriptors. Generic declarations are not stable C exports.
Any future `cdylib`, `staticlib` or `.solib` export needs a closed signature and explicit ABI,
symbol, dependency, version and import rules. Internal AOT/ORC instances and dynamic/static SolRT
selection do not relax those requirements.

An internal specialization key for a member should include the canonical source declaration,
ordered closed owner arguments, ordered closed method arguments, and any enclosing nested-owner
arguments that the declaration captures. It also needs the source/analysis version and target ABI
when reused across compilations. A short display name such as `map_String_int` can be useful in
diagnostics or debugger output, but it is not a unique identity and should not drive caches or
linking.

If the compiler introduces a mangled name, it must define collision handling, escaping, overload
disambiguation, target decoration and version compatibility. Two source instances can produce the
same low-level signature while retaining different source declarations, effects or owner identity.
The linker may coalesce machine code only after those differences have been proven irrelevant to
semantics, diagnostics and runtime metadata.

An external `.solib` cannot assume that another build uses the same in-memory type IDs. An exported
closed generic wrapper records a stable source symbol, concrete canonical type description, target
triple, runtime ABI, compiler contract version and dependency identity. An import validates these
fields before it resolves the wrapper. An open generic declaration requires a separately designed
dictionary or reified ABI; serializing the name `T` is not such an ABI.

## Phase ownership and diagnostics

| Condition | Owning phase | Current interpretation |
|---|---|---|
| Unknown/inaccessible source member | Source lookup/binding | Source error; inaccessible candidates do not supply evidence |
| Constraint failure | Generic candidate analysis | Candidate is rejected before code generation |
| Conflicting constructed base projections | Member resolution | Incomplete or invalid source candidate set; no first-path winner |
| Member source body outside native subset | Native source admission | `SOL2400`, not a claim the source language forbids it |
| Invalid or inconsistent constructed FIR | Independent verifier | `SOL2402`; must not be hidden as successful source binding |
| Expansion exceeds function/body/type budget | Specialization resource accounting | `SOL2403`; no partial verified program or artifact |
| Cancellation during lookup or specialization | Cancellation boundary | Propagate cancellation and discard unpublished state |

Source errors, deferred analysis, native unsupported cases, verifier failures and resource limits are
different outcomes. The user-facing diagnostic should retain the receiver/call as the primary
origin where applicable and attach the owner declaration, inherited base route, selected member and
generic arguments as notes. A body error in one specialization also includes the instantiation call
chain in deterministic order.

The current FIR JSON schema records a function's closed specialization arguments and each generic
call's selected type arguments. Those IDs are useful inside the serialized compilation graph; they
are not stable names for cross-process packages or public ABI symbols. A consumer must combine them
with the schema version and source declaration/origin context. It must not create a new owner/member
identity from a printed `T` or persist a raw workspace-local numeric ID.

When a future member instance is rejected, diagnostics should identify both the owner and the
member. A primary span points to the source operation whose type or body is unsupported. Notes can
show the constructed receiver, the generic declaration, the ordered owner/method arguments, the
selected inheritance projection and the caller that requested the instance. Repeated specializations
share source body spans, so the instance context is required to explain which closed body failed.

## Required pipeline before generic member FIR

1. Merge and validate all partial owner parts.
2. Build canonical owner and member identities, including owner-qualified parameter ordinals.
3. Resolve the accessible receiver candidate set over constructed inheritance paths.
4. Map source arguments, then solve owner and method substitutions candidate by candidate.
5. Select one source declaration and preserve effects, modes, defaults and source origins.
6. Use the existing internal source-only body view for its supported direct-owner shape; integrate
   complete source call paths, inherited projections and nested owners only after their canonical
   identities and route proofs are implemented.
7. Validate construction, override/dispatch slots, field layout, escape and runtime effects.
8. Build concrete FIR with source-origin identity and verify the graph independently.
9. Generate matching GC descriptors, stack maps, runtime metadata and ABI signatures.
10. Qualify each AOT/ORC and artifact/runtime linkage combination separately.

Passing source inference, selecting one member, substituting a return type, or verifying an LLVM
module alone cannot replace these stages.

Each stage should publish an explicit result state rather than overload a single `null` or empty
list:

| Result state | Meaning | May continue to FIR? |
|---|---|---|
| Bound | One source declaration, full mapping and closed substitutions are known | Only after all native and verifier gates pass |
| Rejected | Source rule is violated, such as an inaccessible member or invalid constraint | No |
| Deferred | Analysis lacks a required fact, such as an open receiver or unsupported contextual expression | No successful instance |
| Ambiguous | Multiple candidates remain tied under the language ranking contract | No |
| Resource limited | A lookup or specialization budget stopped before the candidate set/graph was complete | No; do not choose a partial winner |
| Cancelled | The operation was interrupted before publication | No partial verified result or artifact |
| Native unsupported | Source binding succeeded, but the current target subset has no representation | No; report the native admission boundary |
| Verifier failed | Constructed FIR disagreed with its source facts or violated an invariant | No backend consumption |

This state model is useful for direct calls as well as properties, constructors and operators. It
prevents an incomplete inherited group from looking like “member not found,” a native rejection from
looking like a source type error, and cancellation from looking like an invalid program.

## Per-member proof obligations

The same owner map is applied to all member forms, but each form has additional proof obligations:

| Member form | Source proof | Additional FIR/runtime proof |
|---|---|---|
| Method | Candidate mapping, constraints, overload, access and effects | Closed body reanalysis and direct or dispatch target identity |
| Constructor | Closed type, selected constructor and initialization path | Allocation/layout, field initialization, base call and failure cleanup |
| Property getter | Read access, substituted result and getter effects | Backing location or call lowering, plus root-map compatibility |
| Property setter | Write access, exact assigned type and setter effects | Mutable storage, write barrier and exception/cleanup behavior |
| Indexer getter | Ordered index mapping and getter identity | Receiver/index stabilization and result representation |
| Indexer setter | Ordered indices plus exact value parameter | Address/temporary behavior, write barrier and failure ordering |
| Operator | Operator family/placement, exact operands and checked semantics | Concrete target, overflow and nullable behavior |
| Delegated accessor | Protocol members on the substituted delegate type | Generated call identity, runtime storage and source-origin mapping |
| Virtual method | Override projection and slot compatibility | Closed slot, concrete dispatch target and ABI equality |
| Interface method | Constructed interface identity and implementation mapping | Interface map, runtime descriptor and call convention |

The compiler may implement these forms in separate milestones. It must not claim a generic-member
feature because only one member kind, one owner category, or one artifact path has passed.

## Completion checklist

Generic owner/member support should not be marked complete until the claimed source categories and
artifact combinations have evidence for:

- stable partial and nested owner identities, including import aliases and source-set changes;
- composed owner/method substitution, constraints and overload winner identity;
- direct closed member body reanalysis is complete only for the internal bounded query; source-call
  integration, inherited/nested routes and generic FIR admission still require separate evidence;
- constructors, field initialization, properties/accessors, indexers, operators and delegates;
- virtual and interface dispatch, override effects and constructed base projections;
- per-instance body rebinding, flow, definite assignment, nullability, effects, borrowing and escape;
- class/value/ref-like layouts, reference maps, stack roots and runtime descriptors;
- exact `ref`/`out` locations and normal-return `out` assignment;
- deterministic work bounds, recursion/cancellation, diagnostics and source maps;
- AOT, process-isolated ORC, dynamic/static SolRT and explicit artifact export rules.

The example matrix includes rejected cases as well as source-bound cases. A normative fixture is a
contract for future implementation, not a report that a parser, semantic test or native backend has
already executed it.

Before widening the boundary, review each new owner category against the source matrix, verifier,
runtime layout, GC descriptor and artifact contract together.
