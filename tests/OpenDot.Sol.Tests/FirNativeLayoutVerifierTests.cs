// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirNativeLayoutVerifierTests
{
    [Fact]
    public void CompleteGraphVerifiesWithoutRebuildingAndForgedFieldContractsFail()
    {
        var (analysis, graph, owner) = Graph();
        new FirNativeLayoutVerifier(analysis, TestContext.Current.CancellationToken).Verify(graph);
        var field = owner.Storage[^1];
        FirNativeStorage[] invalid =
        [
            field with { Offset = field.Offset + 8 }, field with { Size = 4 },
            field with { Alignment = 4 }, field with { IsGcReference = false },
            field with { UsesInterfaceCell = false }, field with { Identity = "forged" },
            field with { ValueType = analysis.Types.GetPrimitive("long") },
            field with { Source = field.Source with { Name = "different" } },
        ];
        foreach (var forged in invalid)
        {
            Reject(analysis, graph.Replace(owner, owner with { Storage = owner.Storage.SetItem(owner.Storage.Length - 1, forged) }));
        }
        Reject(analysis, graph.Replace(owner, owner with { Storage = owner.Storage.RemoveAt(owner.Storage.Length - 1) }));
        Reject(analysis, graph.Replace(owner, owner with { Storage = owner.Storage.Reverse().ToImmutableArray() }));
    }

    [Fact]
    public void ForgedDescriptorPrefixRootsIdentityAndIncompleteGraphsFail()
    {
        var (analysis, graph, owner) = Graph();
        FirNativeTypeLayout[] invalid =
        [
            owner with { Size = owner.Size + 8 }, owner with { Alignment = 8 },
            owner with { Base = null }, owner with { TypeId = 123 }, owner with { Identity = "forged" },
            owner with { IsRuntimeOwned = true }, owner with { IsInterface = true },
            owner with { ReferenceOffsets = [] }, owner with { ReferenceOffsets = [16, 24] },
            owner with { Storage = default }, owner with { ReferenceOffsets = default },
        ];
        foreach (var forged in invalid) { Reject(analysis, graph.Replace(owner, forged)); }
        Reject(analysis, graph.Add(owner));
        Reject(analysis, graph.Where(layout => layout.Type.Declaration?.Name != "I").ToImmutableArray());
        Reject(analysis, graph.Where(layout => layout.Type != owner.Base).ToImmutableArray());
        Reject(analysis, default);
        var prefix = graph.Single(layout => layout.Type == owner.Base);
        Reject(analysis, graph.Replace(prefix, prefix with { Size = prefix.Size + 8 }));
    }

    [Fact]
    public void RuntimeDescriptorRootsAndWorkspaceOwnershipAreNotCallerAssertions()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Failure: RuntimeException {}");
        var type = analysis.Types.Nominal(analysis.Sources.Declarations.Modules["app"].Declarations.Single(), []);
        var graph = new FirNativeLayoutBuilder(analysis, TestContext.Current.CancellationToken).Build([type]);
        new FirNativeLayoutVerifier(analysis, TestContext.Current.CancellationToken).Verify(graph);
        var exception = graph.Single(layout => layout.Type.Declaration?.Name == "RuntimeException");
        Reject(analysis, graph.Replace(exception, exception with { ReferenceOffsets = [16, 24] }));
        var other = GenericMemberProofTestSource.Analyze("class Other {}");
        Assert.Throws<ArgumentException>(() => new FirNativeLayoutVerifier(other, TestContext.Current.CancellationToken).Verify(graph));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new FirNativeLayoutVerifier(analysis, cancelled.Token).Verify(graph));
        var once = new FirNativeLayoutVerifier(analysis, TestContext.Current.CancellationToken);
        once.Verify(graph);
        Assert.Throws<InvalidOperationException>(() => once.Verify(graph));
    }

    [Fact]
    public void PublicFirVerificationDoesNotMintACapabilityForForgedInternalLayouts()
    {
        var (analysis, graph, owner) = Graph();
        var program = new FirProgram(analysis, RuntimeCatalog.Create(), [], -1)
        { IsLibrary = true, NativeLayouts = graph };
        Assert.NotNull(FirVerifier.Verify(program, TestContext.Current.CancellationToken).Program);
        var result = FirVerifier.Verify(program with
        { NativeLayouts = graph.Replace(owner, owner with { ReferenceOffsets = [] }) }, TestContext.Current.CancellationToken);
        Assert.Null(result.Program);
        Assert.Equal("SOL2402", Assert.Single(result.Diagnostics).Code);
    }

    private static (BodyAnalysis Analysis, ImmutableArray<FirNativeTypeLayout> Graph, FirNativeTypeLayout Owner) Graph()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface I { func read(): int }\n"
            + "open class B(val count: int) {}\nclass C(val value: I?): B(1) {}");
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Message)));
        var owner = analysis.Types.Nominal(analysis.Sources.Declarations.Modules["app"].Declarations.Single(s => s.Name == "C"), []);
        var graph = new FirNativeLayoutBuilder(analysis, TestContext.Current.CancellationToken).Build([owner]);
        return (analysis, graph, graph.Single(layout => layout.Type == owner));
    }

    private static void Reject(BodyAnalysis analysis, ImmutableArray<FirNativeTypeLayout> graph)
        => Assert.Equal("SOL2402", Assert.Throws<FirNativeLayoutException>(() =>
            new FirNativeLayoutVerifier(analysis, TestContext.Current.CancellationToken).Verify(graph)).Code);
}
