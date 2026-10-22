// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

public enum SymbolKind
{
    Type,
    Function,
    Property,
    Constructor,
    Indexer,
    EnumVariant,
}

/// <summary>Declaration identity within one analysis session, not a stable binary or cache ID.</summary>
public sealed class DeclarationSymbol
{
    internal DeclarationSymbol(int id, SymbolKind kind, string moduleName, string qualifiedName,
        string name, int arity, ImmutableArray<AstDeclaration> parts,
        ImmutableArray<DeclarationSymbol> members, string signature)
    {
        Id = id;
        Kind = kind;
        ModuleName = moduleName;
        QualifiedName = qualifiedName;
        Name = name;
        Arity = arity;
        Parts = parts;
        Members = members;
        Signature = signature;
    }

    public int Id { get; }
    public SymbolKind Kind { get; }
    public string ModuleName { get; }
    public string QualifiedName { get; }
    public string Name { get; }
    public int Arity { get; }
    public string Signature { get; }
    public ImmutableArray<AstDeclaration> Parts { get; }
    public ImmutableArray<DeclarationSymbol> Members { get; }
    public bool IsPublic => Parts.All(p => p.IsPublic);
    public bool IsPartial => Parts.Length > 1 && Parts.All(p => p.IsPartial);

    public IEnumerable<DeclarationSymbol> FindMembers(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Members.Where(member => member.Name == name);
    }
}

public sealed class ModuleSymbol
{
    internal ModuleSymbol(string name, ImmutableArray<AstSnapshot> files,
        ImmutableArray<DeclarationSymbol> declarations)
    {
        Name = name;
        Files = files;
        Declarations = declarations;
    }

    public string Name { get; }
    public ImmutableArray<AstSnapshot> Files { get; }
    public ImmutableArray<DeclarationSymbol> Declarations { get; }
}

public enum LookupKind
{
    Missing,
    Found,
    OverloadSet,
    Ambiguous,
}

public enum LookupScope
{
    None,
    Member,
    Module,
    Import,
    Core,
}

public sealed record NameLookup(
    LookupKind Kind,
    LookupScope Scope,
    ImmutableArray<DeclarationSymbol> Candidates)
{
    public bool IsSuccess => Kind is LookupKind.Found or LookupKind.OverloadSet;

    internal static NameLookup From(IEnumerable<DeclarationSymbol> symbols, LookupScope scope)
    {
        var candidates = symbols.Distinct(ReferenceEqualityComparer.Instance)
            .Cast<DeclarationSymbol>().OrderBy(s => s.QualifiedName, StringComparer.Ordinal)
            .ThenBy(s => s.Signature, StringComparer.Ordinal).ToImmutableArray();
        if (candidates.IsEmpty)
        {
            return new(LookupKind.Missing, LookupScope.None, []);
        }
        if (candidates.Length == 1)
        {
            return new(LookupKind.Found, scope, candidates);
        }
        bool overload = candidates.All(s => s.Kind == SymbolKind.Function)
            && candidates.Select(s => s.ModuleName).Distinct(StringComparer.Ordinal).Count() == 1;
        return new(overload ? LookupKind.OverloadSet : LookupKind.Ambiguous, scope, candidates);
    }
}

public sealed record AnalysisOptions
{
    /// <summary>One explicit distribution is indexed by a session. Cross-distribution partial merging is not performed.</summary>
    public string DistributionName { get; init; } = "application";

    /// <summary>Only compiler bootstrap/test callers should disable the implicit core requirement.</summary>
    public bool RequireCore { get; init; } = true;

    public int DiagnosticLimit { get; init; } = 1000;
}
