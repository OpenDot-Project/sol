// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;

namespace OpenDot.Sol.Analysis;

/// <summary>Explicit source-module graph. Ordinary signature cycles are legal compilation groups.</summary>
public sealed class ModuleGraph
{
    private ModuleGraph(ImmutableDictionary<string, ImmutableArray<string>> edges,
        ImmutableArray<ImmutableArray<string>> groups)
    {
        Edges = edges;
        CompilationGroups = groups;
    }

    public ImmutableDictionary<string, ImmutableArray<string>> Edges { get; }

    /// <summary>Strongly connected groups in dependency-first order, with ordinal names within each group.</summary>
    public ImmutableArray<ImmutableArray<string>> CompilationGroups { get; }

    public static ModuleGraph Create(IEnumerable<KeyValuePair<string, IEnumerable<string>>> modules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modules);
        cancellationToken.ThrowIfCancellationRequested();
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.Ordinal);
        foreach (var pair in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            var targets = pair.Value.ToArray();
            if (targets.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Module dependencies must have explicit nonempty names.", nameof(modules));
            }
            if (builder.ContainsKey(pair.Key))
            {
                throw new ArgumentException("Duplicate graph module name.", nameof(modules));
            }
            builder.Add(pair.Key, targets.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray());
        }
        foreach (var name in builder.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal).ToArray())
        {
            builder.TryAdd(name, []);
        }
        var edges = builder.ToImmutable();
        return new(edges, Components(edges, cancellationToken));
    }

    public bool HasCycle
    {
        get
        {
            return CompilationGroups.Any(group => group.Length > 1
                || group.Length == 1 && Edges[group[0]].Contains(group[0], StringComparer.Ordinal));
        }
    }

    public ImmutableArray<string> TransitiveDependencies(string moduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        if (!Edges.ContainsKey(moduleName))
        {
            throw new ArgumentException("Unknown graph module.", nameof(moduleName));
        }
        var visited = new HashSet<string>(StringComparer.Ordinal) { moduleName };
        var pending = new Stack<string>(Edges[moduleName].Reverse());
        while (pending.TryPop(out var name))
        {
            if (!visited.Add(name)) { continue; }
            foreach (var target in Edges[name].Reverse()) { pending.Push(target); }
        }
        visited.Remove(moduleName);
        return visited.Order(StringComparer.Ordinal).ToImmutableArray();
    }

    private static ImmutableArray<ImmutableArray<string>> Components(
        ImmutableDictionary<string, ImmutableArray<string>> edges, CancellationToken token)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var finish = new List<string>();
        foreach (var name in edges.Keys.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (visited.Contains(name)) { continue; }
            var pending = new Stack<(string Name, bool Exit)>();
            pending.Push((name, false));
            while (pending.TryPop(out var frame))
            {
                token.ThrowIfCancellationRequested();
                if (frame.Exit)
                {
                    finish.Add(frame.Name);
                    continue;
                }
                if (!visited.Add(frame.Name)) { continue; }
                pending.Push((frame.Name, true));
                foreach (var next in edges[frame.Name].Reverse()) { pending.Push((next, false)); }
            }
        }
        var reversed = edges.Keys.ToDictionary(n => n, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var pair in edges)
        {
            foreach (var next in pair.Value) { reversed[next].Add(pair.Key); }
        }
        visited.Clear();
        var groups = new List<ImmutableArray<string>>();
        for (int index = finish.Count - 1; index >= 0; index--)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(finish[index])) { continue; }
            var members = new List<string>();
            var pending = new Stack<string>();
            pending.Push(finish[index]);
            while (pending.TryPop(out var name))
            {
                token.ThrowIfCancellationRequested();
                members.Add(name);
                foreach (var next in reversed[name])
                {
                    if (visited.Add(next)) { pending.Push(next); }
                }
            }
            groups.Add(members.Order(StringComparer.Ordinal).ToImmutableArray());
        }
        groups.Reverse();
        return groups.ToImmutableArray();
    }
}
