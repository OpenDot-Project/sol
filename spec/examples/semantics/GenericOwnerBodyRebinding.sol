// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: source-analysis examples for bounded on-demand direct and inherited generic member body views; no FIR admission is implied.
module source.spec.examples.semantics.generic_owner_body_rebinding

// Generic owner body rebinding
// ============================
//
// This example matrix distinguishes declaration-time analysis, an on-demand closed source body
// view, and verified FIR/native output. The source body view is an internal analysis query. The
// ordinary call-analysis path now requests a bounded proof for selected closed owner methods.
// `Body: Bound` below describes source evidence, not an executable generic owner method.
//
// FIR continues to reject generic class/struct/ref-struct/interface owners and their members with
// SOL2400. Source binding, a closed body view, and an emitted native instance are separate results.

interface Named {}

class Widget: Object, Named {}
class Other: Object {}
class Text: Object {}

// 1. Owner and method parameters retain separate identities.

class Shadowed<T> {
    func <T> echo(value: T): T { this; value }
}

func useShadowed(owner: Shadowed<Widget>, value: Text): Text = owner.echo<Text>(value)

// expect: shadowed.identity | Body: Bound for owner T=Widget and method T=Text; `this` is Shadowed<Widget>; value/result are Text | FIR: Rejected(SOL2400, generic owner member).

// 2. Closing an owner can change the selected overload.

class Choice<T: Object> {
    func choose(value: Object): int = 1
    func choose(value: Widget): Text = "widget"
    func resolve(value: T) = choose(value)
}

func resolveWidget(owner: Choice<Widget>, value: Widget): Text = owner.resolve(value)

// expect: overload.closed-owner | Open body selects choose(Object); on-demand Choice<Widget>.resolve rebinds the original call and selects choose(Widget), returning Text | FIR: Rejected(SOL2400).

// 3. Both constraint owners are checked after substitution.

class Constrained<T: Named> {
    func <U> accept(value: U): U where U: T = value
}

func acceptEntity(owner: Constrained<Widget>, value: Widget): Widget = owner.accept<Widget>(value)

// expect: constraints.satisfied | Body: Bound after owner T=Widget and method U=Widget are checked | FIR: Rejected(SOL2400).

func rejectOther(owner: Constrained<Widget>, value: Other): Other = owner.accept<Other>(value)

// expect: constraints.method-failure | Body: Invalid, SOL2221 for Other not satisfying Named | FIR: no instance.

// 4. The body result is recomputed, rather than patched from the open summary.

class Sequence<T> {
    func head(values: []T): T = values[0]
    func nullable(value: T?): T? = value
}

func sequenceResults(owner: Sequence<Text>, values: []Text, value: Text?): Text? {
    val item: Text = owner.head(values)
    owner.nullable(value)
}

// expect: result.substitution | Body: head parameter/result and local item are []Text/Text; nullable result is Text? | FIR: Rejected(SOL2400).

// 5. Reachable checked effects are rebound with the selected body call.

class ReadFailure: Exception {}

func readObject(value: Object): () = ()
func readWidget(value: Widget): () throws ReadFailure {}

class Reader<T: Object> {
    func read(value: T): () = readObject(value)
}

func closedReader(owner: Reader<Widget>, value: Widget): () throws ReadFailure = owner.read(value)

// expect: effect.closed-overload | The open body binds readObject; Reader<Widget> rebind selects readWidget and adds ReadFailure to the escaping effects | FIR: Rejected(SOL2400).

// 6. Nullable results and checked calls keep their source identities.

func readValue(): () throws ReadFailure {}

class NullableReader<T> {
    func maybe(value: T?): T? throws ReadFailure { readValue(); value }
}

func nullableText(owner: NullableReader<Text>, value: Text?): Text? throws ReadFailure = owner.maybe(value)

// expect: nullability.effects | Body: Bound; result is Text? and the reachable readValue call contributes ReadFailure | FIR: Rejected(SOL2400).

// 7. Ref/out and definite assignment use the closed body flow.

class Output<T> {
    func assign(out target: T, value: T): () { target = value }
    func missing(out target: T): () {}
}

