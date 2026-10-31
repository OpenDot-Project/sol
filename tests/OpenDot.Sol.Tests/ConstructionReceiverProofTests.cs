// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class ConstructionReceiverProofTests
{
    [Theory]
    [InlineData("class C { val item: int = 1; constructor() { val alias = this; alias.check() }; private func check(): int = this.item }")]
    [InlineData("class C { var item: int; constructor() { val alias = this; alias.initialize() }; private func initialize() { this.item = 1 } }")]
    [InlineData("class C { val item: int = 1; constructor() { this.first() }; private func first(): int = this.second(); private func second(): int = this.item }")]
    [InlineData("class C { val item: int = 1; constructor() { this.read() }; private func read() = this.item }")]
    [InlineData("class C { var item: int; constructor() { this.initialize(true) }; private func initialize(flag: bool) { this.item = 1; if flag { this.initialize(false) } } }")]
    [InlineData("class C { val item: int = 1; constructor() { val alias = this.self(); alias.read() }; private func self(): C = this; private func read(): int = this.item }")]
    [InlineData("class C { val item: int = 1; val other: int { get = this.item }; constructor() { val copy = this.other } }")]
    [InlineData("class C { var item: int; constructor() { val initialize: ()->() = { this.item = 1 }; initialize() } }")]
    [InlineData("class C { var item: int; constructor() { val initialize = func(): () { this.item = 1 }; initialize() } }")]
    [InlineData("class C { val item: int; constructor() { var value: int; val initialize = { value = 1 }; initialize(); this.item = value } }")]
    [InlineData("class C { var item: int; this[index: int]: int { get = this.item; set { this.item = value } }; constructor() { this[0] = 1 } }")]
    [InlineData("class C { var item: int; constructor() { this.run({ this.item = 1 }) }; private func run(callback: ()->()) { callback() } }")]
    [InlineData("open class B { open func read(): int = 1 }\nclass C: B { constructor() { this.read() }; final override func read(): int = 2 }")]
    [InlineData("open class B { open func read(): int = 1 }\nclass C: B { constructor() { this.read() } }")]
    public void ExactReceiverHelpersRetainValidFieldProofs(string source)
    {
        var analysis = GenericMemberProofTestSource.Analyze(source + "\nfunc use(): C = C()");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.Equal(BodyBindingStatus.Bound, analysis.ConstructionProofs.Values.Single(proof =>
            proof.Selection.Owner.Declaration!.Name == "C" && proof.Selection.Site is AstCall { Origin.Text: "C()" }).Status);
    }

    [Theory]
    [InlineData("func publish(value: Object) {}\nclass C { constructor() { val alias = this; publish(alias) } }", "SOL2273")]
    [InlineData("class Holder { var value: Object? = null }\nclass C(holder: Holder) { init { holder.value = this } }", "SOL2273")]
    [InlineData("open class C { constructor() { this.read() }; open func read(): int = 1 }", "SOL2272")]
    [InlineData("open class B { constructor() { this.read() }; open func read(): int = 1 }\nclass C: B { override func read(): int = 2 }", "SOL2272")]
    [InlineData("open class B { constructor() { val copy = this.item }; open val item: int { get = 1 } }\nclass C: B { override val item: int { get = 2 } }", "SOL2272")]
    [InlineData("class C { val item: int; constructor() { val alias = this; alias.read(); this.item = 1 }; private func read(): int = this.item }", "SOL2271")]
    [InlineData("class C { val item: int; val other: int { get = this.item }; constructor() { val copy = this.other; this.item = 1 } }", "SOL2271")]
    [InlineData("class C { val item: int; constructor() { val read: ()->int = { this.item }; read(); this.item = 1 } }", "SOL2271")]
    [InlineData("class C { val item: int; constructor() { val read = func(): int = this.item; read(); this.item = 1 } }", "SOL2271")]
    [InlineData("func publish(value: Object) {}\nclass C { constructor() { val self: ()->C = { this }; publish(self()) } }", "SOL2273")]
    [InlineData("class C { val item: int; constructor() { var alias: C? = null; val assign = { alias = this }; assign(); alias!!.read(); this.item = 1 }; private func read(): int = this.item }", "SOL2271")]
    [InlineData("class C { val item: int; this[index: int]: int { get = this.item }; constructor() { val copy = this[0]; this.item = 1 } }", "SOL2271")]
    [InlineData("class C { val item: int; constructor() { this.run({ this.item }); this.item = 1 }; private func run(callback: ()->int): int = callback() }", "SOL2271")]
    [InlineData("class Holder { var value: Object? = null }\nclass C(holder: Holder) { init { holder.value = this.self() }; private func self(): C = this }", "SOL2273")]
    [InlineData("class C { val item: int; constructor() { this.initialize(true) }; private func initialize(flag: bool) { if flag { this.initialize(false) }; val copy = this.item } }", "SOL2271")]
    public void AliasesAndHelpersCannotHideUnsafeConstruction(string source, string code)
    {
        var analysis = GenericMemberProofTestSource.Analyze(source);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.False(analysis.IsComplete);
    }

    [Fact]
    public void ARecursiveAllocationIsNotAThisDelegationCycle()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class C() { val next: C = C() }");
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2275");
        Assert.False(analysis.IsComplete);
        Assert.Contains(analysis.ConstructionProofs.Values, proof => proof.Status == BodyBindingStatus.Deferred);
    }

    [Fact]
    public void PassingTheReceiverIntoAnotherAllocationNeedsASeparateAliasProof()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Holder(value: C) { init { value.read() } }\n"
            + "class C { val item: int; constructor() { val holder = Holder(this); this.item = 1 }; func read(): int = this.item }");
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.ConstructionProofs.Values, proof => proof.Selection.Owner.Declaration?.Name == "C"
            && proof.Status == BodyBindingStatus.Bound);
    }

    [Fact]
    public void AnUnknownFunctionValueCannotCertifyItsReceiverArgument()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class C(callback: (C)->()) { init { callback(this) } }");
        Assert.False(analysis.IsComplete);
        Assert.Contains(analysis.ConstructionProofs.Values, proof => proof.Status == BodyBindingStatus.Deferred);
    }
}
