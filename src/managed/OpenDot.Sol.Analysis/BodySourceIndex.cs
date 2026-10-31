// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

/// <summary>Immutable source ownership shared by body instances in one source analysis.</summary>
internal sealed class BodySourceIndex : IReadOnlyDictionary<AstNode, AstSnapshot>
{
    private static readonly ConditionalWeakTable<SourceAnalysis, BodySourceIndex> Indexes = new();
    private readonly ImmutableDictionary<AstNode, AstSnapshot> _files;
    private readonly ImmutableArray<KeyValuePair<AstNode, AstSnapshot>> _entries;

    private BodySourceIndex(SourceAnalysis sources)
    {
        Types = sources.Types;
        var files = ImmutableDictionary.CreateBuilder<AstNode, AstSnapshot>(ReferenceEqualityComparer.Instance);
        var entries = ImmutableArray.CreateBuilder<KeyValuePair<AstNode, AstSnapshot>>();
        foreach (var file in sources.Declarations.Files)
        {
            foreach (var node in file.Nodes)
            {
                files.Add(node, file);
                entries.Add(new(node, file));
            }
        }
        _files = files.ToImmutable();
        _entries = entries.ToImmutable();
        Nodes = ImmutableHashSet.CreateRange<AstNode>(ReferenceEqualityComparer.Instance, _files.Keys);
    }

    internal IReadOnlyDictionary<AstNode, AstSnapshot> Files => this;
    internal ImmutableHashSet<AstNode> Nodes { get; }
    internal TypeAnalysis Types { get; }

    // Lookup is hashed, but enumeration preserves the source traversal order used by the binder.
    public int Count => _files.Count;
    public AstSnapshot this[AstNode key] => _files[key];
    public IEnumerable<AstNode> Keys => _entries.Select(entry => entry.Key);
    public IEnumerable<AstSnapshot> Values => _entries.Select(entry => entry.Value);
    public bool ContainsKey(AstNode key) => _files.ContainsKey(key);
    public bool TryGetValue(AstNode key, [MaybeNullWhen(false)] out AstSnapshot value) =>
        _files.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<AstNode, AstSnapshot>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<AstNode, AstSnapshot>>)_entries).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal static BodySourceIndex Get(SourceAnalysis sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return Indexes.GetValue(sources, static source => new BodySourceIndex(source));
    }
}
