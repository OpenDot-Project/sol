// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Diagnostics;
using OpenDot.Sol.Text;
using static OpenDot.Sol.Cst.SyntaxKind;

namespace OpenDot.Sol.Cst;

/// <summary>Initial declaration/expression parser. Unsupported productions fail explicitly.</summary>
internal sealed partial class Parser(LexResult lex, int errorLimit, int maxDepth, CancellationToken cancellation)
{
    private readonly List<Diagnostic> _diagnostics = [.. lex.Diagnostics];
    private readonly Dictionary<SyntaxNode, int> _nodeDepths = new(ReferenceEqualityComparer.Instance);
    private int _index;
    private int _depth;
    private int _suppressedNewlines;
    private int _tokenOffset;
    private bool _typeEnded;
    private int _lastEnd;
    private SyntaxToken Current => _tokenOffset == 0 ? lex.Tokens[_index] : new(
        lex.Tokens[_index].Kind switch
        {
            GreaterGreaterToken => GreaterToken,
            GreaterGreaterEqualsToken when _tokenOffset == 1 => GreaterEqualsToken,
            _ => EqualsToken,
        }, new(lex.Tokens[_index].Span.Start + _tokenOffset, lex.Tokens[_index].Span.Length - _tokenOffset), []);
    private SyntaxKind Kind => Current.Kind;
    private int Start => Current.Span.Start;
    private bool AtEnd => Kind == EndOfFileToken;

    public SyntaxTree Parse()
    {
        var children = new List<SyntaxNode>();
        while (Kind == AtToken) { children.Add(ParseAnnotation(file: true)); Terminate(); }
        string? moduleName = null;
        if (Kind == ModuleKeyword)
        {
            int start = Start;
            Advance();
            var name = ParseName();
            if (name.Children.Length != 0)
            {
                moduleName = string.Join('.', name.Children.Select(n => lex.Source.GetText(n.Span)));
            }
            Terminate();
            children.Add(Node(NodeKind.ModuleDeclaration, start, [name]));
        }
        else { Error("SOL1100", "A source file must begin with an explicit module declaration."); }
        while (!AtEnd)
        {
            if (Take(SemicolonToken)) { continue; }
            int before = _index;
            children.Add(ParseDeclaration());
            EnsureProgress(before);
        }
        var root = new SyntaxNode(NodeKind.CompilationUnit, new(0, lex.Source.Length), children.ToImmutableArray());
        return new(lex.Source, root, lex.Tokens,
            _diagnostics.OrderBy(d => d.Span.Start).ThenBy(d => d.Code, StringComparer.Ordinal).ToImmutableArray(), moduleName);
    }

    private SyntaxNode ParseBlock()
    {
        int start = Start;
        if (!Enter()) { return Recover(start); }
        int previousSuppression = _suppressedNewlines;
        _suppressedNewlines = 0;
        try
        {
            Expect(OpenBraceToken);
            var statements = new List<SyntaxNode>();
            while (!AtEnd && Kind is not (CloseBraceToken or ElseKeyword or CatchKeyword or FinallyKeyword or CaseKeyword)
                && !(Kind == DefaultKeyword && Peek(1).Kind != LessToken))
            {
                if (Take(SemicolonToken)) { continue; }
                int before = _index;
                statements.Add(ParseStatement());
                EnsureProgress(before);
            }
            Expect(CloseBraceToken);
            return Node(NodeKind.Block, start, statements);
        }
        finally { _depth--; _suppressedNewlines = previousSuppression; }
    }

    private SyntaxNode ParseStatement()
    {
        int start = Start;
        if (!Enter()) { return Recover(start); }
        try { return ParseStatementCore(); }
        finally { _depth--; }
    }

