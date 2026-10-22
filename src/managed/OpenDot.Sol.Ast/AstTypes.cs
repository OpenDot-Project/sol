// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Ast;

public enum AstTypeKind
{
    Error,
    Named,
    Bottom,
    Nullable,
    Array,
    FixedArray,
    Tuple,
    Function,
}

/// <summary>Unresolved normalized type spelling, never a runtime or FIR type identity.</summary>
public sealed record TypeShape(
    AstTypeKind Kind,
    string Name,
    ImmutableArray<TypeShape> Arguments,
    ImmutableArray<AstPassingMode> ParameterModes,
    ImmutableArray<TypeShape> Effects)
{
    public static TypeShape Named(string name, params TypeShape[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(arguments);
        return new(AstTypeKind.Named, name, arguments.ToImmutableArray(), [], []);
    }

    public static TypeShape Nullable(TypeShape element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.Kind == AstTypeKind.Nullable ? element : new(AstTypeKind.Nullable, "", [element], [], []);
    }

    public string CanonicalName
    {
        get
        {
            var output = new StringBuilder();
            var pending = new Stack<object>();
            pending.Push(this);
            while (pending.TryPop(out var item))
            {
                if (item is string text)
                {
                    output.Append(text);
                    continue;
                }
                var shape = (TypeShape)item;
                var pieces = shape.Pieces().ToArray();
                for (int index = pieces.Length - 1; index >= 0; index--)
                {
                    pending.Push(pieces[index]);
                }
            }
            return output.ToString();
        }
    }

    private IEnumerable<object> Pieces()
    {
        switch (Kind)
        {
            case AstTypeKind.Named:
                yield return Name;
                if (!Arguments.IsEmpty)
                {
                    yield return "<";
                    foreach (var item in Separated(Arguments)) { yield return item; }
                    yield return ">";
                }
                break;
            case AstTypeKind.Bottom:
                yield return "!";
                break;
            case AstTypeKind.Nullable:
                yield return "(";
                foreach (var item in Separated(Arguments)) { yield return item; }
                yield return ")?";
                break;
            case AstTypeKind.Array:
                yield return "[]";
                foreach (var item in Separated(Arguments)) { yield return item; }
                break;
            case AstTypeKind.FixedArray:
                yield return "[" + Name + "]";
                foreach (var item in Separated(Arguments)) { yield return item; }
                break;
            case AstTypeKind.Tuple:
                yield return "(";
                foreach (var item in Separated(Arguments)) { yield return item; }
                if (Arguments.Length == 1) { yield return ","; }
                yield return ")";
                break;
            case AstTypeKind.Function:
                yield return "(";
                for (int index = 0; index < Arguments.Length - 1; index++)
                {
                    if (index != 0) { yield return ","; }
                    if (index < ParameterModes.Length && ParameterModes[index] != AstPassingMode.Value)
                    {
                        yield return ParameterModes[index].ToString().ToLowerInvariant() + " ";
                    }
                    yield return Arguments[index];
                }
                yield return ")->";
                if (!Arguments.IsEmpty) { yield return Arguments[^1]; }
                if (!Effects.IsEmpty)
                {
                    yield return " throws ";
                    foreach (var item in Separated(Effects)) { yield return item; }
                }
                break;
            default:
                yield return "<error>";
                break;
        }
    }

    private static IEnumerable<object> Separated(ImmutableArray<TypeShape> items)
    {
        for (int index = 0; index < items.Length; index++)
        {
            if (index != 0) { yield return ","; }
            yield return items[index];
        }
    }
}

public sealed class AstType : AstNode
{
    internal AstType(int id, AstOrigin origin, ImmutableArray<AstNode> children, TypeShape shape)
        : base(id, origin, children)
    {
        Shape = shape;
    }

    public TypeShape Shape { get; }
}

internal static class TypeNormalizer
{
    internal static TypeShape Create(AstOrigin origin, ImmutableArray<AstNode> children)
    {
        var ownTokens = origin.Element.Tokens.Where(token => !origin.Element.Children.Any(child =>
            child.Span.Start <= token.Span.Start && child.Span.End >= token.Span.End)).ToArray();
        var types = children.OfType<AstType>().Select(t => t.Shape).ToImmutableArray();
        TypeShape result;
        if (ownTokens.FirstOrDefault()?.Kind == SyntaxKind.OpenBracketToken)
        {
            var length = children.FirstOrDefault(n => n.SyntaxKind == NodeKind.LiteralExpression);
            result = new(length is null ? AstTypeKind.Array : AstTypeKind.FixedArray,
                length?.Origin.Text.Replace("_", "", StringComparison.Ordinal) ?? "", types, [], []);
        }
        else if (ownTokens.Any(t => t.Kind == SyntaxKind.ArrowToken))
        {
            var modes = new List<AstPassingMode>();
            var current = AstPassingMode.Value;
            foreach (var child in children)
            {
                if (child.SyntaxKind == NodeKind.PassingMode)
                {
                    current = child.Origin.Text == "ref" ? AstPassingMode.Ref : AstPassingMode.Out;
                }
                else if (child is AstType)
                {
                    modes.Add(current);
                    current = AstPassingMode.Value;
                }
            }
            if (modes.Count != 0) { modes.RemoveAt(modes.Count - 1); }
            result = new(AstTypeKind.Function, "", types, modes.ToImmutableArray(),
                children.FirstOrDefault(n => n.SyntaxKind == NodeKind.ThrowsClause)?.Children
                    .OfType<AstType>().Select(t => t.Shape).ToImmutableArray() ?? []);
        }
        else if (ownTokens.FirstOrDefault()?.Kind == SyntaxKind.OpenParenToken)
        {
            result = types.Length == 1 && !ownTokens.Any(t => t.Kind == SyntaxKind.CommaToken)
                ? types[0] : new(AstTypeKind.Tuple, "", types, [], []);
        }
        else if (ownTokens.Any(t => t.Kind == SyntaxKind.BangToken))
        {
            result = new(AstTypeKind.Bottom, "", [], [], []);
        }
        else
        {
            var name = children.FirstOrDefault(n => n.SyntaxKind == NodeKind.QualifiedName);
            var arguments = children.FirstOrDefault(n => n.SyntaxKind == NodeKind.TypeArgumentList);
            string spelling = name is null ? "" : string.Join('.', name.Children.Select(n => n.Origin.Text));
            result = new(string.IsNullOrEmpty(spelling) ? AstTypeKind.Error : AstTypeKind.Named,
                spelling, arguments?.Children.OfType<AstType>().Select(t => t.Shape).ToImmutableArray() ?? [], [], []);
        }
        if (ownTokens.LastOrDefault()?.Kind == SyntaxKind.QuestionToken)
        {
            result = TypeShape.Nullable(result);
        }
        return result;
    }
}
