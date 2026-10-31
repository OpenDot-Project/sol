// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionReceiverCaptureTests
{
    private static LexicalAnalysis Analyze(params string[] sources)
        => LexicalAnalysis.Create(AstTestSources.Session(sources), TestContext.Current.CancellationToken);

    [Fact]
    public void ExplicitThisAndConstructorPropertyUseOneReceiverIdentity()
    {
        var analysis = Analyze("module app\nclass Box(val value: int) { val callback = "
            + "func(): int { this.value + value } }");
        var function = Assert.Single(analysis.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());
        var capture = Assert.Single(analysis.GetAnonymousFunctionReceiverCaptures(function));

        Assert.Same(function, capture.Function);
        Assert.Equal("Box", capture.Owner.Name);
        Assert.Equal(AnonymousFunctionReceiverAccess.ExplicitThis | AnonymousFunctionReceiverAccess.ImplicitMember,
            capture.Access);
        Assert.Equal(new[] { "this", "value" }, capture.References.Select(reference => reference.Spelling));
        Assert.Empty(capture.Members);
        Assert.Empty(analysis.GetAnonymousFunctionCaptures(function));
    }

    [Fact]
    public void DirectInstanceMembersRetainResolvedDeclarationIdentity()
    {
        var analysis = Analyze("module app\nclass Box { val value: int = 1; "
            + "val callback = func(): int { value } }");
        var capture = Assert.Single(analysis.AnonymousFunctionReceiverCaptures);
        var member = Assert.Single(capture.Members);

        Assert.Equal(AnonymousFunctionReceiverAccess.ImplicitMember, capture.Access);
        Assert.Equal(SymbolKind.Property, member.Kind);
        Assert.Equal("value", member.Name);
        Assert.True(capture.IsComplete);
    }

    [Fact]
    public void InheritedMemberAndItsReceiverPropagateThroughNestedAnonymousFunctions()
    {
        var analysis = Analyze("module app\nopen class Base { open func read(): int = 1 }\n"
            + "class Child: Base { val callback = func(): int { val nested = func(): int { read() }; nested() } }");
        var functions = analysis.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>().OrderBy(function => function.Origin.Span.Start).ToArray();
        var captures = analysis.AnonymousFunctionReceiverCaptures.ToArray();

        Assert.Equal(2, functions.Length);
        Assert.Equal(2, captures.Length);
        Assert.Equal(functions, captures.Select(capture => capture.Function));
        Assert.All(captures, capture =>
        {
            Assert.Equal("Child", capture.Owner.Name);
            Assert.Equal(AnonymousFunctionReceiverAccess.ImplicitMember, capture.Access);
            Assert.Equal("read", Assert.Single(capture.Members).Name);
            Assert.True(capture.IsComplete);
        });
    }

    [Fact]
    public void ExplicitSuperReferenceBelongsToNearestNominalReceiver()
    {
        var analysis = Analyze("module app\nopen class Base { open func read(): int = 1 }\n"
            + "class Child: Base { val callback = func(): int { super.read() } }");
        var capture = Assert.Single(analysis.AnonymousFunctionReceiverCaptures);

        Assert.Equal("Child", capture.Owner.Name);
        Assert.Equal(AnonymousFunctionReceiverAccess.ExplicitSuper, capture.Access);
        Assert.Equal("super", Assert.Single(capture.References).Spelling);
    }

    [Fact]
    public void AccessorBackingFieldRequiresItsPropertyReceiver()
    {
        var analysis = Analyze("module app\nclass Box { val value: int { get = "
            + "func(): int { field } } }");
        var capture = Assert.Single(analysis.AnonymousFunctionReceiverCaptures);

        Assert.Equal("Box", capture.Owner.Name);
        Assert.Equal(AnonymousFunctionReceiverAccess.ImplicitMember, capture.Access);
        Assert.Equal("field", Assert.Single(capture.References).Spelling);
        Assert.Empty(capture.Members);
    }

    [Fact]
    public void AnonymousParameterShadowingInstanceMemberDoesNotCaptureReceiver()
    {
        var analysis = Analyze("module app\nclass Box { val value: int = 1; "
            + "val callback = func(value: int): int { value } }");
        var function = Assert.Single(analysis.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());

        Assert.Empty(analysis.GetAnonymousFunctionReceiverCaptures(function));
        Assert.Empty(analysis.GetAnonymousFunctionCaptures(function));
        Assert.Empty(analysis.AnonymousFunctionReceiverCaptures);
    }

    [Fact]
    public void QualifiedMemberOnAnotherObjectDoesNotCaptureTheEnclosingReceiver()
    {
        var analysis = Analyze("module app\nclass Box { val value: int = 1; "
            + "val callback = func(other: Box): int { other.value } }");

        Assert.Empty(analysis.AnonymousFunctionReceiverCaptures);
        Assert.Empty(analysis.AnonymousFunctionCaptures);
    }

    [Fact]
    public void NestedTypeUsesItsNearestReceiverAndStableQuery()
    {
        var analysis = Analyze("module app\nclass Outer { val outer: int = 1; class Inner { "
            + "val inner: int = 2; val callback = func(): int { inner } } }");
        var function = Assert.Single(analysis.Declarations.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());
        var capture = Assert.Single(analysis.GetAnonymousFunctionReceiverCaptures(function));

        Assert.Equal("Inner", capture.Owner.Name);
        Assert.Equal("inner", Assert.Single(capture.Members).Name);
        Assert.Equal(analysis.GetAnonymousFunctionReceiverCaptures(function),
            analysis.GetAnonymousFunctionReceiverCaptures(function));
    }

    [Fact]
    public void InheritedLookupBeyondBudgetKeepsAnIncompleteReceiverInventory()
    {
        var sources = new List<string> { "module app", "open class C0 { val far: int = 1 }" };
        for (int index = 1; index <= 132; index++) { sources.Add($"open class C{index}: C{index - 1} {{ }}"); }
        sources.Add("class Leaf: C132 { val callback = func(): int { far } }");

        var analysis = Analyze(System.String.Join("\n", sources));
        var capture = Assert.Single(analysis.AnonymousFunctionReceiverCaptures);

        Assert.Equal("Leaf", capture.Owner.Name);
        Assert.Equal(AnonymousFunctionReceiverAccess.ImplicitMember, capture.Access);
        Assert.False(capture.IsComplete);
        Assert.Empty(capture.Members);
        Assert.Equal("far", Assert.Single(capture.References).Spelling);
    }

    [Fact]
    public void ReceiverQueryRejectsAnonymousFunctionsFromAnotherSnapshot()
    {
        var firstSession = AstTestSources.Session("module app\nclass Box { val value: int = 1; "
            + "val callback = func(): int { value } }");
        var secondSession = AstTestSources.Session("module app\nclass Box { val value: int = 1; "
            + "val callback = func(): int { value } }");
        var first = LexicalAnalysis.Create(firstSession, TestContext.Current.CancellationToken);
        var second = LexicalAnalysis.Create(secondSession, TestContext.Current.CancellationToken);
        var owned = Assert.Single(firstSession.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());
        var foreign = Assert.Single(secondSession.Files.SelectMany(file => file.Root.Descendants())
            .OfType<AstAnonymousFunction>());

        Assert.Single(first.GetAnonymousFunctionReceiverCaptures(owned));
        Assert.Throws<System.ArgumentException>(() => first.GetAnonymousFunctionReceiverCaptures(foreign));
        Assert.Throws<System.ArgumentNullException>(() => first.GetAnonymousFunctionReceiverCaptures(null!));
    }
}
