// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class ModuleGraphDifferentialTests
{
    [Fact]
    public void SeededGraphsMatchAnIndependentReachabilityPartition()
    {
        var random = new Random(23132026);
        for (int sample = 0; sample < 80; sample++)
        {
            int count = random.Next(1, 28);
            var edges = new Dictionary<string, IEnumerable<string>>(StringComparer.Ordinal);
            for (int vertex = 0; vertex < count; vertex++)
            {
                var targets = new List<string>();
                for (int target = 0; target < count; target++)
                {
                    if (random.Next(7) == 0) { targets.Add(Name(target)); }
                }
                edges.Add(Name(vertex), targets);
            }
            var graph = ModuleGraph.Create(edges, TestContext.Current.CancellationToken);
            var reachability = edges.Keys.ToDictionary(name => name, name => Reach(edges, name), StringComparer.Ordinal);
            var components = graph.CompilationGroups;
            var allMembers = components.SelectMany(group => group).ToArray();
            Assert.Equal(count, allMembers.Length);
            Assert.Equal(count, allMembers.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(edges.Keys.Order(StringComparer.Ordinal), allMembers.Order(StringComparer.Ordinal));
            foreach (var left in edges.Keys)
            {
                var actualDependencies = graph.TransitiveDependencies(left);
                Assert.Equal(reachability[left].Where(name => name != left).Order(StringComparer.Ordinal), actualDependencies);
                foreach (var right in edges.Keys)
                {
                    bool expectedSame = left == right
                        || reachability[left].Contains(right) && reachability[right].Contains(left);
                    bool actualSame = components.Any(group => group.Contains(left) && group.Contains(right));
                    Assert.Equal(expectedSame, actualSame);
                }
            }
            Assert.Equal(HasCycle(edges, reachability), graph.HasCycle);
            VerifyDependencyOrder(graph);
        }
    }

    [Fact]
    public void InputAndDependencyPermutationCannotChangeThePartition()
    {
        var original = new Dictionary<string, IEnumerable<string>>(StringComparer.Ordinal)
        {
            ["app"] = new[] { "api", "logging" },
            ["api"] = new[] { "models", "contracts" },
            ["models"] = new[] { "contracts" },
            ["contracts"] = new[] { "models" },
            ["logging"] = Array.Empty<string>(),
        };
        var baseline = ModuleGraph.Create(original, TestContext.Current.CancellationToken);
        var random = new Random(404);
        for (int sample = 0; sample < 40; sample++)
        {
            var permuted = original.OrderBy(_ => random.Next()).Select(pair =>
                new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value.OrderBy(_ => random.Next()).ToArray()));
            var graph = ModuleGraph.Create(permuted, TestContext.Current.CancellationToken);
            Assert.Equal(baseline.CompilationGroups.Select(group => string.Join(',', group)),
                graph.CompilationGroups.Select(group => string.Join(',', group)));
            VerifyDependencyOrder(graph);
        }
    }

    private static string Name(int index)
    {
        return "module" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static HashSet<string> Reach(Dictionary<string, IEnumerable<string>> edges, string start)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(edges[start]);
        while (queue.TryDequeue(out var name))
        {
            if (!result.Add(name)) { continue; }
            foreach (var target in edges[name]) { queue.Enqueue(target); }
        }
        return result;
    }

    private static bool HasCycle(Dictionary<string, IEnumerable<string>> edges,
        Dictionary<string, HashSet<string>> reachability)
    {
        return edges.Keys.Any(name => reachability[name].Contains(name));
    }

    private static void VerifyDependencyOrder(ModuleGraph graph)
    {
        var groupOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < graph.CompilationGroups.Length; index++)
        {
            foreach (var member in graph.CompilationGroups[index]) { groupOf.Add(member, index); }
        }
        foreach (var pair in graph.Edges)
        {
            foreach (var dependency in pair.Value)
            {
                Assert.True(groupOf[dependency] <= groupOf[pair.Key], pair.Key + " must follow " + dependency);
            }
        }
    }
}
