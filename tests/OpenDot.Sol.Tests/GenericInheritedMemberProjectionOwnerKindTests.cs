// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericInheritedMemberProjectionOwnerKindTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}";

    public static TheoryData<string, string, string> KindMatrix => new()
    {
        { "open class Root<T>", "class Leaf<T>", "class" },
        { "interface Root<T>", "class Leaf<T>", "class" },
        { "interface Root<T>", "struct Leaf<T>", "struct" },
        { "interface Root<T>", "ref struct Leaf<T>", "ref struct" },
        { "interface Root<T>", "interface Leaf<T>", "interface" },
    };

    [Theory]
    [MemberData(nameof(KindMatrix))]
    public void ProjectsAcrossEverySupportedNominalReceiverDeclarationKind(
        string rootDeclaration, string leafDeclaration, string receiverKind)
    {
        var analysis = Analyze("module app\n"
            + rootDeclaration + " { func echo(value: T): T = value }\n"
            + leafDeclaration + ": Root<T> {}");
        var root = Type(analysis, "Root");
        var leaf = Type(analysis, "Leaf");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var text = Nominal(analysis, "String");
        var receiver = analysis.Types.Nominal(analysis.Sources.Declarations.GetDeclaredSymbol(leaf)!, [text]);

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        Assert.Equal(receiverKind, leaf.SyntaxKind switch
        {
            NodeKind.ClassDeclaration => "class",
            NodeKind.StructDeclaration => "struct",
            NodeKind.RefStructDeclaration => "ref struct",
            NodeKind.InterfaceDeclaration => "interface",
            _ => "unknown",
        });
        var candidate = Assert.Single(result.Projection.Candidates);
        Assert.Equal(text, Assert.Single(candidate.ConstructedOwner.Arguments));
        Assert.Equal(text, result.Body!.Summary.ReturnType);
        Assert.Equal(BodyBindingStatus.Bound, result.Body.Summary.Status);
        Assert.Same(receiver, candidate.Paths[0][0]);
        Assert.Same(candidate.ConstructedOwner, candidate.Paths[0][^1]);
    }

    [Fact]
    public void ProjectsAConcreteGenericClassOwnerThroughItsClassBaseChain()
    {
        var analysis = Analyze("module app\n"
            + "open class Root<T> { func echo(value: T): T = value }\n"
            + "open class Middle<U>: Root<[]U> {}\n"
            + "class Leaf<V>: Middle<V> {}");
        var method = Assert.Single(Type(analysis, "Root").Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        Assert.Equal(analysis.Types.GetArray(Nominal(analysis, "String")),
            Assert.Single(candidate.ConstructedOwner.Arguments));
        Assert.Equal("app.Root", candidate.ConstructedOwner.Name);
        Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
    }

    [Theory]
    [InlineData("open class Root<T>")]
    [InlineData("struct Root<T>")]
    [InlineData("ref struct Root<T>")]
    [InlineData("interface Root<T>")]
    public void DirectOwnerProjectionSupportsEveryGenericOwnerKind(string declaration)
    {
        var analysis = Analyze("module app\n"
            + declaration + " { func echo(value: T): T = value }");
        var root = Type(analysis, "Root");
        var method = Assert.Single(root.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Root", Nominal(analysis, "String"));

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        Assert.Single(candidate.Paths);
        Assert.Single(candidate.Paths[0]);
        Assert.Same(receiver, candidate.ConstructedOwner);
        Assert.Equal(receiver.Declaration, candidate.ConstructedOwner.Declaration);
        Assert.Equal("std.core.String", result.Body!.Summary.ReturnType!.Name);
        Assert.Equal(BodyBindingStatus.Bound, result.Body.Summary.Status);
    }

    [Fact]
    public void MultipleReceiverDeclarationsCanReuseOneCanonicalOwnerAndDifferentPathRoots()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "interface Left<T>: Root<T> {}\n"
            + "class ClassLeaf<T>: Left<T> {}\n"
            + "struct StructLeaf<T>: Left<T> {}\n"
            + "ref struct RefLeaf<T>: Left<T> {}");
        var method = Assert.Single(Type(analysis, "Root").Members.OfType<AstFunction>());
        var text = Nominal(analysis, "String");
        var names = new[] { "ClassLeaf", "StructLeaf", "RefLeaf" };
        var results = names.Select(name =>
        {
            var receiver = Constructed(analysis, name, text);
            return (Receiver: receiver, Result: analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
                TestContext.Current.CancellationToken));
        }).ToArray();

        Assert.All(results, item =>
        {
            Assert.Equal(OwnerProjectionStatus.Unique, item.Result.Projection.Status);
            var candidate = Assert.Single(item.Result.Projection.Candidates);
            Assert.Same(item.Receiver, candidate.Paths[0][0]);
            Assert.Equal(text, Assert.Single(candidate.ConstructedOwner.Arguments));
            Assert.Equal(BodyBindingStatus.Bound, item.Result.Body!.Summary.Status);
        });
        var canonicalOwner = Assert.Single(results[0].Result.Projection.Candidates).ConstructedOwner;
        Assert.All(results, item => Assert.Same(canonicalOwner,
            Assert.Single(item.Result.Projection.Candidates).ConstructedOwner));
        Assert.All(results, item => Assert.Same(results[0].Result.Body, item.Result.Body));
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session(source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static AstTypeDeclaration Type(BodyAnalysis analysis, string name) =>
        analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>().Single(type => type.Name == name);

    private static ResolvedType Nominal(BodyAnalysis analysis, string name) => analysis.Types.Nominal(
        analysis.Sources.Declarations.GetDeclaredSymbol(Type(analysis, name))!, []);

    private static ResolvedType Constructed(BodyAnalysis analysis, string name, params ResolvedType[] arguments) =>
        analysis.Types.Nominal(analysis.Sources.Declarations.GetDeclaredSymbol(Type(analysis, name))!,
            arguments.ToImmutableArray());
}
