// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

public enum LocalSymbolKind
{
    Parameter,
    Variable,
    Function,
    Catch,
    Pattern,
    AccessorValue,
    ConstructorProperty,
}

/// <summary>A lexical declaration, independent of definite assignment and inferred expression types.</summary>
public sealed record LocalSymbol(
    int Id,
    string Name,
    LocalSymbolKind Kind,
    AstOrigin Origin,
    AstType? WrittenType,
    bool IsMutable,
    int VisibleFrom,
    int ScopeId);

public sealed class LexicalScope
{
    internal LexicalScope(int id, int? parentId, AstNode owner, ImmutableArray<LocalSymbol> symbols)
    {
        Id = id;
        ParentId = parentId;
        Owner = owner;
        Symbols = symbols;
    }

    public int Id { get; }
    public int? ParentId { get; }
    public AstNode Owner { get; }
    public ImmutableArray<LocalSymbol> Symbols { get; }
}

public sealed record LexicalLookup(
    ImmutableArray<LocalSymbol> Locals,
    NameLookup Declarations)
{
    public bool IsLocal => !Locals.IsEmpty;
    public bool IsMissing => Locals.IsEmpty && Declarations.Kind == LookupKind.Missing;
}

public sealed record NameBinding(AstAtom Reference, LexicalLookup Lookup, bool CapturesOuterLocal);
