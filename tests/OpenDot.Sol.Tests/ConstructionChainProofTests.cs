// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class ConstructionChainProofTests
{
    [Theory]
    [InlineData("class C { val value: int; init { this.value = 1 } }\nfunc use(): C = C()")]
    [InlineData("class C(value: int) { val stored: int = value; constructor(): this(1) {} }\nfunc use(): C = C()")]
    [InlineData("open class B(val item: int) {}\nclass C(value: int): B(value) { val copied: int = this.item }\nfunc use(): C = C(1)")]
    [InlineData("class C<T>(val item: T) { constructor(value: T, ignored: int): this(value) {} }\nfunc use(): C<int> = C<int>(1, 0)")]
    [InlineData("class C { constructor(value: int) {} constructor(): this(1) {} }\nfunc use(): C = C()")]
    [InlineData("class C { val item: int = 1; constructor() { this.check() }; private func check(): int = this.item }\nfunc use(): C = C()")]
    [InlineData("class C { var item: int; constructor() { this.initialize() }; private func initialize() { this.item = 1 } }\nfunc use(): C = C()")]
    public void ValidConstructionChainsHaveCompleteCallProofs(string source)
    {
        var analysis = GenericMemberProofTestSource.Analyze(source);
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var call = analysis.ConstructionProofs.Values.Last(proof => proof.Selection.Site is OpenDot.Sol.Ast.AstCall
            && proof.Selection.Owner.Declaration!.Name == "C");
        Assert.Equal(BodyBindingStatus.Bound, call.Status);
    }

    [Theory]
    [InlineData("class C { constructor(): this(1) {} constructor(value: int): this() {} }", "SOL2275")]
    [InlineData("open class B(value: int) {}\nclass C: B {}", "SOL2274")]
    [InlineData("class C(value: int) { constructor() {} }", "SOL2274")]
    [InlineData("class C { val item: int; constructor() { this.read(); this.item = 1 }; private func read(): int = this.item }", "SOL2271")]
    [InlineData("class C(flag: bool) { val item: int; init { if flag { this.item = 1 } } }", "SOL2270")]
    [InlineData("class C { val item: int = 1; init { this.item = 2 } }", "SOL2241")]
    [InlineData("open class B { private constructor() {} }\nclass C: B {}", "SOL2214")]
    [InlineData("class Failure: Exception {}\nfunc fail(): int throws Failure { throw Failure() }\nclass C() { val item: int = fail() }", "SOL2250")]
    public void InvalidUnusedConstructionIsDiagnosed(string source, string code)
    {
        var analysis = GenericMemberProofTestSource.Analyze(source);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.False(analysis.IsComplete);
    }
}
