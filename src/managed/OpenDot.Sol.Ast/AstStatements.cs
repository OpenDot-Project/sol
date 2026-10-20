// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Ast;

public class AstStatement : AstNode
{
    internal AstStatement(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }
}

public sealed class AstBlock : AstStatement
{
    internal AstBlock(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public ImmutableArray<AstNode> Statements => Children;
}

public sealed class AstExpressionStatement : AstStatement
{
    internal AstExpressionStatement(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public AstExpression? Expression => Children.FirstOrDefault() as AstExpression;
}

public sealed class AstReturn : AstStatement
{
    internal AstReturn(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public string? Label => Child(NodeKind.Name)?.Origin.Text;
    public AstExpression? Value => Children.OfType<AstExpression>().FirstOrDefault();
    public bool IsByReference => Children.Any(n => n.SyntaxKind == NodeKind.RefExpression);
}

public sealed class AstAssignment : AstStatement
{
    internal AstAssignment(int id, AstOrigin origin, ImmutableArray<AstNode> children, string operation)
        : base(id, origin, children)
    {
        Operation = operation;
    }

    public string Operation { get; }
    public AstExpression? Location => Children.ElementAtOrDefault(0) as AstExpression;
    public AstExpression? Value => Children.ElementAtOrDefault(1) as AstExpression;
}

public sealed class AstIf : AstStatement
{
    internal AstIf(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public AstExpression? Condition => Children.ElementAtOrDefault(0) as AstExpression;
    public AstBlock? Then => Children.ElementAtOrDefault(1) as AstBlock;
    public AstNode? Else => Children.ElementAtOrDefault(2);
}

public enum AstForForm
{
    Infinite,
    Condition,
    ThreeClause,
    Each,
}

public sealed class AstFor : AstStatement
{
    internal AstFor(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public AstForForm Form => Child(NodeKind.ForEachBinding) is not null ? AstForForm.Each
        : Child(NodeKind.ForInitializer) is not null ? AstForForm.ThreeClause
        : Child(NodeKind.ForCondition) is not null ? AstForForm.Condition : AstForForm.Infinite;
    public AstBlock? Body => Children.OfType<AstBlock>().LastOrDefault();
    public AstNode? Initializer => Child(NodeKind.ForInitializer)?.Children.FirstOrDefault();
    public AstExpression? Condition => Child(NodeKind.ForCondition)?.Children.OfType<AstExpression>().FirstOrDefault();
    public AstAssignment? Update => Child(NodeKind.ForUpdate)?.Children.OfType<AstAssignment>().FirstOrDefault();
    public AstProperty? Binding => Child(NodeKind.ForEachBinding)?.Children.OfType<AstProperty>().FirstOrDefault();
    public AstExpression? Iterable => Form == AstForForm.Each ? Children.OfType<AstExpression>().FirstOrDefault() : null;
}

public sealed class AstSwitch : AstStatement
{
    internal AstSwitch(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public AstExpression? Value => Children.OfType<AstExpression>().FirstOrDefault();
    public IEnumerable<AstCase> Cases => Children.OfType<AstCase>();
}

public sealed class AstCase : AstStatement
{
    internal AstCase(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public bool IsDefault => Origin.Element.Tokens.FirstOrDefault()?.Kind == OpenDot.Sol.Cst.SyntaxKind.DefaultKeyword;
    public AstNode? Pattern => IsDefault ? null : Children.FirstOrDefault();
    public IEnumerable<AstNode> Statements => IsDefault ? Children : Children.Skip(1);
}

public sealed class AstTry : AstExpression
{
    internal AstTry(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public bool IsValue => SyntaxKind == NodeKind.TryExpression;
    public AstBlock? Body => Children.OfType<AstBlock>().FirstOrDefault();
    public IEnumerable<AstCatch> Catches => Children.OfType<AstCatch>();
    public AstBlock? Finally => Child(NodeKind.FinallyClause)?.Children.OfType<AstBlock>().FirstOrDefault();
}

public sealed class AstCatch : AstStatement
{
    internal AstCatch(int id, AstOrigin origin, ImmutableArray<AstNode> children)
        : base(id, origin, children) { }

    public string? Name => Child(NodeKind.Name)?.Origin.Text;
    public AstType? ExceptionType => Children.OfType<AstType>().FirstOrDefault();
    public AstBlock? Body => Children.OfType<AstBlock>().FirstOrDefault();
}
