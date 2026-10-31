// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericInheritedMemberProjectionConstructedGraphTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}";

    private sealed record Edge(int? Parent, int FirstArgument, int SecondArgument)
    {
        internal static Edge ToNode(int parent, int first, int second) => new(parent, first, second);
        internal static Edge ToOwner(int first, int second) => new(null, first, second);
    }

    private sealed record GraphCase(
        int Seed,
        ImmutableArray<ImmutableArray<Edge>> Parents,
        ImmutableArray<Edge> LeafEdges);

    [Fact]
    public void ConstructedGenericGraphsMatchIndependentArgumentAndRouteOracle()
    {
        const int graphCount = 18;
        const int nodeCount = 5;
        GraphCase[] graphs = Enumerable.Range(201, graphCount)
            .Select(seed => Generate(seed, nodeCount)).ToArray();
        string source = BuildSource(graphs.ToImmutableArray());
        var session = AstTestSources.Session(source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        foreach (GraphCase graph in graphs)
        {
            string suffix = graph.Seed.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            AstTypeDeclaration owner = FindType(analysis, "Root" + suffix);
            AstFunction method = Assert.Single(owner.Members.OfType<AstFunction>());
            ResolvedType receiver = Constructed(analysis, "Leaf" + suffix,
                analysis.Types.GetPrimitive("int"), Nominal(analysis, "String"));
            Dictionary<string, List<string>> expected = EnumerateExpected(graph, suffix);

            GenericMemberBodyProjectionAnalysis result = analysis.GetGenericMemberBodyForReceiver(
                method, receiver, [], TestContext.Current.CancellationToken);

            OwnerProjectionStatus expectedStatus = expected.Count switch
            {
                0 => OwnerProjectionStatus.Missing,
                1 => OwnerProjectionStatus.Unique,
                _ => OwnerProjectionStatus.Ambiguous,
            };
            Assert.Equal(expectedStatus, result.Projection.Status);
            Assert.Equal(expected.Count, result.Projection.Candidates.Length);

            var actual = result.Projection.Candidates.ToDictionary(
                candidate => OwnerVector(candidate.ConstructedOwner.Arguments),
                candidate => candidate.Paths.Select(PathNames)
                    .OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
            var orderedExpectedKeys = expected.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
            Assert.Equal(orderedExpectedKeys, actual.Keys.OrderBy(key => key, StringComparer.Ordinal));
            foreach (string ownerVector in orderedExpectedKeys)
            {
                Assert.Equal(expected[ownerVector].OrderBy(path => path, StringComparer.Ordinal),
                    actual[ownerVector]);
            }

            if (expectedStatus == OwnerProjectionStatus.Unique)
            {
                Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
                Assert.Same(result.Projection.Candidates[0].ConstructedOwner.Arguments[0],
                    result.Body.Summary.ReturnType);
            }
            else
            {
                Assert.Null(result.Body);
            }
        }
    }

    private static GraphCase Generate(int seed, int nodeCount)
    {
        var random = new Random(seed);
        var parents = ImmutableArray.CreateBuilder<ImmutableArray<Edge>>(nodeCount);
        for (int node = 0; node < nodeCount; node++)
        {
            var edges = ImmutableArray.CreateBuilder<Edge>();
            for (int parent = node + 1; parent < nodeCount; parent++)
            {
                if (random.Next(100) < 37)
                {
                    edges.Add(Edge.ToNode(parent, random.Next(2), random.Next(2)));
                }
            }
            if (random.Next(100) < 62)
            {
                edges.Add(Edge.ToOwner(random.Next(2), random.Next(2)));
            }
            if (edges.Count == 0)
            {
                edges.Add(node + 1 < nodeCount
                    ? Edge.ToNode(node + 1, random.Next(2), random.Next(2))
                    : Edge.ToOwner(random.Next(2), random.Next(2)));
            }
            parents.Add(edges.ToImmutable());
        }

        var leafEdges = ImmutableArray.CreateBuilder<Edge>();
        for (int node = 0; node < Math.Min(3, nodeCount); node++)
        {
            if (random.Next(100) < 72)
            {
                leafEdges.Add(Edge.ToNode(node, random.Next(2), random.Next(2)));
            }
        }
        if (leafEdges.Count == 0) { leafEdges.Add(Edge.ToNode(0, 0, 1)); }
        return new(seed, parents.ToImmutable(), leafEdges.ToImmutable());
    }

    private static string BuildSource(ImmutableArray<GraphCase> graphs)
    {
        var source = new StringBuilder("module app\n");
        foreach (GraphCase graph in graphs)
        {
            string suffix = graph.Seed.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            source.Append("interface Root").Append(suffix)
                .Append("<A, B> { func choose(first: A, second: B): A = first }\n");
            for (int node = 0; node < graph.Parents.Length; node++)
            {
                source.Append("interface G").Append(suffix).Append('N').Append(node).Append("<A, B>: ");
                source.AppendJoin(", ", graph.Parents[node]
                    .Select(edge => TargetType(edge, suffix, "A", "B")));
                source.Append(" {}\n");
            }
            source.Append("class Leaf").Append(suffix).Append("<A, B>: ")
                .AppendJoin(", ", graph.LeafEdges.Select(edge => TargetType(edge, suffix, "A", "B")))
                .Append(" {}\n");
        }
        return source.ToString();
    }

    private static string TargetType(Edge edge, string suffix, string first, string second)
    {
        string genericArguments = string.Join(", ",
            edge.FirstArgument == 0 ? first : second,
            edge.SecondArgument == 0 ? first : second);
        string owner = edge.Parent is { } parent
            ? "G" + suffix + "N" + parent
            : "Root" + suffix;
        return owner + "<" + genericArguments + ">";
    }

    private static Dictionary<string, List<string>> EnumerateExpected(GraphCase graph, string suffix)
    {
        var expected = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (Edge edge in graph.LeafEdges)
        {
            string[] initial = ["int", "std.core.String"];
            Visit(edge.Parent!.Value,
                [initial[edge.FirstArgument], initial[edge.SecondArgument]],
                ["app.Leaf" + suffix]);
        }
        return expected;

        void Visit(int node, string[] arguments, ImmutableArray<string> prefix)
        {
            string nodeName = "app.G" + suffix + "N" + node;
            ImmutableArray<string> path = prefix.Add(nodeName);
            foreach (Edge edge in graph.Parents[node])
            {
                string[] next = [arguments[edge.FirstArgument], arguments[edge.SecondArgument]];
                if (edge.Parent is { } parent)
                {
                    Visit(parent, next, path);
                    continue;
                }

                string key = string.Join('|', next);
                string route = string.Join(" -> ", path.Add("app.Root" + suffix));
                if (!expected.TryGetValue(key, out List<string>? routes))
                {
                    routes = [];
                    expected.Add(key, routes);
                }
                if (!routes.Contains(route, StringComparer.Ordinal)) { routes.Add(route); }
            }
        }
    }

    private static string OwnerVector(ImmutableArray<ResolvedType> arguments) =>
        string.Join("|", arguments.Select(argument => argument.Name));

    private static string PathNames(ImmutableArray<ResolvedType> path) =>
        string.Join(" -> ", path.Select(type => type.Name));

    private static AstTypeDeclaration FindType(BodyAnalysis analysis, string name) =>
        analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>().Single(type => type.Name == name);

    private static ResolvedType Nominal(BodyAnalysis analysis, string name) =>
        analysis.Types.Nominal(analysis.Sources.Declarations.GetDeclaredSymbol(FindType(analysis, name))!, []);

    private static ResolvedType Constructed(BodyAnalysis analysis, string name,
        params ResolvedType[] arguments) => analysis.Types.Nominal(
        analysis.Sources.Declarations.GetDeclaredSymbol(FindType(analysis, name))!, arguments.ToImmutableArray());
}
