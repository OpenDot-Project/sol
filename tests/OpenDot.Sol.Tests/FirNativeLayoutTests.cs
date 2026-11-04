// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirNativeLayoutTests
{
    [Fact]
    public void BasePrefixPrimaryOrderCanonicalFieldsAndGcOffsetsRemainDistinct()
    {
        var analysis = GenericMemberProofTestSource.Analyze("open class B { val baseValue: long = 0 }\n"
            + "class C(val z: byte, val a: long): B { var beta: String? = null; var alpha: int = 0; val computed: int { get = 42 } }");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var owner = Nominal(analysis, "C");
        var graph = new FirNativeLayoutBuilder(analysis, TestContext.Current.CancellationToken).Build([owner]);
        var layout = graph.Single(type => type.Type == owner);
        Assert.Equal(["baseValue", "z", "a", "alpha", "beta"], layout.Storage.Select(slot => slot.Source.Name));
        Assert.Equal([16L, 24L, 32L, 40L, 48L], layout.Storage.Select(slot => slot.Offset));
        Assert.Equal([48u], layout.ReferenceOffsets);
        Assert.Equal(56, layout.Size); Assert.Equal(16, layout.Alignment);
        Assert.Equal("B", layout.Base!.Declaration!.Name);
        Assert.DoesNotContain(layout.Storage, slot => slot.Source.Name == "computed");
    }

    [Fact]
    public void ClosedOwnersHaveSeparateStorageAndSelfReferencesDoNotExpandForever()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Box<T>(val item: T) {}\nclass Node(var next: Node?) {}");
        var scalar = Nominal(analysis, "Box", analysis.Types.GetPrimitive("int"));
        var text = Nominal(analysis, "Box", Nominal(analysis, "String", module: "std.core"));
        var node = Nominal(analysis, "Node");
        var layouts = new FirNativeLayoutBuilder(analysis, TestContext.Current.CancellationToken).Build([scalar, text, node]);
        var scalarLayout = layouts.Single(layout => layout.Type == scalar);
        var textLayout = layouts.Single(layout => layout.Type == text);
        Assert.NotEqual(scalarLayout.Identity, textLayout.Identity);
        Assert.NotEqual(scalarLayout.TypeId, textLayout.TypeId);
        Assert.Equal(4, Assert.Single(scalarLayout.Storage).Size); Assert.Empty(scalarLayout.ReferenceOffsets);
        Assert.Equal(8, Assert.Single(textLayout.Storage).Size); Assert.Equal([16u], textLayout.ReferenceOffsets);
        Assert.Equal([16u], layouts.Single(layout => layout.Type == node).ReferenceOffsets);
        Assert.Equal(1, layouts.Count(layout => layout.Type == node));
    }

    [Fact]
    public void InterfaceStorageUsesOneCellReferenceAndOnePreciseRootOffset()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface I { func read(): int }\nclass Holder(val value: I?) {}");
        var owner = Nominal(analysis, "Holder");
        var graph = new FirNativeLayoutBuilder(analysis, TestContext.Current.CancellationToken).Build([owner]);
        var layout = graph.Single(type => type.Type == owner);
        var field = Assert.Single(layout.Storage);
        Assert.True(field.UsesInterfaceCell); Assert.True(field.IsGcReference);
        Assert.Equal(8, field.Size); Assert.Equal([16u], layout.ReferenceOffsets);
        Assert.True(graph.Single(type => type.Type.Declaration?.Name == "I").IsInterface);
    }

    [Fact]
    public void PartialInputOrderAndWorkspaceIdsDoNotChangeTheAbi()
    {
        const string first = "module app; partial class Item { var zebra: int; constructor() { this.zebra = 1; this.alpha = 2 } }";
        const string second = "module app; partial class Item { var alpha: int }";
        var a = Analyze(first, second, GenericMemberProofTestSource.Core);
        var b = Analyze(GenericMemberProofTestSource.Core, second, first);
        var left = new FirNativeLayoutBuilder(a, TestContext.Current.CancellationToken).Build([Nominal(a, "Item")]);
        var right = new FirNativeLayoutBuilder(b, TestContext.Current.CancellationToken).Build([Nominal(b, "Item")]);
        Assert.Equal(left.Select(layout => layout.Identity), right.Select(layout => layout.Identity));
        Assert.Equal(left.Select(layout => layout.TypeId), right.Select(layout => layout.TypeId));
        Assert.Equal(left.SelectMany(layout => layout.Storage).Select(slot => (slot.Identity, slot.Offset)),
            right.SelectMany(layout => layout.Storage).Select(slot => (slot.Identity, slot.Offset)));
    }

    [Fact]
    public void OpenForeignUnsupportedAndCancelledGraphsCannotPublishPartialLayouts()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Box<T>(val item: T) {}\nstruct Value { var number: int }\nclass Holder(val value: Value) {}");
        var symbol = analysis.Sources.Declarations.Modules["app"].Declarations.Single(s => s.Name == "Box");
        var open = analysis.Types.Nominal(symbol, analysis.Sources.Types.GetTypeParameters(symbol.Parts[0]));
        Assert.Equal("SOL2400", Assert.Throws<FirNativeLayoutException>(() =>
            new FirNativeLayoutBuilder(analysis, TestContext.Current.CancellationToken).Build([open])).Code);
        var failed = new FirNativeLayoutBuilder(analysis, TestContext.Current.CancellationToken);
        Assert.Equal("SOL2400", Assert.Throws<FirNativeLayoutException>(() => failed.Build([Nominal(analysis, "Holder")])).Code);
        Assert.Throws<InvalidOperationException>(() => failed.Build([]));
        var other = GenericMemberProofTestSource.Analyze("class Other {}");
        Assert.Throws<ArgumentException>(() => new FirNativeLayoutBuilder(analysis, TestContext.Current.CancellationToken)
            .Build([Nominal(other, "Other")]));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new FirNativeLayoutBuilder(analysis, cancelled.Token).Build([]));
    }

    private static ResolvedType Nominal(BodyAnalysis analysis, string name, ResolvedType? argument = null, string module = "app")
        => analysis.Types.Nominal(analysis.Sources.Declarations.Modules[module].Declarations.Single(symbol => symbol.Name == name),
            argument is null ? [] : [argument]);

    [Fact]
    public void HashCollisionsAreExplicitFailures()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class A {}\nclass B {}");
        var graph = new FirNativeLayoutBuilder(analysis, TestContext.Current.CancellationToken, identityHash: _ => 1UL << 63);
        Assert.Equal("SOL2402", Assert.Throws<FirNativeLayoutException>(() =>
            graph.Build([Nominal(analysis, "A"), Nominal(analysis, "B")])).Code);
        Assert.Throws<InvalidOperationException>(() => graph.Build([]));
    }

    [Fact]
    public void ConstructedTypeLimitIncludesImplicitBaseDescriptors()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var analysis = GenericMemberProofTestSource.Analyze("class Pair<T, U> {}");
        var integer = analysis.Types.GetPrimitive("int");
        var arguments = Enumerable.Range(0, 64).Select(count => analysis.Types.GetFunction(
            Enumerable.Repeat(integer, count), integer, Enumerable.Repeat(AstPassingMode.Value, count))).ToArray();
        var definition = analysis.Sources.Declarations.Modules["app"].Declarations.Single(symbol => symbol.Name == "Pair");
        var owners = arguments.SelectMany(left => arguments.Select(right => analysis.Types.Nominal(definition, [left, right]))).ToArray();
        Assert.Equal(4096, new FirNativeLayoutBuilder(analysis, cancellation).Build(owners.Take(4095)).Length);
        var failed = new FirNativeLayoutBuilder(analysis, cancellation);
        Assert.Equal("SOL2403", Assert.Throws<FirNativeLayoutException>(() => failed.Build(owners)).Code);
        Assert.Throws<InvalidOperationException>(() => failed.Build([]));
    }

    private static BodyAnalysis Analyze(params string[] sources)
    {
        var session = SourceAnalysis.Create(AstTestSources.Session(sources), TestContext.Current.CancellationToken);
        var result = BodyAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(result.HasErrors, string.Join('\n', result.Diagnostics.Select(d => d.Message)));
        return result;
    }
}
