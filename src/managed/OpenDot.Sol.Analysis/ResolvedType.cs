// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Collections.Generic;
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
        ImmutableArray<ResolvedType> effects, GenericParameterIdentity? parameter = null)
    {
        Id = id;
        Kind = kind;
        Name = name;
        Declaration = declaration;
        Arguments = arguments;
        ParameterModes = modes;
        Effects = effects;
        Parameter = parameter;
    }

    public int Id { get; }
    public ResolvedTypeKind Kind { get; }
    public string Name { get; }
    public DeclarationSymbol? Declaration { get; }
    public ImmutableArray<ResolvedType> Arguments { get; }
    public ImmutableArray<AstPassingMode> ParameterModes { get; }
    public ImmutableArray<ResolvedType> Effects { get; }
    public GenericParameterIdentity? Parameter { get; }
    public bool IsNullable => Kind == ResolvedTypeKind.Nullable;
    public bool IsError => Kind == ResolvedTypeKind.Error;
    public bool IsRefLike
    {
        get
        {
            var pending = new Stack<ResolvedType>();
            var visited = new HashSet<ResolvedType>();
            pending.Push(this);
            while (pending.TryPop(out var type))
            {
                if (!visited.Add(type)) { continue; }
                if (type.Declaration?.Parts.FirstOrDefault()?.SyntaxKind == Cst.NodeKind.RefStructDeclaration) { return true; }
                if (type.Kind is ResolvedTypeKind.Nullable or ResolvedTypeKind.FixedArray or ResolvedTypeKind.Tuple)
                {
                    foreach (var argument in type.Arguments) { pending.Push(argument); }
                }
            }
            return false;
        }
    }
}

/// <summary>Canonical declaration and zero-based position, independent of a parameter's spelling.</summary>
public sealed record GenericParameterIdentity(AstDeclaration Owner, int Ordinal);
