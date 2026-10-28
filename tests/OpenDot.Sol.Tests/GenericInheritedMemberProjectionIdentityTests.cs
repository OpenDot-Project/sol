// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericInheritedMemberProjectionIdentityTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}";

    [Fact]
    public void SameShortNameOwnersInDifferentModulesRemainSeparateProjectionTargets()
    {
        var session = AstTestSources.Session(
            "module left\ninterface Root<T> { func echo(value: T): T = value }",
            "module right\ninterface Root<T> { func echo(value: T): T = value }",
            "module app\nimport left\nimport right\n"
                + "interface LeftPath<T>: left.Root<T> {}\n"
                + "interface RightPath<T>: right.Root<T> {}\n"
                + "class Leaf: LeftPath<String>, RightPath<String> {}",
            Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var rootDeclarations = sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>().Where(type => type.Name == "Root").ToArray();
        Assert.Equal(2, rootDeclarations.Length);
        var leftRoot = rootDeclarations.Single(type => sources.Declarations.GetDeclaredSymbol(type)!.ModuleName == "left");
        var rightRoot = rootDeclarations.Single(type => sources.Declarations.GetDeclaredSymbol(type)!.ModuleName == "right");
        var leftMethod = Assert.Single(leftRoot.Members.OfType<AstFunction>());
        var rightMethod = Assert.Single(rightRoot.Members.OfType<AstFunction>());
        var receiver = Constructed(analysis, "Leaf");

        var leftResult = analysis.GetGenericMemberBodyForReceiver(leftMethod, receiver, [],
            TestContext.Current.CancellationToken);
        var rightResult = analysis.GetGenericMemberBodyForReceiver(rightMethod, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, leftResult.Projection.Status);
        Assert.Equal(OwnerProjectionStatus.Unique, rightResult.Projection.Status);
        var leftOwner = Assert.Single(leftResult.Projection.Candidates).ConstructedOwner;
        var rightOwner = Assert.Single(rightResult.Projection.Candidates).ConstructedOwner;
        Assert.Equal("left.Root", leftOwner.Name);
        Assert.Equal("right.Root", rightOwner.Name);
        Assert.NotSame(leftOwner.Declaration, rightOwner.Declaration);
        Assert.Equal(3, leftResult.Projection.Candidates[0].Paths[0].Length);
        Assert.Equal(3, rightResult.Projection.Candidates[0].Paths[0].Length);
        Assert.Equal("std.core.String", Assert.Single(leftOwner.Arguments).Name);
        Assert.Equal("std.core.String", Assert.Single(rightOwner.Arguments).Name);
        Assert.Equal(BodyBindingStatus.Bound, leftResult.Body!.Summary.Status);
        Assert.Equal(BodyBindingStatus.Bound, rightResult.Body!.Summary.Status);
    }

    [Fact]
    public void OneCanonicalOwnerReachedByDifferentGenericEdgeExpressionsSharesIdentity()
    {
        var analysis = Analyze("module app\n"
            + "class Box<T> {}\n"
            + "interface Root<A, B> { func echo(value: A): A = value }\n"
            + "interface Left<T, U>: Root<Box<T>, U> {}\n"
            + "interface Right<V, W>: Root<Box<V>, W> {}\n"
            + "class Leaf: Left<String, int>, Right<String, int> {}");
        var method = Method(analysis, "Root", "echo");
        var receiver = Constructed(analysis, "Leaf");

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        Assert.Equal(2, candidate.Paths.Length);
        Assert.Same(candidate.Paths[0][^1], candidate.Paths[1][^1]);
        Assert.Same(candidate.ConstructedOwner, candidate.Paths[0][^1]);
        Assert.Same(candidate.ConstructedOwner, candidate.Paths[1][^1]);
        var box = Type(analysis, "Box");
        var expected = analysis.Types.Nominal(
            analysis.Sources.Declarations.GetDeclaredSymbol(box)!, [Nominal(analysis, "String")]);
        Assert.Equal(expected, candidate.ConstructedOwner.Arguments[0]);
        Assert.Equal(analysis.Types.GetPrimitive("int"), candidate.ConstructedOwner.Arguments[1]);
        Assert.Equal(expected, result.Body!.Summary.ReturnType);
    }

    [Fact]
    public void SameGenericDeclarationWithDifferentArgumentsIsNotCollapsedAcrossRoutes()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "interface First: Root<String> {}\n"
            + "interface Second: Root<int> {}\n"
            + "interface Third: Root<String> {}\n"
            + "class Leaf: First, Second, Third {}", allowSourceErrors: true);
        var method = Method(analysis, "Root", "echo");
        var receiver = Constructed(analysis, "Leaf");

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Ambiguous, result.Projection.Status);
        Assert.Null(result.Body);
        Assert.Equal(2, result.Projection.Candidates.Length);
        Assert.All(result.Projection.Candidates, candidate =>
            Assert.Same(analysis.Sources.Declarations.GetDeclaredSymbol(Type(analysis, "Root")),
                candidate.ConstructedOwner.Declaration));
        var stringOwner = Assert.Single(result.Projection.Candidates,
            candidate => Assert.Single(candidate.ConstructedOwner.Arguments).Name == "std.core.String");
        var integerOwner = Assert.Single(result.Projection.Candidates,
            candidate => Assert.Single(candidate.ConstructedOwner.Arguments).Name == "int");
        Assert.Equal(2, stringOwner.Paths.Length);
        Assert.Single(integerOwner.Paths);
    }

    private static BodyAnalysis Analyze(string source, bool allowSourceErrors = false)
    {
        var session = AstTestSources.Session(source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        if (!allowSourceErrors)
        {
            Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics
                .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        }
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static AstTypeDeclaration Type(BodyAnalysis analysis, string name) =>
        analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>().Single(type => type.Name == name);

    private static AstFunction Method(BodyAnalysis analysis, string owner, string name) =>
        Type(analysis, owner).Members.OfType<AstFunction>().Single(method => method.Name == name);

    private static ResolvedType Nominal(BodyAnalysis analysis, string name) => analysis.Types.Nominal(
        analysis.Sources.Declarations.GetDeclaredSymbol(Type(analysis, name))!, []);

    private static ResolvedType Constructed(BodyAnalysis analysis, string name, params ResolvedType[] arguments) =>
        analysis.Types.Nominal(analysis.Sources.Declarations.GetDeclaredSymbol(Type(analysis, name))!,
            arguments.ToImmutableArray());
}
