// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LocalFunctionReceiverCaptureTests
{
    private static LexicalAnalysis Bind(string source) => LexicalAnalysis.Create(
        AstTestSources.Session("module app\n" + source), TestContext.Current.CancellationToken);

    private static AstFunction Function(LexicalAnalysis analysis, string name) => Assert.Single(
        analysis.Declarations.Files.SelectMany(file => file.Nodes).OfType<AstFunction>(),
        function => function.Name == name);

    [Fact]
    public void ExplicitThisAndImplicitConstructorPropertyUseOneReceiverIdentity()
    {
        var analysis = Bind("class Box(val value: int) { func outer(): int { "
            + "func nested(): int = this.value + value; nested() } }");
        var capture = Assert.Single(analysis.GetLocalFunctionReceiverCaptures(Function(analysis, "nested")));

        Assert.Equal("Box", capture.Owner.Name);
        Assert.Equal(LocalFunctionReceiverAccess.ExplicitThis | LocalFunctionReceiverAccess.ImplicitMember,
            capture.Access);
        Assert.Equal(2, capture.References.Length);
        Assert.Empty(capture.Members);
        Assert.True(capture.IsComplete);
    }

    [Fact]
    public void ImplicitTypeMemberRetainsResolvedDeclarationIdentity()
    {
        var analysis = Bind("class Box { val value: int = 1; func outer(): int { "
            + "func nested(): int = value; nested() } }");
        var capture = Assert.Single(analysis.GetLocalFunctionReceiverCaptures(Function(analysis, "nested")));
        var member = Assert.Single(capture.Members);

        Assert.Equal(LocalFunctionReceiverAccess.ImplicitMember, capture.Access);
        Assert.Equal(SymbolKind.Property, member.Kind);
        Assert.Equal("value", member.Name);
        Assert.True(capture.IsComplete);
    }

    [Fact]
    public void InheritedMemberLookupPreservesTheReceiverOwnerAndMember()
    {
        var analysis = Bind("open class Base { open val value: int = 1 }\n"
            + "class Child: Base { func outer(): int { func nested(): int = value; nested() } }");
        var capture = Assert.Single(analysis.GetLocalFunctionReceiverCaptures(Function(analysis, "nested")));

        Assert.Equal("Child", capture.Owner.Name);
        Assert.True(capture.IsComplete);
        Assert.Contains(capture.Members, member => member.Name == "value");
    }

    [Fact]
    public void ExplicitSuperUseIsRecordedOnTheLocalFunctionReceiver()
    {
        var analysis = Bind("open class Base { open func read(): int = 1 }\n"
            + "class Child: Base { func outer(): int { func nested(): int = super.read(); nested() } }");
        var capture = Assert.Single(analysis.GetLocalFunctionReceiverCaptures(Function(analysis, "nested")));

        Assert.Equal("Child", capture.Owner.Name);
        Assert.Equal(LocalFunctionReceiverAccess.ExplicitSuper, capture.Access);
        Assert.Single(capture.References.Where(reference => reference.Spelling == "super"));
    }

    [Fact]
    public void ReceiverRequirementFlowsThroughEveryNestedLocalFunctionBoundary()
    {
        var analysis = Bind("class Box(val value: int) { func outer(): int { "
            + "func middle(): int { func inner(): int = value; inner() }; middle() } }");
        var outer = Function(analysis, "outer");
        var middle = Function(analysis, "middle");
        var inner = Function(analysis, "inner");
        var captures = analysis.LocalFunctionReceiverCaptures;

        Assert.Equal(new[] { "inner", "middle" }, captures.Select(capture => capture.Function.Name)
            .OrderBy(name => name, System.StringComparer.Ordinal));
        Assert.DoesNotContain(captures, capture => ReferenceEquals(capture.Function, outer));
        Assert.All(captures, capture =>
        {
            Assert.Equal("Box", capture.Owner.Name);
            Assert.Equal(LocalFunctionReceiverAccess.ImplicitMember, capture.Access);
            Assert.Single(capture.References);
        });
        Assert.Same(captures[0].References[0], captures[1].References[0]);
        Assert.Empty(analysis.GetLocalFunctionReceiverCaptures(outer));
    }

    [Fact]
    public void CapturedLocalShadowingMemberDoesNotCreateReceiverRequirement()
    {
        var analysis = Bind("class Box { val value: int = 1; func outer(value: int): int { "
            + "func nested(): int = value; nested() } }");
        var function = Function(analysis, "nested");

        Assert.Empty(analysis.GetLocalFunctionReceiverCaptures(function));
        Assert.Equal("value", Assert.Single(analysis.GetLocalFunctionCaptures(function)).Local.Name);
    }

    [Fact]
    public void NestedTypeUsesItsNearestReceiverOwner()
    {
        var analysis = Bind("class Outer { val outerValue: int = 1; class Inner { "
            + "val innerValue: int = 2; func outer(): int { func nested(): int = innerValue; nested() } } }");
        var capture = Assert.Single(analysis.GetLocalFunctionReceiverCaptures(Function(analysis, "nested")));

        Assert.Equal("Inner", capture.Owner.Name);
        Assert.Contains(capture.Members, member => member.Name == "innerValue");
        Assert.DoesNotContain(capture.Members, member => member.Name == "outerValue");
    }

    [Fact]
    public void ReceiverCaptureQueryRejectsFunctionsFromAnotherSnapshot()
    {
        var first = Bind("class Box { val value: int = 1; func outer() { func nested(): int = value } }");
        var second = Bind("class Box { val value: int = 1; func outer() { func nested(): int = value } }");
        var foreign = Function(second, "nested");
        var owned = Function(first, "nested");

        Assert.Throws<System.ArgumentException>(() => first.GetLocalFunctionReceiverCaptures(foreign));
        Assert.Throws<System.ArgumentNullException>(() => first.GetLocalFunctionReceiverCaptures(null!));
        Assert.Single(first.GetLocalFunctionReceiverCaptures(owned));
    }
}
