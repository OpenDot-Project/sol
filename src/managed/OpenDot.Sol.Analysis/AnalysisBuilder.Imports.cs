// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class AnalysisBuilder
{
    private void BuildGraphs()
    {
        var all = Modules.Values.Select(module => new KeyValuePair<string, IEnumerable<string>>(module.Name,
            module.Files.SelectMany(f => f.Root.Imports).Select(i => i.ModuleName)
                .Where(name => !string.IsNullOrWhiteSpace(name))));
        Graph = ModuleGraph.Create(all, _cancellation);
        var exported = Modules.Values.Select(module => new KeyValuePair<string, IEnumerable<string>>(module.Name,
            module.Files.SelectMany(f => f.Root.Imports).Where(i => i.IsPublic).Select(i => i.ModuleName)
                .Where(name => !string.IsNullOrWhiteSpace(name))));
        ReExportGraph = ModuleGraph.Create(exported, _cancellation);
        foreach (var cycle in ReExportGraph.CompilationGroups.Where(group => group.Length > 1
            || group.Length == 1 && ReExportGraph.Edges[group[0]].Contains(group[0], StringComparer.Ordinal)))
        {
            foreach (var name in cycle)
            {
                if (!Modules.TryGetValue(name, out var module)) { continue; }
                foreach (var import in module.Files.SelectMany(f => f.Root.Imports)
                    .Where(i => i.IsPublic && cycle.Contains(i.ModuleName, StringComparer.Ordinal)))
                {
                    Report("SOL2007", "Public re-export cycles are not allowed: " + string.Join(", ", cycle), import);
                }
            }
        }
    }

    private void ValidateImports()
    {
        foreach (var file in Files)
        {
            _cancellation.ThrowIfCancellationRequested();
            if (Options.RequireCore && !Modules.ContainsKey("std.core"))
            {
                Report("SOL2001", "The explicit source set does not provide the implicit std.core module.", file.Root);
            }
            foreach (var import in file.Root.Imports)
            {
                if (!Modules.ContainsKey(import.ModuleName))
                {
                    Report("SOL2001", "Unresolved source module: " + import.ModuleName, import);
                }
                if (import.Alias is not null && import.IsSelective)
                {
                    Report("SOL2008", "A module alias cannot also declare selective bindings.", import);
                }
                foreach (var duplicate in import.Items.GroupBy(i => i.LocalName, StringComparer.Ordinal).Where(g => g.Count() > 1))
                {
                    Report("SOL2008", "Duplicate selective binding: " + duplicate.Key, import);
                }
            }
        }
    }

    private void BuildExports()
    {
        var maps = Modules.Values.ToDictionary(module => module.Name,
            module => module.Declarations.Where(d => d.IsPublic).GroupBy(d => d.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal), StringComparer.Ordinal);
        // A finite fixed point handles erroneous cyclic graphs without recursion or unbounded growth.
        bool changed;
        do
        {
            changed = false;
            foreach (var module in Modules.Values.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                _cancellation.ThrowIfCancellationRequested();
                foreach (var import in module.Files.SelectMany(f => f.Root.Imports).Where(i => i.IsPublic && i.Alias is null))
                {
                    if (!maps.TryGetValue(import.ModuleName, out var target)) { continue; }
                    var entries = import.IsSelective
                        ? import.Items.Select(item => (Local: item.LocalName, Symbols: target.GetValueOrDefault(item.OriginalName, [])))
                        : target.Select(pair => (Local: pair.Key, Symbols: pair.Value)).ToArray();
                    foreach (var entry in entries.ToArray())
                    {
                        if (!maps[module.Name].TryGetValue(entry.Local, out var symbols))
                        {
                            symbols = [];
                            maps[module.Name].Add(entry.Local, symbols);
                        }
                        foreach (var symbol in entry.Symbols.ToArray())
                        {
                            if (!symbols.Contains(symbol))
                            {
                                symbols.Add(symbol);
                                changed = true;
                            }
                        }
                    }
                }
            }
        } while (changed);
        foreach (var pair in maps)
        {
            Exports.Add(pair.Key, pair.Value.ToImmutableDictionary(entry => entry.Key,
                entry => entry.Value.OrderBy(s => s.QualifiedName, StringComparer.Ordinal)
                    .ThenBy(s => s.Signature, StringComparer.Ordinal).ToImmutableArray(), StringComparer.Ordinal));
        }
    }

    private void ValidateSelectiveImports()
    {
        foreach (var file in Files)
        {
            foreach (var import in file.Root.Imports)
            {
                _cancellation.ThrowIfCancellationRequested();
                if (!Exports.TryGetValue(import.ModuleName, out var exports)) { continue; }
                foreach (var item in import.Items)
                {
                    if (!exports.TryGetValue(item.OriginalName, out var symbols) || symbols.IsEmpty)
                    {
                        Report("SOL2008", "No public export named " + item.OriginalName + " in " + import.ModuleName, import);
                    }
                }
            }
        }
    }
}
