// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericInheritedMemberProjectionGraphTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}";

    [Fact]
    public void BinaryDiamondDepthMatrixRetainsEveryDistinctRouteAtTheCandidateBoundary()
    {
        foreach (int layers in Enumerable.Range(1, 7))
        {
            var analysis = Analyze(BinaryDiamond(layers));
            var root = Type(analysis, "Root");
            var method = Assert.Single(root.Members.OfType<AstFunction>());
            var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));

            var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
                TestContext.Current.CancellationToken);

            Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
            var candidate = Assert.Single(result.Projection.Candidates);
            Assert.Equal(1 << layers, candidate.Paths.Length);
            Assert.Equal(candidate.Paths.Length,
                candidate.Paths.Select(PathIdentity).Distinct(StringComparer.Ordinal).Count());
            Assert.All(candidate.Paths, path =>
            {
                Assert.Equal(2 * layers + 2, path.Length);
                Assert.Same(receiver, path[0]);
                Assert.Same(candidate.ConstructedOwner, path[^1]);
            });
            Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
        }
    }

    [Fact]
    public void CandidateOrderingDoesNotDependOnBaseClauseOrder()
    {
        const string common = "module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "interface TextPath: Root<String> {}\n"
            + "interface IntegerPath: Root<int> {}\n";
        var textFirst = Analyze(common + "class Leaf: TextPath, IntegerPath {}");
        var integerFirst = Analyze(common + "class Leaf: IntegerPath, TextPath {}");
        var textMethod = Method(textFirst, "echo");
        var integerMethod = Method(integerFirst, "echo");
        var textReceiver = Constructed(textFirst, "Leaf");
        var integerReceiver = Constructed(integerFirst, "Leaf");

        var textFirstResult = textFirst.GetGenericMemberBodyForReceiver(textMethod, textReceiver, [],
            TestContext.Current.CancellationToken);
        var integerFirstResult = integerFirst.GetGenericMemberBodyForReceiver(integerMethod, integerReceiver, [],
            TestContext.Current.CancellationToken);
        string[] firstOrder = OwnerArgumentNames(textFirstResult);
        string[] secondOrder = OwnerArgumentNames(integerFirstResult);

        Assert.Equal(OwnerProjectionStatus.Ambiguous, textFirstResult.Projection.Status);
        Assert.Equal(OwnerProjectionStatus.Ambiguous, integerFirstResult.Projection.Status);
        Assert.Equal(firstOrder, secondOrder);
        Assert.Null(textFirstResult.Body);
        Assert.Null(integerFirstResult.Body);
    }

    [Fact]
    public void ThreeEquivalentRoutesCollapseToOneOwnerWithoutDroppingRouteIdentity()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "interface North<T>: Root<T> {}\n"
            + "interface Center<T>: Root<T> {}\n"
            + "interface South<T>: Root<T> {}\n"
            + "class Leaf<T>: North<T>, Center<T>, South<T> {}");
        var method = Method(analysis, "echo");
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        Assert.Equal(3, candidate.Paths.Length);
        Assert.Equal(3, candidate.Paths.Select(path => path[1].Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(candidate.Paths, path =>
        {
            Assert.Equal(3, path.Length);
            Assert.Same(receiver, path[0]);
            Assert.Same(candidate.ConstructedOwner, path[^1]);
        });
        Assert.Equal("std.core.String", Assert.Single(candidate.ConstructedOwner.Arguments).Name);
    }

    [Fact]
    public void ModuleImportsDoNotChangeCanonicalDeclaringOwnerIdentity()
    {
        var analysis = AnalyzeFiles(
            "module app\nimport library\nclass Leaf<T>: Middle<T> {}",
            "module library\ninterface Root<T> { func echo(value: T): T = value }\n"
                + "interface Middle<U>: Root<[]U> {}",
            Core);
        var method = Method(analysis, "echo");
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        Assert.Equal("library.Root", candidate.ConstructedOwner.Name);
        Assert.Equal(analysis.Types.GetArray(Nominal(analysis, "String")),
            Assert.Single(candidate.ConstructedOwner.Arguments));
        Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
    }

    private static string BinaryDiamond(int layers)
    {
        var source = new StringBuilder("module app\n")
            .Append("interface Root<T> { func echo(value: T): T = value }\n");
        for (int layer = 1; layer <= layers; layer++)
        {
            string parent = layer == 1 ? "Root" : "Join" + (layer - 1);
            source.Append("interface Left").Append(layer).Append("<T>: ").Append(parent).Append("<T> {}\n")
                .Append("interface Right").Append(layer).Append("<T>: ").Append(parent).Append("<T> {}\n")
                .Append("interface Join").Append(layer).Append("<T>: Left").Append(layer).Append("<T>, Right")
                .Append(layer).Append("<T> {}\n");
        }
        source.Append("class Leaf<T>: Join").Append(layers).Append("<T> {}");
        return source.ToString();
    }

    private static string PathIdentity(ImmutableArray<ResolvedType> path) =>
        string.Join("/", path.Select(type => type.Name));

    private static string[] OwnerArgumentNames(GenericMemberBodyProjectionAnalysis result) =>
        result.Projection.Candidates.Select(candidate => Assert.Single(candidate.ConstructedOwner.Arguments).Name)
            .ToArray();

    private static BodyAnalysis Analyze(string source) => AnalyzeFiles(source, Core);

    private static BodyAnalysis AnalyzeFiles(params string[] sources)
    {
        var session = AstTestSources.Session(sources);
        var analysis = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(analysis, TestContext.Current.CancellationToken);
    }

    private static AstTypeDeclaration Type(BodyAnalysis analysis, string name) =>
        analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>().Single(type => type.Name == name);

    private static AstFunction Method(BodyAnalysis analysis, string name) =>
        analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>().SelectMany(type => type.Members)
            .OfType<AstFunction>().Single(method => method.Name == name);

    private static ResolvedType Nominal(BodyAnalysis analysis, string name) => analysis.Types.Nominal(
        analysis.Sources.Declarations.GetDeclaredSymbol(Type(analysis, name))!, []);

    private static ResolvedType Constructed(BodyAnalysis analysis, string name, params ResolvedType[] arguments) =>
        analysis.Types.Nominal(analysis.Sources.Declarations.GetDeclaredSymbol(Type(analysis, name))!,
            arguments.ToImmutableArray());
}
