// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace OpenDot.Sol.Analysis;

internal sealed partial class AnalysisBuilder
{
    private void BuildQualifiers()
    {
        var maps = Modules.Keys.ToDictionary(name => name,
            _ => new Dictionary<string, HashSet<string>>(StringComparer.Ordinal), StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var module in Modules.Values)
            {
                _cancellation.ThrowIfCancellationRequested();
                foreach (var import in module.Files.SelectMany(f => f.Root.Imports).Where(i => i.IsPublic))
                {
                    if (!maps.TryGetValue(import.ModuleName, out var target)) { continue; }
                    if (import.Alias is not null)
                    {
                        changed |= Add(maps[module.Name], import.Alias, [import.ModuleName]);
                    }
                    else if (!import.IsSelective)
                    {
                        changed |= Add(maps[module.Name], import.ModuleName, [import.ModuleName]);
                        foreach (var pair in target.ToArray())
                        {
                            changed |= Add(maps[module.Name], pair.Key, pair.Value.ToArray());
                        }
                    }
                }
            }
        } while (changed);
        foreach (var pair in maps)
        {
            Qualifiers.Add(pair.Key, pair.Value.ToImmutableDictionary(entry => entry.Key,
                entry => entry.Value.Order(StringComparer.Ordinal).ToImmutableArray(), StringComparer.Ordinal));
        }
    }

    private static bool Add(Dictionary<string, HashSet<string>> map, string name, IEnumerable<string> modules)
    {
        if (!map.TryGetValue(name, out var targets))
        {
            targets = new(StringComparer.Ordinal);
            map.Add(name, targets);
        }
        bool changed = false;
        foreach (var module in modules) { changed |= targets.Add(module); }
        return changed;
    }
}
