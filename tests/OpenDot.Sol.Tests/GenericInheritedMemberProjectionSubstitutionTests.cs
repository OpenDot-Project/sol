// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericInheritedMemberProjectionSubstitutionTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}";

    [Fact]
    public void ComposedAndDirectEdgeExpressionsCollapseWhenTheyProduceTheSameOwnerVector()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<A, B> { func echo(value: A): A = value }\n"
            + "interface Stage<X, Y>: Root<Y, []X> {}\n"
            + "interface Left<P, Q>: Stage<P, Q> {}\n"
            + "interface Right<M, N>: Root<N, []M> {}\n"
            + "class Leaf: Left<String, int>, Right<String, int> {}");
        var method = Method(analysis, "Root", "echo");
        var receiver = Constructed(analysis, "Leaf");

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        Assert.Equal(2, candidate.Paths.Length);
        Assert.Equal(4, candidate.Paths.Single(path => path.Length == 4).Length);
        Assert.Equal(3, candidate.Paths.Single(path => path.Length == 3).Length);
        Assert.Equal("int", candidate.ConstructedOwner.Arguments[0].Name);
        Assert.Equal(analysis.Types.GetArray(Nominal(analysis, "String")),
            candidate.ConstructedOwner.Arguments[1]);
        Assert.Equal(analysis.Types.GetPrimitive("int"), result.Body!.Summary.ReturnType);
        Assert.Equal(BodyBindingStatus.Bound, result.Body.Summary.Status);
    }

    [Fact]
    public void ASingleChangedNestedArgumentKeepsTheComposedOwnerAmbiguous()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<A, B> { func echo(value: A): A = value }\n"
            + "interface Stage<X, Y>: Root<Y, []X> {}\n"
            + "interface Composed<P, Q>: Stage<P, Q> {}\n"
            + "interface Direct<M, N>: Root<N, M> {}\n"
            + "class Leaf: Composed<String, int>, Direct<String, int> {}", allowSourceErrors: true);
        var method = Method(analysis, "Root", "echo");
        var receiver = Constructed(analysis, "Leaf");

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Ambiguous, result.Projection.Status);
        Assert.Null(result.Body);
        Assert.Equal(2, result.Projection.Candidates.Length);
        var composed = Assert.Single(result.Projection.Candidates,
            candidate => candidate.ConstructedOwner.Arguments[1].Kind == ResolvedTypeKind.Array);
        var direct = Assert.Single(result.Projection.Candidates,
            candidate => candidate.ConstructedOwner.Arguments[1].Kind != ResolvedTypeKind.Array);
        Assert.Single(composed.Paths);
        Assert.Single(direct.Paths);
        Assert.Equal("int", composed.ConstructedOwner.Arguments[0].Name);
        Assert.Equal("std.core.String", direct.ConstructedOwner.Arguments[1].Name);
        Assert.All(result.Projection.Candidates, candidate =>
            Assert.Same(candidate.ConstructedOwner.Declaration,
                result.Projection.Candidates[0].ConstructedOwner.Declaration));
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
