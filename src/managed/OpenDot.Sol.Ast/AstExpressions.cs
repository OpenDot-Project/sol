// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Ast;

public class AstExpression : AstNode
{
    internal AstExpression(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }
}

public sealed class AstAtom : AstExpression
{
    internal AstAtom(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public string Spelling => Origin.Text;
}

public sealed class AstOperation : AstExpression
{
    internal AstOperation(int id, AstOrigin origin, ImmutableArray<AstNode> children, string operation)
        : base(id, origin, children)
    {
        Operation = operation;
    }

    public string Operation { get; }
    public IEnumerable<AstExpression> Operands => Children.OfType<AstExpression>();
    public AstType? TargetType => Children.OfType<AstType>().FirstOrDefault();
    public bool IsPostfix => SyntaxKind == NodeKind.UnaryExpression && Children.Length != 0
        && Origin.Span.Start == Children[0].Origin.Span.Start;
    public bool IsShortCircuit => Operation is "&&" or "||" or "?:";
}

public enum AstArgumentMode
{
    Value,
    Ref,
    Out,
    OutVariable,
    Spread,
}

public sealed record AstArgument(
    string? Name,
    AstArgumentMode Mode,
    AstNode Value,
    AstOrigin Origin,
    bool IsTrailingLambda = false);

public sealed class AstCall : AstExpression
{
    internal AstCall(int id, AstOrigin origin, ImmutableArray<AstNode> children,
        ImmutableArray<AstArgument> arguments)
        : base(id, origin, children)
    {
        Arguments = arguments;
    }

    public AstExpression? Target => Children.FirstOrDefault() as AstExpression;
    public ImmutableArray<AstArgument> Arguments { get; }
}

public sealed class AstMember : AstExpression
{
    internal AstMember(int id, AstOrigin origin, ImmutableArray<AstNode> children, bool safe)
        : base(id, origin, children)
    {
        IsSafe = safe;
    }

    public AstExpression? Receiver => Children.FirstOrDefault() as AstExpression;
    public string? Name => Children.FirstOrDefault(n => n.SyntaxKind == NodeKind.Name)?.Origin.Text;
    public bool IsSafe { get; }
}

public sealed class AstSequence : AstExpression
{
    internal AstSequence(int id, AstOrigin origin, ImmutableArray<AstNode> children, bool grouped)
        : base(id, origin, children)
    {
        IsGrouping = grouped;
    }

    public bool IsGrouping { get; }
    public IEnumerable<AstExpression> Elements => Children.OfType<AstExpression>();
}

public enum AstLambdaForm
{
    Headerless,
    Named,
    Positional,
}

public sealed class AstLambda : AstExpression
{
    internal AstLambda(int id, AstOrigin origin, ImmutableArray<AstNode> children,
        AstLambdaForm form, ImmutableArray<int> positions, bool unrepresentablePosition)
        : base(id, origin, children)
    {
        Form = form;
        Positions = positions;
        HasUnrepresentablePosition = unrepresentablePosition;
    }

    public AstLambdaForm Form { get; }
    public ImmutableArray<int> Positions { get; }
    public bool HasUnrepresentablePosition { get; }
    public int MinimumPositionalArity => Positions.IsEmpty ? 0 : Positions[^1] + 1;
    public IEnumerable<AstNode> Parameters => Child(NodeKind.LambdaParameters)?.Children ?? [];
    public IEnumerable<AstNode> Statements => Children.Where(n => n.SyntaxKind != NodeKind.LambdaParameters);
    public AstExpression? TailExpression => (Statements.LastOrDefault() as AstExpressionStatement)?.Expression;
}

/// <summary>A source-level anonymous function expression with its own explicit callable signature.</summary>
public sealed class AstAnonymousFunction : AstExpression
{
    /// <param name="Parameters">Flattened parameters while retaining their original group origins.</param>
    /// <param name="ReturnType">The explicit result type, or null when inferred by later analysis.</param>
    /// <param name="ThrownTypes">The ordered checked-effect types declared by the expression.</param>
    /// <param name="Body">The expression body node and kind; it is missing when syntax recovery found no body.</param>
    internal AstAnonymousFunction(int id, AstOrigin origin, ImmutableArray<AstNode> children,
        ImmutableArray<AstParameter> parameters, AstType? returnType,
        ImmutableArray<AstType> thrownTypes, AstBody body)
        : base(id, origin, children)
    {
        Parameters = parameters;
        ReturnType = returnType;
        ThrownTypes = thrownTypes;
        Body = body;
    }

    public ImmutableArray<AstParameter> Parameters { get; }
    public AstType? ReturnType { get; }
    public ImmutableArray<AstType> ThrownTypes { get; }
    public AstBody Body { get; }
}

public sealed class AstGenericApplication : AstExpression
{
    internal AstGenericApplication(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public AstExpression? Target => Children.FirstOrDefault() as AstExpression;
    public IEnumerable<AstType> TypeArguments => Child(NodeKind.TypeArgumentList)?.Children.OfType<AstType>() ?? [];
}

public sealed class AstLabelledExpression : AstExpression
{
    internal AstLabelledExpression(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public string? Label => Child(NodeKind.Name)?.Origin.Text;
    public AstLambda? Lambda => Children.OfType<AstLambda>().FirstOrDefault();
}