    private SyntaxNode ParseStatementCore()
    {
        int start = Start;
        switch (Kind)
        {
            case IfKeyword: return ParseIf();
            case ForKeyword: return ParseFor();
            case SwitchKeyword: return ParseSwitch();
            case TryKeyword: return ParseTry(NodeKind.TryStatement);
            case DeferKeyword: return ParseCleanup(NodeKind.DeferStatement);
            case UnsafeKeyword: return ParseCleanup(NodeKind.UnsafeStatement);
            case BreakKeyword:
            case ContinueKeyword:
                var kind = Kind == BreakKeyword ? NodeKind.BreakStatement : NodeKind.ContinueStatement;
                Advance();
                Terminate();
                return Node(kind, start, []);
        }
        if (Kind is ValKeyword or VarKeyword or ConstKeyword) { return ParseBinding(start, allowDestructuring: true); }
        if (Kind == FuncKeyword) { return ParseFunction(start); }
        if (Kind is ReturnKeyword or ThrowKeyword)
        {
            bool throwing = Kind == ThrowKeyword;
            Advance();
            var children = new List<SyntaxNode>();
            if (!throwing && !Boundary && Take(AtToken)) { children.Add(ConsumeName()); }
            if (throwing || (!Boundary && Kind is not (CloseBraceToken or EndOfFileToken or SemicolonToken)))
            {
                if (!throwing && Kind == RefKeyword)
                {
                    int refStart = Start;
                    Advance();
                    children.Add(Node(NodeKind.RefExpression, refStart, [ParseExpression()]));
                }
                else { children.Add(ParseExpression()); }
            }
            Terminate();
            return Node(throwing ? NodeKind.ThrowStatement : NodeKind.ReturnStatement, start, children);
        }
        var expression = ParseExpression();
        if (!Boundary && IsAssignment(Kind))
        {
            Advance();
            var value = ParseExpression();
            Terminate();
            return Node(NodeKind.Assignment, start, [expression, value]);
        }
        Terminate();
        return Node(NodeKind.ExpressionStatement, start, [expression]);
    }

    private SyntaxNode ParseExpression(int minimum = 0, bool allowTrailingLambda = true)
    {
        int start = Start;
        if (!Enter()) { return Recover(start); }
        try
        {
            SyntaxNode left;
            if (Kind is PlusToken or MinusToken or BangToken or TildeToken or PlusPlusToken or MinusMinusToken)
            {
                Advance();
                left = Node(NodeKind.UnaryExpression, start, [ParseExpression(14, allowTrailingLambda)]);
            }
            else { left = ParseAtom(); }
            while (!AtEnd && !Boundary)
            {
                if (Kind == LessToken && left.Kind is NodeKind.NameExpression or NodeKind.MemberExpression
                    && LooksLikeGenericApplication())
                {
                    left = Node(NodeKind.GenericExpression, start, [left, ParseTypeArguments()]);
                    continue;
                }
                if (Kind == OpenParenToken)
                {
                    left = Node(NodeKind.CallExpression, start, [left, ParseArguments()]);
                    continue;
                }
                if (allowTrailingLambda && Kind == OpenBraceToken && left.Kind == NodeKind.CallExpression
                    && left.Children.Length == 2)
                {
                    left = Node(NodeKind.CallExpression, start, left.Children.Add(ParseLambda()));
                    continue;
                }
                if (allowTrailingLambda && Kind == OpenBraceToken
                    && left.Kind is NodeKind.NameExpression or NodeKind.MemberExpression or NodeKind.GenericExpression)
                {
                    left = Node(NodeKind.CallExpression, start, [left, ParseLambda()]);
                    continue;
                }
                if (Kind is DotToken or QuestionDotToken)
                {
                    Advance();
                    left = Node(NodeKind.MemberExpression, start, [left, ConsumeName()]);
                    continue;
                }
                if (Kind == OpenBracketToken)
                {
                    left = Node(NodeKind.IndexExpression, start, [left, ParseSequence(OpenBracketToken, CloseBracketToken, NodeKind.ArgumentList)]);
                    continue;
                }
                if (Kind is BangBangToken or PlusPlusToken or MinusMinusToken)
                {
                    Advance();
                    left = Node(NodeKind.UnaryExpression, start, [left]);
                    continue;
                }
                if (minimum <= 11 && (Kind is AsKeyword or IsKeyword || Kind == BangToken && Peek(1).Kind == IsKeyword))
                {
                    bool cast = Kind == AsKeyword;
                    if (Take(BangToken)) { Expect(IsKeyword); }
                    else { Advance(); }
                    if (cast) { Take(QuestionToken); }
                    left = Node(cast ? NodeKind.CastExpression : NodeKind.TypeTestExpression, start, [left, ParseType()]);
                    continue;
                }
                int precedence = Precedence(Kind);
                if (precedence < minimum || precedence < 0) { break; }
                bool rightAssociative = Kind == QuestionColonToken;
                Advance();
                left = Node(NodeKind.BinaryExpression, start, [left, ParseExpression(precedence + (rightAssociative ? 0 : 1), allowTrailingLambda)]);
            }
            return left;
        }
        finally { _depth--; }
    }

