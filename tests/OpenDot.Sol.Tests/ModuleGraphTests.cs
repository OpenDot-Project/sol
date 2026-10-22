// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class ModuleGraphTests
{
    private static KeyValuePair<string, IEnumerable<string>> Edge(string name, params string[] targets)
    {
        return new(name, targets);
    }

    [Fact]
    public void DependenciesPrecedeTheirConsumers()
    {
        var graph = ModuleGraph.Create(new[] { Edge("app", "middle"), Edge("middle", "core"), Edge("core") }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "core", "middle", "app" }, graph.CompilationGroups.Select(g => Assert.Single(g)));
        Assert.False(graph.HasCycle);
    }

    [Fact]
    public void MissingTargetsRemainVisibleInGraph()
    {
        var graph = ModuleGraph.Create(new[] { Edge("app", "not.provided") }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(graph.Edges.ContainsKey("not.provided"));
        Assert.Empty(graph.Edges["not.provided"]);
        Assert.Equal(new[] { "not.provided" }, graph.TransitiveDependencies("app"));
    }

    [Fact]
    public void SelfImportIsAnOrdinaryCycle()
    {
        var graph = ModuleGraph.Create(new[] { Edge("self", "self") }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(graph.HasCycle);
        Assert.Empty(graph.TransitiveDependencies("self"));
        Assert.Equal("self", Assert.Single(Assert.Single(graph.CompilationGroups)));
    }

    [Fact]
    public void DisconnectedAndCyclicGroupsAreDeterministic()
    {
        var edges = new[] { Edge("z"), Edge("a", "b"), Edge("b", "a"), Edge("c", "a") };
        var first = ModuleGraph.Create(edges, cancellationToken: TestContext.Current.CancellationToken);
        var second = ModuleGraph.Create(edges.Reverse(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(first.CompilationGroups.Select(g => string.Join(',', g)), second.CompilationGroups.Select(g => string.Join(',', g)));
        Assert.Contains(first.CompilationGroups, group => group.SequenceEqual(new[] { "a", "b" }));
        Assert.Equal(new[] { "a", "b" }, first.TransitiveDependencies("c"));
    }

    [Fact]
    public void DuplicateEdgesDoNotDuplicateDependencies()
    {
        var graph = ModuleGraph.Create(new[] { Edge("a", "b", "b", "c"), Edge("b", "c"), Edge("c") }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "b", "c" }, graph.Edges["a"]);
        Assert.Equal(new[] { "b", "c" }, graph.TransitiveDependencies("a"));
    }

    [Fact]
    public void DeepGraphsUseIterativeTraversal()
    {
        const int count = 6000;
        var graph = ModuleGraph.Create(Enumerable.Range(0, count).Select(index => index == count - 1
            ? Edge("m" + index) : Edge("m" + index, "m" + (index + 1))), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(count, graph.CompilationGroups.Length);
        Assert.Equal(count - 1, graph.TransitiveDependencies("m0").Length);
        Assert.Equal("m5999", Assert.Single(graph.CompilationGroups[0]));
    }

    [Fact]
    public void GraphInputValidationDoesNotHideBadManifests()
    {
        Assert.Throws<ArgumentNullException>(() => ModuleGraph.Create(null!, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => ModuleGraph.Create(new[] { Edge(" ") }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => ModuleGraph.Create(new[] { Edge("a", "") }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => ModuleGraph.Create(new[] { Edge("a"), Edge("a") }, cancellationToken: TestContext.Current.CancellationToken));
        var graph = ModuleGraph.Create(new[] { Edge("a") }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentException>(() => graph.TransitiveDependencies("missing"));
        Assert.Throws<OperationCanceledException>(() => ModuleGraph.Create(new[] { Edge("a") }, new CancellationToken(true)));
    }
}
