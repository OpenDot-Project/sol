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

public sealed class ConstructorBodyAnalysisTests
{
    [Fact]
    public void ClosedConstructionViewsRetainBindingsWithoutChangingOrdinaryBodies()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Box<T>(input: T) { val item: T = input }");
        var definition = analysis.Sources.Declarations.Modules["app"].Declarations.Single(symbol => symbol.Name == "Box");
        var declaration = (AstTypeDeclaration)definition.Parts[0];
        var initializer = (AstExpression)declaration.Members.OfType<AstProperty>().Single().Initializer!;
        var ordinary = analysis.GetExpression(initializer);
        var integer = analysis.Types.GetPrimitive("int"); var boolean = analysis.Types.GetPrimitive("bool");
        var intOwner = analysis.Types.Nominal(definition, [integer]);
        var boolOwner = analysis.Types.Nominal(definition, [boolean]);
        var first = analysis.GetConstructorBody(intOwner, declaration.PrimaryConstructor, TestContext.Current.CancellationToken);
        var second = analysis.GetConstructorBody(boolOwner, declaration.PrimaryConstructor, TestContext.Current.CancellationToken);
        Assert.True(first.IsComplete, string.Join('\n', first.Diagnostics.Select(d => d.Message)));
        Assert.True(second.IsComplete);
        Assert.Same(integer, first.Expressions[initializer].Type);
        Assert.Same(boolean, second.Expressions[initializer].Type);
        Assert.Same(ordinary, analysis.GetExpression(initializer));
        Assert.Equal(ResolvedTypeKind.Parameter, ordinary!.Type!.Kind);
        Assert.Same(first, analysis.GetConstructorBody(intOwner, declaration.PrimaryConstructor, TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => analysis.GetConstructorBody(intOwner, declaration.PrimaryConstructor, cancelled.Token));
        Assert.Throws<ArgumentException>(() => analysis.GetConstructorBody(intOwner, null, TestContext.Current.CancellationToken));
        var foreign = GenericMemberProofTestSource.Analyze("class Other {}");
        var other = foreign.Types.Nominal(foreign.Sources.Declarations.Modules["app"].Declarations.Single(), []);
        Assert.Throws<ArgumentException>(() => analysis.GetConstructorBody(other, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ImplicitConstructionUsesItsRealOwnerOriginAndCannotCacheFailure()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Ready { val item: int = 1 }\n"
            + "class Broken { val item: int; constructor() { this.read(); this.item = 1 }; private func read(): int = this.item }");
        var ready = analysis.Types.Nominal(analysis.Sources.Declarations.Modules["app"].Declarations.Single(symbol => symbol.Name == "Ready"), []);
        int before = analysis.Sources.Declarations.Files.Sum(file => file.Nodes.Length);
        var implicitBody = analysis.GetConstructorBody(ready, null, TestContext.Current.CancellationToken);
        Assert.True(implicitBody.IsComplete);
        Assert.Null(implicitBody.Proof.Selection.Constructor);
        Assert.Same(ready.Declaration!.Parts[0], implicitBody.Proof.Selection.Site);
        Assert.Equal(before, analysis.Sources.Declarations.Files.Sum(file => file.Nodes.Length));
        var broken = analysis.Types.Nominal(analysis.Sources.Declarations.Modules["app"].Declarations.Single(symbol => symbol.Name == "Broken"), []);
        var constructor = broken.Declaration!.Members.SelectMany(symbol => symbol.Parts).OfType<AstConstructor>().Single();
        var first = analysis.GetConstructorBody(broken, constructor, TestContext.Current.CancellationToken);
        Assert.False(first.IsComplete); Assert.Equal(BodyBindingStatus.Invalid, first.Proof.Status);
        Assert.NotSame(first, analysis.GetConstructorBody(broken, constructor, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ProviderSelectionCarriesItsActualStorageTypeIntoNativeLayout()
    {
        var source = SourceAnalysis.Create(AstTestSources.Session("module app; import std.reflect;\n"
            + "class Target { operator func getValue(thisRef: Holder, property: SolProperty<int>): int = 1 }\n"
            + "class Factory { operator func provideDelegate(thisRef: Holder, property: SolProperty<int>): Target = Target() }\n"
            + "class Holder { val item: int by Factory() }", GenericMemberProofTestSource.Core,
            "module std.reflect; class SolProperty<T> {}"), TestContext.Current.CancellationToken);
        var analysis = BodyAnalysis.Create(source, TestContext.Current.CancellationToken);
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var owner = analysis.Types.Nominal(source.Declarations.Modules["app"].Declarations.Single(symbol => symbol.Name == "Holder"), []);
        var body = analysis.GetConstructorBody(owner, null, TestContext.Current.CancellationToken);
        Assert.True(body.IsComplete);
        var contract = Assert.Single(body.Delegates);
        Assert.Equal("Target", contract.StoredType.Declaration!.Name);
        Assert.Same(owner, contract.Owner); Assert.True(contract.HasProvider);
        Assert.Equal("Target", contract.Provider!.Value.Type.Declaration!.Name);
        var graph = new FirNativeLayoutBuilder(analysis, TestContext.Current.CancellationToken,
            slot => body.Delegates.Single(binding => binding.Property == slot.Declaration && binding.Owner == slot.Owner).StoredType).Build([owner]);
        var field = Assert.Single(graph.Single(layout => layout.Type == owner).Storage);
        Assert.Same(contract.StoredType, field.ValueType); Assert.True(field.IsGcReference);
    }
}
