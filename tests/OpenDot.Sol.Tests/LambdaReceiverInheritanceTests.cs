// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaReceiverInheritanceTests
{
    private static LexicalAnalysis Analyze(params string[] sources)
        => LexicalAnalysis.Create(AstTestSources.Session(sources), TestContext.Current.CancellationToken);

    [Fact]
    public void InheritedPropertyAndMethodReferencesShareTheDerivedReceiverCapture()
    {
        var analysis = Analyze("module app\nopen class Base { val value: int = 1; open func read(): int = value }\n"
            + "class Child: Base { func callback(): () -> int = { value + read() } }");
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);

        Assert.Equal("Child", capture.Owner.Name);
        Assert.Equal(LambdaReceiverAccess.ImplicitMember, capture.Access);
        Assert.Equal(new[] { "read", "value" }, capture.Members.Select(member => member.Name).OrderBy(name => name));
        Assert.Equal(SymbolKind.Function, capture.Members.Single(member => member.Name == "read").Kind);
        Assert.Equal(SymbolKind.Property, capture.Members.Single(member => member.Name == "value").Kind);
        Assert.Equal(2, capture.References.Length);
        Assert.Equal(capture.References.Select(reference => reference.Origin.Span.Start).OrderBy(offset => offset),
            capture.References.Select(reference => reference.Origin.Span.Start));
    }

    [Fact]
    public void GenericBasePropertyKeepsItsDeclarationIdentityForReceiverCapture()
    {
        var session = AstTestSources.Session("module app\nopen class Base<T> { val value: T; constructor(value: T) { this.value = value } }\n"
            + "class Child: Base<int> { constructor(value: int): super(value) {}; func callback(): () -> int = { value } }");
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join("\n", sources.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var bodies = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var analysis = sources.Lexical;
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);
        var member = Assert.Single(capture.Members);
        var reference = Assert.Single(capture.References);
        var binding = Assert.IsType<ExpressionBinding>(bodies.GetExpression(reference));

        Assert.Equal("Child", capture.Owner.Name);
        Assert.Equal("app.Base`1.value", member.QualifiedName);
        Assert.Equal("value", member.Name);
        Assert.Equal(SymbolKind.Property, member.Kind);
        Assert.Equal("int", binding.Type?.Name);
        Assert.Equal(LambdaReceiverAccess.ImplicitMember, capture.Access);
        Assert.False(bodies.HasErrors, string.Join("\n", bodies.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.True(bodies.IsComplete);
    }

    [Fact]
    public void DirectMemberHidesInheritedSameNameInReceiverCandidateInventory()
    {
        var analysis = Analyze("module app\nopen class Base { val value: int = 1 }\n"
            + "class Child: Base { val value: int = 2; func callback(): () -> int = { value } }");
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);

        var member = Assert.Single(capture.Members);
        Assert.Equal("app.Child`0.value", member.QualifiedName);
        Assert.Equal("value", member.Name);
        Assert.Single(capture.References);
    }

    [Fact]
    public void InheritedInstanceReceiverIsNotConfusedWithQualifiedOtherReceiver()
    {
        var analysis = Analyze("module app\nopen class Base { val value: int = 1 }\n"
            + "class Child: Base { func callback(other: Child): () -> int = { other.value } }");

        Assert.Empty(analysis.LambdaReceiverCaptures);
        Assert.Equal("other", Assert.Single(analysis.LambdaCaptures).Local.Name);
    }

    [Fact]
    public void InheritedMemberReceiverPropagatesAcrossNestedLambdas()
    {
        var analysis = Analyze("module app\nopen class Base { val value: int = 1 }\n"
            + "class Child: Base { func callback(): () -> () -> int = { { value } } }");
        var captures = analysis.LambdaReceiverCaptures.OrderBy(capture => capture.Lambda.Origin.Span.Start).ToArray();

        Assert.Equal(2, captures.Length);
        Assert.All(captures, capture =>
        {
            Assert.Equal("Child", capture.Owner.Name);
            Assert.Equal("value", Assert.Single(capture.Members).Name);
            Assert.Single(capture.References);
        });
    }

    [Fact]
    public void LambdaInsideNestedTypeCapturesNearestTypeReceiver()
    {
        var analysis = Analyze("module app\nclass Outer { val outer: int = 1\n"
            + "class Inner { val inner: int = 2; func callback(): () -> int = { inner } } }");
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);

        Assert.Equal("Inner", capture.Owner.Name);
        Assert.Equal("inner", Assert.Single(capture.Members).Name);
        Assert.DoesNotContain(capture.Members, member => member.Name == "outer");
    }

    [Fact]
    public void ObjectLambdaCapturesTheObjectReceiverAndItsMembers()
    {
        var analysis = Analyze("module app\nobject Counter { val value: int = 1; "
            + "func callback(): () -> int = { value } }");
        var capture = Assert.Single(analysis.LambdaReceiverCaptures);

        Assert.Equal("Counter", capture.Owner.Name);
        Assert.Equal(NodeKind.ObjectDeclaration, capture.Owner.SyntaxKind);
        Assert.Equal("value", Assert.Single(capture.Members).Name);
    }

    [Fact]
    public void ReceiverCaptureOrderingIsStableAcrossRepeatedAnalysisAndFiles()
    {
        var files = new[]
        {
            "module app\nopen class Base { val value: int = 1 }\nclass First: Base { func callback(): () -> int = { value } }",
            "module app\nopen class Base { val value: int = 1 }\nclass Second: Base { func callback(): () -> int = { value } }",
        };
        var first = Analyze(files);
        var second = Analyze(files);
        static string[] Shape(LexicalAnalysis value) => value.LambdaReceiverCaptures.Select(capture =>
            string.Join("|", capture.Lambda.Origin.Source.Path, capture.Lambda.Origin.Span.Start,
                capture.Owner.Name, capture.Access, string.Join(",", capture.References.Select(reference => reference.Origin.Span.Start)),
                string.Join(",", capture.Members.Select(member => member.QualifiedName)))).ToArray();

        Assert.Equal(Shape(first), Shape(second));
        Assert.Equal(new[] { "file0.sol", "file1.sol" },
            first.LambdaReceiverCaptures.Select(capture => capture.Lambda.Origin.Source.Path));
    }
}
