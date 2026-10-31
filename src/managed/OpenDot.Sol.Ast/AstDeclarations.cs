// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;

namespace OpenDot.Sol.Ast;

public abstract class AstDeclaration : AstNode
{
    internal AstDeclaration(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public string? Name => (Origin.Element as PsiDeclaration)?.Name;
    public ImmutableArray<string> Modifiers => Origin.Element.Children
        .Where(n => n.Kind == NodeKind.Modifier).Select(n => n.Text).ToImmutableArray();
    public bool IsPartial => Modifiers.Contains("partial");
    public bool IsPublic => !Modifiers.Any(m => m is "private" or "internal" or "protected");
    public IEnumerable<AstNode> Annotations => Children.Where(n => n.SyntaxKind == NodeKind.Annotation);
}

public sealed class AstTypeDeclaration : AstDeclaration
{
    internal AstTypeDeclaration(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public IEnumerable<AstDeclaration> Members => Child(NodeKind.TypeBody)?.Children.OfType<AstDeclaration>() ?? [];
    public IEnumerable<AstTypeParameter> TypeParameters => Child(NodeKind.TypeParameterList)?.Children.OfType<AstTypeParameter>() ?? [];
    public int Arity => TypeParameters.Count();
    public IEnumerable<AstNode> Bases => Child(NodeKind.BaseList)?.Children ?? [];
    public AstConstructor? PrimaryConstructor => Children.OfType<AstConstructor>()
        .FirstOrDefault(n => n.SyntaxKind == NodeKind.PrimaryConstructor);
    public bool IsCompanion => SyntaxKind == NodeKind.ObjectDeclaration
        && Origin.Element.Tokens.TakeWhile(t => t.Kind != OpenDot.Sol.Cst.SyntaxKind.ObjectKeyword)
        .Any(t => t.Kind == OpenDot.Sol.Cst.SyntaxKind.CompanionKeyword);
    public bool HasInitialization => PrimaryConstructor is not null || Members.Any(member => member switch
    {
        AstConstructor => true,
        AstInitializer => true,
        AstProperty property => property.Initializer is not null || property.Delegate is not null,
        _ => false,
    });
}

public sealed class AstTypeParameter : AstNode
{
    internal AstTypeParameter(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public string? Name => ((PsiTypeParameter)Origin.Element).Name;
    public AstType? Constraint => Children.OfType<AstType>().FirstOrDefault();
}

public enum AstPassingMode
{
    Value,
    Ref,
    Out,
    Vararg,
}

/// <summary>One parameter, expanded from a source group while retaining the shared group origin.</summary>
public sealed record AstParameter(
    string Name,
    AstType? Type,
    AstPassingMode PassingMode,
    AstNode? DefaultValue,
    bool IsProperty,
    bool IsMutable,
    AstOrigin NameOrigin,
    AstOrigin GroupOrigin)
{
    /// <summary>Annotations retained from the source parameter group.</summary>
    public ImmutableArray<AstNode> Annotations { get; init; } = [];

    /// <summary>Whether the source declares that this function-valued parameter may escape.</summary>
    public bool IsEscaping => Annotations.Any(annotation => annotation.Origin.Element is PsiAnnotation
        { Name: "escaping" });
}

public abstract class AstCallable : AstDeclaration
{
    internal AstCallable(int id, AstOrigin origin, ImmutableArray<AstNode> children,
        ImmutableArray<AstParameter> parameters)
        : base(id, origin, children)
    {
        Parameters = parameters;
    }

    public ImmutableArray<AstParameter> Parameters { get; }
    public IEnumerable<AstType> ThrownTypes => Child(NodeKind.ThrowsClause)?.Children.OfType<AstType>() ?? [];
    public IEnumerable<AstTypeParameter> TypeParameters => Child(NodeKind.TypeParameterList)?.Children.OfType<AstTypeParameter>() ?? [];
    public AstType? ReturnType => Child(NodeKind.ReturnType)?.Children.OfType<AstType>().FirstOrDefault();
    public bool ReturnsByReference => Child(NodeKind.ReturnType)?.Children
        .Any(n => n.SyntaxKind == NodeKind.PassingMode && n.Origin.Text == "ref") == true;
}

public sealed class AstFunction : AstCallable
{
    internal AstFunction(int id, AstOrigin origin, ImmutableArray<AstNode> children,
        ImmutableArray<AstParameter> parameters, AstBody body)
        : base(id, origin, children, parameters)
    {
        Body = body;
    }

    public AstBody Body { get; }
    public bool IsCheckedOperator => ((PsiFunction)Origin.Element).IsCheckedOperator;
}

public sealed class AstConstructor : AstCallable
{
    internal AstConstructor(int id, AstOrigin origin, ImmutableArray<AstNode> children,
        ImmutableArray<AstParameter> parameters)
        : base(id, origin, children, parameters) { }

    public AstBlock? Body => Children.OfType<AstBlock>().FirstOrDefault();
    public AstNode? Delegation => Child(NodeKind.ConstructorDelegation);
}

public sealed class AstProperty : AstDeclaration
{
    internal AstProperty(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public AstType? Type => Children.OfType<AstType>().FirstOrDefault();
    public bool IsMutable => ((PsiProperty)Origin.Element).IsMutable;
    public bool IsConstant => Origin.Element.Tokens.Any(t => t.Kind == OpenDot.Sol.Cst.SyntaxKind.ConstKeyword
        && t.Span.Start < ((PsiProperty)Origin.Element).NameElement?.Span.Start);
    public AstExpression? Initializer => Children.OfType<AstExpression>().LastOrDefault();
    public AstExpression? Delegate => Child(NodeKind.Delegation)?.Children.OfType<AstExpression>().FirstOrDefault();
    public IEnumerable<AstAccessor> Accessors => Child(NodeKind.AccessorList)?.Children.OfType<AstAccessor>() ?? [];
    public IEnumerable<string> BindingNames => Origin.Element.Children
        .Where(n => n.Kind is NodeKind.Name or NodeKind.Destructuring)
        .SelectMany(n => n.Kind == NodeKind.Name ? [n.Text] : n.Children.Select(c => c.Text));
}

public sealed class AstAccessor : AstCallable
{
    internal AstAccessor(int id, AstOrigin origin, ImmutableArray<AstNode> children, AstBody body)
        : base(id, origin, children, [])
    {
        Body = body;
    }

    public AstBody Body { get; }
}

public sealed class AstIndexer : AstCallable
{
    internal AstIndexer(int id, AstOrigin origin, ImmutableArray<AstNode> children,
        ImmutableArray<AstParameter> parameters)
        : base(id, origin, children, parameters) { }

    public AstType? ValueType => Children.OfType<AstType>().FirstOrDefault();
    public IEnumerable<AstAccessor> Accessors => Child(NodeKind.AccessorList)?.Children.OfType<AstAccessor>() ?? [];
}

public sealed class AstInitializer : AstDeclaration
{
    internal AstInitializer(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public AstBlock? Body => Children.OfType<AstBlock>().FirstOrDefault();
}

public sealed class AstEnumVariant : AstCallable
{
    internal AstEnumVariant(int id, AstOrigin origin, ImmutableArray<AstNode> children,
        ImmutableArray<AstParameter> parameters)
        : base(id, origin, children, parameters) { }
}

public sealed class AstExtern : AstDeclaration
{
    internal AstExtern(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public IEnumerable<AstFunction> Functions => Child(NodeKind.TypeBody)?.Children.OfType<AstFunction>() ?? [];
}
