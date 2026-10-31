// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Text;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class ConstructionBoundaryProofTests
{
    [Fact]
    public void ClosedOwnerProofsAreIsolatedAndCancellationPrecedesCacheAccess()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Box<T>(val item: T) {}\n"
            + "func <T> make(item: T): Box<T> = Box<T>(item)");
        var function = GenericMemberProofTestSource.Function(analysis, "make");
        var first = analysis.GetGenericBody(function, [analysis.Types.GetPrimitive("int")], TestContext.Current.CancellationToken);
        var second = analysis.GetGenericBody(function, [analysis.Types.GetPrimitive("bool")], TestContext.Current.CancellationToken);
        Assert.Equal("int", first.ConstructionProofs.Values.Single().Selection.Owner.Arguments.Single().Name);
        Assert.Equal("bool", second.ConstructionProofs.Values.Single().Selection.Owner.Arguments.Single().Name);
        Assert.Equal(BodyBindingStatus.Bound, first.Summary.Status);
        Assert.Equal(BodyBindingStatus.Bound, second.Summary.Status);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => analysis.GetGenericBody(function,
            [analysis.Types.GetPrimitive("int")], cancellation.Token));
        var foreign = GenericMemberProofTestSource.Analyze("func <T> make(item: T): T = item");
        Assert.Throws<ArgumentException>(() => analysis.GetGenericBody(GenericMemberProofTestSource.Function(foreign, "make"),
            [analysis.Types.GetPrimitive("int")], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AHelperDepthLimitDoesNotCertifyAPartialFieldAssignment()
    {
        var source = new StringBuilder("class C { var item: int; constructor() { this.h0() };\n");
        for (int index = 0; index < 130; index++)
        { source.Append("private func h").Append(index).Append("() { this.h").Append(index + 1).Append("() };\n"); }
        source.Append("private func h130() { this.item = 1 } }");
        var analysis = GenericMemberProofTestSource.Analyze(source.ToString());
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.ConstructionProofs.Values, proof => proof.Selection.Owner.Declaration?.Name == "C"
            && proof.Status == BodyBindingStatus.Bound);
    }

    [Fact]
    public void LateStorageValidationInvalidatesCallsLambdasAndSpecializedBodies()
    {
        var analysis = GenericMemberProofTestSource.Analyze("ref struct R {}\n"
            + "class C { val item: R; constructor(item: R) { this.item = item } }\n"
            + "func <T> make(item: R, ignored: T): C = C(item)\n"
            + "func use(item: R): ()->C = { C(item) }");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2260");
        Assert.DoesNotContain(analysis.ConstructionProofs.Values, proof => proof.Selection.Owner.Declaration?.Name == "C"
            && proof.Status == BodyBindingStatus.Bound);
        Assert.False(Assert.Single(analysis.LambdaBodies).IsComplete);
        var specialized = analysis.GetGenericBody(GenericMemberProofTestSource.Function(analysis, "make"),
            [analysis.Types.GetPrimitive("int")], TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Invalid, specialized.Summary.Status);
    }

    [Fact]
    public void AConstructionHelperFailureDoesNotMakeItsNormalBodyAppearSuccessful()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class C { constructor() { this.bad() }; private func bad(): int = \"bad\" }");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
        Assert.Equal(BodyBindingStatus.Invalid, analysis.Bodies.Single(body => body.Declaration.Name == "bad").Status);
    }

    [Fact]
    public void RecursiveAllocationInADefaultRemainsDeferredWithoutADelegationCycle()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class C { constructor(item: C = C()) {} }\nfunc use(): C = C()");
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2275");
        Assert.Contains(analysis.ConstructionProofs.Values, proof => proof.Status == BodyBindingStatus.Deferred);
    }

    [Fact]
    public void AClosedConstructorEffectFailureDoesNotInvalidateAnotherOwnerInstance()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class A: Exception {}\nclass B: Exception {}\ninterface I {}\n"
            + "func select(value: Object, target: I): I throws A = target\n"
            + "func select(value: String, target: I): I throws B = target\n"
            + "class C<T: Object>(value: T, target: I) throws A: I by select(value, target) {}\n"
            + "func bad(value: String, target: I): C<String> throws A = C<String>(value, target)\n"
            + "func good(value: Object, target: I): C<Object> throws A = C<Object>(value, target)");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2250");
        var good = Assert.Single(analysis.ConstructionProofs.Values, proof =>
            proof.Selection.Site.Origin.Text == "C<Object>(value, target)");
        var bad = Assert.Single(analysis.ConstructionProofs.Values, proof =>
            proof.Selection.Site.Origin.Text == "C<String>(value, target)");
        Assert.Equal(BodyBindingStatus.Bound, good.Status);
        Assert.Equal(BodyBindingStatus.Invalid, bad.Status);
    }

    [Theory]
    [InlineData("class C(val item: int) {}\nfunc use(): C = C(1)")]
    [InlineData("class C { var item: int; constructor() { this.initialize() }; private func initialize() { this.item = 1 } }")]
    [InlineData("class C { constructor(value: int) {} constructor(): this(1) {} }\nfunc use(): C = C()")]
    public void SourceConstructionProofsDoNotExpandNativeAdmission(string source)
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(source
            + "\nobject Main { func main(args: []String) {} }")], TestContext.Current.CancellationToken);
        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
    }
}