    private SyntaxNode ParseAtom()
    {
        int start = Start;
        if (Kind == TryKeyword) { return ParseTry(NodeKind.TryExpression); }
        if (Take(ThrowKeyword)) { return Node(NodeKind.ThrowExpression, start, [ParseExpression()]); }
        if (Kind == FuncKeyword) { return ParseAnonymousFunction(); }
        if (Kind == IdentifierToken && Peek(1).Kind == AtToken)
        {
            var label = ConsumeName();
            Advance();
            if (Kind != OpenBraceToken) { Error("SOL1101", "A label must introduce a lambda."); }
            return Node(NodeKind.LabelledExpression, start, [label, ParseLambda()]);
        }
        if (Kind is IntegerLiteralToken or FloatLiteralToken or StringLiteralToken or RuneLiteralToken
            or TrueKeyword or FalseKeyword or NullKeyword)
        {
            Advance();
            return Node(NodeKind.LiteralExpression, start, []);
        }
        if (Kind is IdentifierToken or ThisKeyword or SuperKeyword || Kind == DefaultKeyword && Peek(1).Kind == LessToken)
        {
            Advance();
            return Node(NodeKind.NameExpression, start, []);
        }
        if (Take(PositionalParameterToken)) { return Node(NodeKind.PositionalExpression, start, []); }
        if (Kind == OpenParenToken) { return ParseSequence(OpenParenToken, CloseParenToken, NodeKind.TupleExpression); }
        if (Kind == OpenBracketToken) { return ParseSequence(OpenBracketToken, CloseBracketToken, NodeKind.ArrayExpression); }
        if (Kind == OpenBraceToken) { return ParseLambda(); }
        Error("SOL1101", "Expected an expression; this production is missing or not yet implemented.");
        if (!AtEnd && Kind is not (CloseParenToken or CloseBracketToken or CloseBraceToken)) { Advance(); }
        return Node(NodeKind.Error, start, []);
    }

    private SyntaxNode ParseSequence(SyntaxKind open, SyntaxKind close, NodeKind kind)
    {
        _suppressedNewlines++;
        try
        {
            int start = Start;
            Expect(open);
            var children = new List<SyntaxNode>();
            while (!AtEnd && Kind != close)
            {
                int before = _index;
                children.Add(ParseExpression());
                EnsureProgress(before);
                if (!Take(CommaToken)) { break; }
            }
            Expect(close);
            return Node(kind, start, children);
        }
        finally { _suppressedNewlines--; }
    }

    private SyntaxNode ParseName()
    {
        int start = Start;
        var names = new List<SyntaxNode> { ConsumeName() };
        while (!Boundary && Take(DotToken)) { names.Add(ConsumeName()); }
        return Node(NodeKind.QualifiedName, start, names);
    }

    private SyntaxNode ConsumeName()
    {
        int start = Start;
        Expect(IdentifierToken);
        return Node(NodeKind.Name, start, []);
    }

    private void RequireSameLineBrace()
    {
        if (HasLeadingNewline()) { Error("SOL1103", "The opening brace must be on the same logical line as its header."); }
    }

    private void Terminate()
    {
        if (Take(SemicolonToken) || AtEnd || Kind == CloseBraceToken || Boundary) { return; }
        Error("SOL1101", "Expected a statement separator.");
    }

