// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

public enum ResolvedTypeKind
{
    Error,
    Primitive,
    Nominal,
    Parameter,
    Bottom,
    Nullable,
    Array,
    FixedArray,
    Tuple,
    Function,
}

/// <summary>Interned type identity belonging to one TypeAnalysis, without runtime layout or conversions.</summary>
public sealed class ResolvedType
{
    internal ResolvedType(int id, ResolvedTypeKind kind, string name, DeclarationSymbol? declaration,
        ImmutableArray<ResolvedType> arguments, ImmutableArray<AstPassingMode> modes,
        ImmutableArray<ResolvedType> effects)
    {
        Id = id;
        Kind = kind;
        Name = name;
        Declaration = declaration;
        Arguments = arguments;
        ParameterModes = modes;
        Effects = effects;
    }

    public int Id { get; }
    public ResolvedTypeKind Kind { get; }
    public string Name { get; }
    public DeclarationSymbol? Declaration { get; }
    public ImmutableArray<ResolvedType> Arguments { get; }
    public ImmutableArray<AstPassingMode> ParameterModes { get; }
    public ImmutableArray<ResolvedType> Effects { get; }
    public bool IsNullable => Kind == ResolvedTypeKind.Nullable;
    public bool IsError => Kind == ResolvedTypeKind.Error;
    public bool IsRefLike => Declaration?.Parts.FirstOrDefault()?.SyntaxKind == Cst.NodeKind.RefStructDeclaration
        || Kind is ResolvedTypeKind.Nullable or ResolvedTypeKind.FixedArray or ResolvedTypeKind.Tuple
        && Arguments.Any(argument => argument.IsRefLike);
}
