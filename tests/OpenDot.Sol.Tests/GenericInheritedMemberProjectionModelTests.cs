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

public sealed class GenericInheritedMemberProjectionModelTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}";

    private sealed record ParentEdge(int? Node, string? OwnerArgument)
    {
        internal static ParentEdge ToNode(int node) => new(node, null);
        internal static ParentEdge ToOwner(string argument) => new(null, argument);
    }

    private sealed record GraphCase(
        int Seed,
        ImmutableArray<ImmutableArray<ParentEdge>> Parents,
        ImmutableArray<int> Starts);

    [Fact]
    public void ThirtyDeterministicAcyclicGraphsMatchAnIndependentRouteOracle()
    {
        const int graphCount = 30;
        const int nodesPerGraph = 6;
        GraphCase[] graphs = Enumerable.Range(1, graphCount)
            .Select(seed => Generate(seed, nodesPerGraph)).ToArray();
        string source = BuildSource(graphs.ToImmutableArray());
        var session = AstTestSources.Session(source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);

        foreach (var graph in graphs)
        {
            string suffix = graph.Seed.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            var owner = Type(analysis, "Root" + suffix);
            var method = Assert.Single(owner.Members.OfType<AstFunction>());
            var receiver = Types(analysis, "Leaf" + suffix, []);
            var expected = EnumerateExpectedPaths(graph, suffix);

            var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
                TestContext.Current.CancellationToken);

            OwnerProjectionStatus expectedStatus = expected.Count switch
            {
                0 => OwnerProjectionStatus.Missing,
                1 => OwnerProjectionStatus.Unique,
                _ => OwnerProjectionStatus.Ambiguous,
            };
            Assert.Equal(expectedStatus, result.Projection.Status);
            Assert.Equal(expected.Count, result.Projection.Candidates.Length);
            if (expected.Count == 0)
            {
                Assert.Empty(result.Projection.Candidates);
                Assert.Null(result.Body);
                continue;
            }

            var actual = result.Projection.Candidates.ToDictionary(
                candidate => Assert.Single(candidate.ConstructedOwner.Arguments).Name,
                candidate => candidate.Paths.Select(PathNames)
                    .OrderBy(path => path, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            var expectedByArgument = expected.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
            Assert.Equal(expectedByArgument.Keys.OrderBy(key => key, StringComparer.Ordinal),
                actual.Keys.OrderBy(key => key, StringComparer.Ordinal));
            foreach (var (argument, paths) in expectedByArgument)
            {
                Assert.Equal(paths, actual[argument]);
            }

            if (expectedStatus == OwnerProjectionStatus.Unique)
            {
                Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
                Assert.Equal(Assert.Single(expectedByArgument.Keys), result.Body.Summary.ReturnType!.Name);
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
        var parents = ImmutableArray.CreateBuilder<ImmutableArray<ParentEdge>>(nodeCount);
        for (int node = 0; node < nodeCount; node++)
        {
            var edges = ImmutableArray.CreateBuilder<ParentEdge>();
            for (int parent = node + 1; parent < nodeCount; parent++)
            {
                if (random.Next(100) < 34) { edges.Add(ParentEdge.ToNode(parent)); }
            }
            if (random.Next(100) < 58)
            {
                edges.Add(ParentEdge.ToOwner(random.Next(2) == 0 ? "int" : "std.core.String"));
            }
            if (edges.Count == 0)
            {
                edges.Add(node + 1 < nodeCount
                    ? ParentEdge.ToNode(node + 1)
                    : ParentEdge.ToOwner(random.Next(2) == 0 ? "int" : "std.core.String"));
            }
            parents.Add(edges.ToImmutable());
        }

        var starts = ImmutableArray.CreateBuilder<int>();
        starts.Add(0);
        for (int node = 1; node < Math.Min(3, nodeCount); node++)
        {
            if (random.Next(100) < 35) { starts.Add(node); }
        }
        return new(seed, parents.ToImmutable(), starts.ToImmutable());
    }

    private static string BuildSource(ImmutableArray<GraphCase> graphs)
    {
        var source = new StringBuilder("module app\n");
        foreach (var graph in graphs)
        {
            string suffix = graph.Seed.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            source.Append("interface Root").Append(suffix)
                .Append("<T> { func echo(value: T): T = value }\n");
            for (int node = 0; node < graph.Parents.Length; node++)
            {
                source.Append("interface G").Append(suffix).Append('N').Append(node).Append(": ");
                source.AppendJoin(", ", graph.Parents[node].Select(edge => edge.Node is { } parent
                    ? "G" + suffix + "N" + parent
                    : "Root" + suffix + "<" + edge.OwnerArgument + ">"));
                source.Append(" {}\n");
            }
            source.Append("class Leaf").Append(suffix).Append(": ");
            source.AppendJoin(", ", graph.Starts.Select(node => "G" + suffix + "N" + node));
            source.Append(" {}\n");
        }
        return source.ToString();
    }

    private static ImmutableDictionary<string, ImmutableArray<string>> EnumerateExpectedPaths(
        GraphCase graph, string suffix)
    {
        var paths = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.Ordinal);
        foreach (int start in graph.Starts)
        {
            var prefix = ImmutableArray.Create("app.Leaf" + suffix);
            Visit(start, prefix);
        }
        return paths.ToImmutable();

        void Visit(int node, ImmutableArray<string> prefix)
        {
            string nodeName = "app.G" + suffix + "N" + node;
            if (prefix.Contains(nodeName, StringComparer.Ordinal)) { return; }
            var currentPath = prefix.Add(nodeName);
            foreach (var edge in graph.Parents[node])
            {
                if (edge.Node is { } parent)
                {
                    Visit(parent, currentPath);
                    continue;
                }

                string ownerName = "app.Root" + suffix;
                string path = string.Join(" -> ", currentPath.Add(ownerName));
                string typeArgument = edge.OwnerArgument!;
                if (!paths.TryGetValue(typeArgument, out var existing)) { existing = []; }
                paths[typeArgument] = existing.Add(path);
            }
        }
    }

    private static string PathNames(ImmutableArray<ResolvedType> path) =>
        string.Join(" -> ", path.Select(type => type.Name));

    private static AstTypeDeclaration Type(BodyAnalysis analysis, string name) =>
        analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>().Single(type => type.Name == name);

    private static ResolvedType Types(BodyAnalysis analysis, string name, ResolvedType[] arguments) =>
        analysis.Types.Nominal(analysis.Sources.Declarations.GetDeclaredSymbol(Type(analysis, name))!,
            arguments.ToImmutableArray());
}