func writeText(owner: Output<Text>, out target: Text, value: Text): () = owner.assign(out target, value)

// expect: out.assigned | Body: Bound; target has exact substituted storage type Text and is assigned on every normal return | FIR: Rejected(SOL2400).

// expect: out.missing | Body: Invalid with SOL2242; the out target is not assigned on normal return | FIR: no instance.

// 8. Partial declarations share one canonical owner parameter vector.

partial class PartialBox<T> {
    func echo(value: T): T = value
}

partial class PartialBox<T> {}

func partialEcho(owner: PartialBox<Text>, value: Text): Text = owner.echo(value)

// expect: partial.owner | Body: Bound when queried for the method in either validated part; owner parameter identity is the merged PartialBox ordinal | FIR: Rejected(SOL2400).

// 9. A multi-edge route supplies the canonical declaring-owner arguments.

open class Base<T> {
    func <U> pair(ownerValue: T, value: U): Pair<T, U> = Pair(ownerValue, value)
}

open class Middle<V>: Base<[]V> {}
class Leaf: Middle<Text> {}

func inheritedPair(owner: Leaf, ownerValue: []Text, value: int): Pair<[]Text, int> =
    owner.pair<int>(ownerValue, value)

// expect: inherited.multi-edge | For the selected Base<T>.pair method, GetGenericMemberBodyForReceiver(Base.pair, Leaf, [int]) returns Projection: Unique with canonical owner Base<[]Text> and the route [Leaf, Middle<Text>, Base<[]Text>]. Body: Bound uses owner T=[]Text and method U=int; `this` is Base<[]Text>, and the body result is Pair<[]Text,int>. Selected closed member calls request this query through the automatic body-proof stage. | FIR: Rejected(SOL2400).

// 10. A diamond collapses only when every route reaches the same constructed owner.

interface Root<T> {
    func <U> pair(left: T, right: U): Pair<T, U> = Pair(left, right)
    func <U> echo(value: U): U = value
}

interface Left<T>: Root<T> {}
interface Right<T>: Root<T> {}
class Diamond: Left<Text>, Right<Text> {}

func diamondPair(owner: Diamond, left: Text, right: int): Pair<Text, int> =
    owner.pair<int>(left, right)

// expect: inherited.collapsible-diamond | GetGenericMemberBodyForReceiver(Root.pair, Diamond, [int]) returns Projection: Unique with canonical owner Root<Text> and retains both routes [Diamond, Left<Text>, Root<Text>] and [Diamond, Right<Text>, Root<Text>]. Body: Bound uses T=Text and U=int. | FIR: Rejected(SOL2400).

// 11. Conflicting constructed arguments reject the projection; no body is published.

interface TextRoot: Root<Text> {}
interface IntRoot: Root<int> {}
class Conflicting: TextRoot, IntRoot {}

func conflictingEcho(owner: Conflicting, value: int): int = owner.echo<int>(value)

// expect: inherited.conflicting-projection | This is intentionally conflicting/incomplete source input: two routes reach the same Root declaration as Root<Text> and Root<int>. GetGenericMemberBodyForReceiver(Root.echo, Conflicting, [int]) returns Projection: Ambiguous with the distinct constructed candidates and Body: null. It is not a successful body view; traversal order cannot choose one owner. | FIR: no instance; ordinary member binding cannot treat this query as a unique source selection.

// expect: api.closed-vectors | Direct body-view requests reject open owner/method arguments, foreign-workspace ResolvedType values, wrong owner symbols, wrong arity, nested owners, and incomplete vectors before publishing a body. The receiver query requires a direct generic owner method and returns no body for Missing, Ambiguous, or LimitExceeded projection statuses. | FIR: unaffected.

// expect: cache.completed-only | Only complete Bound results without diagnostics or unverified nodes are cached by source method plus ordered owner/method type IDs; invalid/deferred/canceled work is not cached | FIR: unaffected.

// 12. Source rebinding does not grant layout or code-generation permissions.

// A successful body view proves no object size/alignment, field order, receiver ABI, virtual slot,
// allocation behavior, exception unwind, escape lifetime, runtime type descriptor, GC root map,
// write barrier, AOT body, or ORC body. Those remain independent native and runtime gates.
