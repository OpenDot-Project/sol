// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class InterfaceImplementationBoundaryTests
{
    [Theory]
    [InlineData(8189, true)]
    [InlineData(8190, false)]
    public void VisitBudgetIncludesEmptyBranchesAndTheImplicitClassBase(int branches, bool bound)
    {
        var source = new StringBuilder("interface Root { func f(): int = 1 }\n");
        for (int index = 0; index < branches; index++)
        { source.Append("interface I").Append(index).Append(" {}\n"); }
        source.Append("class C: Root, ").AppendJoin(", ", Enumerable.Range(0, branches).Select(index => "I" + index)).Append(" {}");
        var analysis = GenericMemberProofTestSource.Analyze(source.ToString());
        var records = analysis.InterfaceImplementations.Values.SelectMany(records => records)
            .Where(record => record.Receiver.Declaration!.Name == "C").ToArray();
        if (bound) { Assert.Equal(BodyBindingStatus.Bound, Assert.Single(records).Status); }
        else { Assert.Empty(records); Assert.False(analysis.IsComplete); }
    }
    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void DiamondPathBudgetNeverSelectsFromATruncatedProviderSet(int paths, bool bound)
    {
        var source = new StringBuilder("interface Root { func f(): int = 1 }\n");
        for (int index = 0; index < paths; index++)
        { source.Append("interface I").Append(index).Append(": Root {}\n"); }
        source.Append("class C: ").AppendJoin(", ", Enumerable.Range(0, paths).Select(index => "I" + index)).Append(" {}");
        var analysis = GenericMemberProofTestSource.Analyze(source.ToString());
        var records = analysis.InterfaceImplementations.Values.SelectMany(records => records)
            .Where(record => record.Receiver.Declaration!.Name == "C").ToArray();
        if (bound) { Assert.Equal(BodyBindingStatus.Bound, Assert.Single(records).Status); }
        else { Assert.Empty(records); Assert.False(analysis.IsComplete); }
    }
    [Theory]
    [InlineData(127, true)]
    [InlineData(128, false)]
    public void InterfaceDepthBudgetCannotPublishAPartialSuccessfulSelection(int interfaces, bool bound)
    {
        var source = new StringBuilder("interface I0 { func f(): int = 1 }\n");
        for (int index = 1; index <= interfaces; index++)
        { source.Append("interface I").Append(index).Append(": I").Append(index - 1).Append(" {}\n"); }
        source.Append("class C: I").Append(interfaces).Append(" {}\nfunc use(c: C): int = c.f()");
        var analysis = GenericMemberProofTestSource.Analyze(source.ToString());
        var records = analysis.InterfaceImplementations.Values.SelectMany(records => records)
            .Where(record => record.Receiver.Declaration!.Name == "C").ToArray();
        if (bound) { Assert.Equal(BodyBindingStatus.Bound, Assert.Single(records).Status); }
        else { Assert.Empty(records); Assert.False(analysis.IsComplete); }
    }

    [Fact]
    public void CancellationDoesNotPoisonTheSourceWorkspace()
    {
        var sources = SourceAnalysis.Create(AstTestSources.Session("module app\ninterface I { func f(): int = 1 }\nclass C: I {}",
            GenericMemberProofTestSource.Core), TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => BodyAnalysis.Create(sources, cancellation.Token));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        Assert.False(analysis.HasErrors);
        Assert.Equal(BodyBindingStatus.Bound, analysis.InterfaceImplementations.Values.SelectMany(records => records)
            .Single(record => record.Receiver.Declaration!.Name == "C").Status);
    }

    [Fact]
    public async Task IndependentBuildsKeepImportedPartialAndConstructedIdentities()
    {
        var sources = SourceAnalysis.Create(AstTestSources.Session(
            "module library\ninterface I<T> { func f(value: T): T = value }",
            "module app\nimport library\npartial class C: I<int> {}",
            "module app\npartial class C {}\nfunc use(c: C): int = c.f(1)",
            GenericMemberProofTestSource.Core), TestContext.Current.CancellationToken);
        var analyses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            BodyAnalysis.Create(sources, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));
        foreach (var analysis in analyses)
        {
            Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
            var call = Assert.Single(analysis.Calls);
            Assert.Equal("library.I", call.Receiver!.Name);
            Assert.Equal("int", Assert.Single(call.Receiver.Arguments).Name);
            Assert.Equal(2, call.InterfaceImplementation!.Receiver.Declaration!.Parts.Length);
        }
        Assert.Equal(analyses[0].Calls[0].Target, analyses[1].Calls[0].Target);
        Assert.NotSame(analyses[0].Calls[0].InterfaceImplementation, analyses[1].Calls[0].InterfaceImplementation);
    }

    [Fact]
    public void ConflictInvalidatesRelatedCallBodyAndLambda()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface A { func f(): int = 1 }\n"
            + "interface B { func f(): int = 2 }\nclass C: A, B {}\nfunc use(c: C): () -> int = { c.f() }");
        Assert.Contains(analysis.Diagnostics, d => d.Code == "SOL2236");
        Assert.Equal(BodyBindingStatus.Invalid, analysis.Bodies.Single(body => body.Declaration.Name == "use").Status);
        Assert.False(Assert.Single(analysis.LambdaBodies).IsComplete);
    }

    [Fact]
    public void InferredContractMismatchInvalidatesAnAlreadyBoundCaller()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface I { func f() = \"text\" }\n"
            + "class C: I { override func f(): int = 1 }\nfunc use(c: C): () -> int = { c.f() }");
        Assert.Contains(analysis.Diagnostics, d => d.Code == "SOL2234");
        Assert.Equal(BodyBindingStatus.Invalid, Assert.Single(analysis.Calls).InterfaceImplementation!.Status);
        Assert.Equal(BodyBindingStatus.Invalid, analysis.Bodies.Single(body => body.Declaration.Name == "use").Status);
        Assert.False(Assert.Single(analysis.LambdaBodies).IsComplete);
    }

    [Fact]
    public void SpecializedCallerCannotCacheAnInferredInterfaceContractMismatch()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface I<T> { func f(value: T) = \"text\" }\n"
            + "class C<T>: I<T> { override func f(value: T): int = 1 }\nfunc <T> use(c: C<T>, value: T): int = c.f(value)");
        var specialized = analysis.GetGenericBody(GenericMemberProofTestSource.Function(analysis, "use"),
            [analysis.Types.GetPrimitive("int")], TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Invalid, specialized.Summary.Status);
    }

    [Fact]
    public void SpecializedGetterUseCarriesTheSelectedSourceBodyFailure()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface I { val item: int { get = \"bad\" } }\n"
            + "class C: I {}\nfunc <T> use(c: C, unused: T): int = c.item");
        var specialized = analysis.GetGenericBody(GenericMemberProofTestSource.Function(analysis, "use"),
            [analysis.Types.GetPrimitive("int")], TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Invalid, specialized.Summary.Status);
        Assert.Equal(BodyBindingStatus.Invalid, Assert.Single(specialized.InterfaceUses).Value.Selection.Status);
    }

    [Theory]
    [InlineData("interface I { val item: int { get = 1 } }\nclass C: I {}\nfunc use(c: C): int = c.item")]
    [InlineData("interface I { this[index: int]: int { get = index } }\nclass C: I {}\nfunc use(c: C): int = c[1]")]
    [InlineData("interface I { func f(): int }\nclass C(delegate: I): I by delegate {}\nfunc use(c: C): int = c.f()")]
    [InlineData("interface I { func f(): int = 1 }\nclass C: I { override func f(): int = super<I>.f() }")]
    public void SourceSelectionNeverExpandsNativeAdmission(string source)
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(source + "\nobject Main { func main(args: []String) {} }")],
            TestContext.Current.CancellationToken);
        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, d => d.Code == "SOL2400");
    }
}