    private bool Boundary => _suppressedNewlines == 0 && _index > 0 && (_typeEnded || CanEnd(lex.Tokens[_index - 1].Kind))
        && Current.LeadingTrivia.Any(t => t.Kind == TriviaKind.Newline
            || (t.Kind == TriviaKind.BlockComment && HasNewline(t.Span)));

    private bool HasNewline(TextSpan span) => lex.Source.Slice(span).IndexOfAny((byte)'\r', (byte)'\n') >= 0;
    private static bool CanEnd(SyntaxKind kind) => kind is IdentifierToken or IntegerLiteralToken
        or FloatLiteralToken or StringLiteralToken or RuneLiteralToken or TrueKeyword or FalseKeyword
        or NullKeyword or BreakKeyword or ContinueKeyword or ReturnKeyword or CloseParenToken
        or CloseBracketToken or CloseBraceToken or BangBangToken or PositionalParameterToken
        or ThisKeyword or SuperKeyword or PlusPlusToken or MinusMinusToken;

    private bool Enter()
    {
        cancellation.ThrowIfCancellationRequested();
        if (_depth >= maxDepth)
        {
            Error("SOL1104", "The parser nesting limit was exceeded.");
            return false;
        }
        _depth++;
        return true;
    }

    private SyntaxNode Unsupported(int start)
    {
        Error("SOL1102", "This declaration is not implemented in this frontend build.");
        return Recover(start);
    }

    private SyntaxNode Recover(int start)
    {
        if (!AtEnd) { Advance(); }
        return Node(NodeKind.Error, start, []);
    }

    private void EnsureProgress(int before) { if (_index == before && !AtEnd) { Advance(); } }
    private bool IsText(string text) => Kind == IdentifierToken && lex.Source.GetText(Current.Span) == text;
    private SyntaxToken Peek(int distance) => PeekAbsolute(_index + distance);
    private SyntaxToken PeekAbsolute(int index) => lex.Tokens[Math.Min(index, lex.Tokens.Length - 1)];
    private void Advance()
    {
        cancellation.ThrowIfCancellationRequested();
        _lastEnd = Current.Span.End;
        _typeEnded = false;
        if (!AtEnd) { _tokenOffset = 0; _index++; }
    }
    private bool Take(SyntaxKind kind) { if (Kind != kind) { return false; } Advance(); return true; }
    private void Expect(SyntaxKind kind) { if (!Take(kind)) { Error("SOL1101", $"Expected {kind}."); } }
    private void Error(string code, string message)
    {
        if (_diagnostics.Count < errorLimit) { _diagnostics.Add(new(code, message, lex.Source, Current.Span)); }
    }
    private SyntaxNode Node(NodeKind kind, int start, IEnumerable<SyntaxNode> children)
    {
        var items = children.ToImmutableArray();
        int depth = 1;
        foreach (var child in items) { depth = Math.Max(depth, _nodeDepths.GetValueOrDefault(child, 1) + 1); }
        if (depth > maxDepth)
        {
            Error("SOL1104", "The concrete syntax tree depth limit was exceeded.");
            kind = NodeKind.Error;
            items = [];
            depth = 1;
        }
        int end = Math.Max(start, _lastEnd);
        foreach (var child in items) { end = Math.Max(end, child.Span.End); }
        var node = new SyntaxNode(kind, TextSpan.FromBounds(start, end), items);
        _nodeDepths.Add(node, depth);
        return node;
    }

    private static int Precedence(SyntaxKind kind) => kind switch
    {
        QuestionColonToken => 0,
        PipePipeToken => 1,
        AmpersandAmpersandToken => 2,
        EqualsEqualsToken or BangEqualsToken or EqualsEqualsEqualsToken or BangEqualsEqualsToken => 3,
        LessToken or LessEqualsToken or GreaterToken or GreaterEqualsToken => 4,
        PipeToken => 5,
        CaretToken => 6,
        AmpersandToken => 7,
        LessLessToken or GreaterGreaterToken => 8,
        PlusToken or MinusToken => 9,
        StarToken or SlashToken or PercentToken => 10,
        _ => -1,
    };
}
