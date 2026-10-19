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
    private SyntaxToken Current => lex.Tokens[_index];
    private SyntaxKind Kind => Current.Kind;
    private int Start => Current.Span.Start;
    private bool AtEnd => Kind == EndOfFileToken;

    public SyntaxTree Parse()
    {
        var children = new List<SyntaxNode>();
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

    private SyntaxNode ParseDeclaration()
    {
        int start = Start;
        if (!Enter()) { return Recover(start); }
        try
        {
            while (Kind is PublicKeyword or PrivateKeyword or InternalKeyword or ProtectedKeyword
                or PartialKeyword or FinalKeyword or OpenKeyword or AbstractKeyword or OverrideKeyword
                or SealedKeyword or OperatorKeyword or UnsafeKeyword)
            {
                Advance();
            }
            return Kind switch
            {
                ImportKeyword => ParseImport(start),
                ObjectKeyword or CompanionKeyword => ParseObject(start),
                FuncKeyword => ParseFunction(start),
                ValKeyword or VarKeyword or ConstKeyword => ParseBinding(start),
                _ => Unsupported(start),
            };
        }
        finally { _depth--; }
    }

    private SyntaxNode ParseImport(int start)
    {
        Advance();
        var children = new List<SyntaxNode>();
        if (Kind == IdentifierToken && Peek(1).Kind == EqualsToken)
        {
            children.Add(ConsumeName());
            Advance();
        }
        children.Add(ParseName());
        if (Take(ColonToken))
        {
            do
            {
                children.Add(ConsumeName());
                if (Take(EqualsToken)) { children.Add(ConsumeName()); }
            } while (Take(CommaToken));
        }
        Terminate();
        return Node(NodeKind.ImportDeclaration, start, children);
    }

    private SyntaxNode ParseObject(int start)
    {
        bool companion = Take(CompanionKeyword);
        Expect(ObjectKeyword);
        var children = new List<SyntaxNode>();
        if (!companion || Kind == IdentifierToken) { children.Add(ConsumeName()); }
        if (Kind != OpenBraceToken)
        {
            Error("SOL1102", "Object base lists are not implemented in this frontend build.");
            return Recover(start);
        }
        RequireSameLineBrace();
        Advance();
        while (!AtEnd && Kind != CloseBraceToken)
        {
            if (Take(SemicolonToken)) { continue; }
            int before = _index;
            children.Add(ParseDeclaration());
            EnsureProgress(before);
        }
        Expect(CloseBraceToken);
        Take(SemicolonToken);
        return Node(NodeKind.ObjectDeclaration, start, children);
    }

    private SyntaxNode ParseFunction(int start)
    {
        Advance();
        var children = new List<SyntaxNode> { ConsumeName(), ParseParameters() };
        if (Take(ColonToken)) { children.Add(ParseType()); }
        if (IsText("throws"))
        {
            Advance();
            do { children.Add(ParseType()); } while (Take(CommaToken));
        }
        if (Take(EqualsToken))
        {
            children.Add(ParseExpression());
            Terminate();
        }
        else if (Kind == OpenBraceToken)
        {
            RequireSameLineBrace();
            children.Add(ParseBlock());
        }
        else if (!Take(SemicolonToken))
        {
            Error("SOL1101", "Expected a function body.");
        }
        return Node(NodeKind.FunctionDeclaration, start, children);
    }

    private SyntaxNode ParseParameters()
    {
        int start = Start;
        var groups = new List<SyntaxNode>();
        Expect(OpenParenToken);
        while (!AtEnd && Kind != CloseParenToken)
        {
            int before = _index;
            int groupStart = Start;
            var names = new List<SyntaxNode>();
            if (Kind is RefKeyword or OutKeyword or VarargKeyword) { Advance(); }
            names.Add(ConsumeName());
            while (Kind == CommaToken && Peek(1).Kind == IdentifierToken)
            {
                Advance();
                names.Add(ConsumeName());
            }
            Expect(ColonToken);
            names.Add(ParseType());
            if (Take(EqualsToken)) { names.Add(ParseExpression()); }
            groups.Add(Node(NodeKind.ParameterGroup, groupStart, names));
            EnsureProgress(before);
            if (!Take(CommaToken)) { break; }
        }
        Expect(CloseParenToken);
        return Node(NodeKind.ParameterList, start, groups);
    }

    private SyntaxNode ParseType()
    {
        int start = Start;
        if (!Enter()) { return Recover(start); }
        try
        {
            var children = new List<SyntaxNode>();
            if (Take(OpenBracketToken))
            {
                if (Kind == IntegerLiteralToken) { Advance(); }
                Expect(CloseBracketToken);
                children.Add(ParseType());
            }
            else if (Take(OpenParenToken))
            {
                bool hasPassingMode = false;
                while (!AtEnd && Kind != CloseParenToken)
                {
                    if (Kind is RefKeyword or OutKeyword) { hasPassingMode = true; Advance(); }
                    children.Add(ParseType());
                    if (!Take(CommaToken)) { break; }
                }
                Expect(CloseParenToken);
                if (Take(ArrowToken)) { children.Add(ParseType()); }
                else if (hasPassingMode) { Error("SOL1101", "Ref/out type modes require a function type."); }
            }
            else if (!Take(BangToken))
            {
                children.Add(ParseName());
                if (Take(LessToken))
                {
                    do { children.Add(ParseType()); } while (Take(CommaToken) && Kind != GreaterToken);
                    // Adjacent generic closers require contextual token splitting, not lexical rewriting.
                    if (Kind == GreaterGreaterToken)
                    {
                        Error("SOL1102", "Adjacent generic closers are not implemented in this frontend build; the source is not accepted.");
                        Advance();
                    }
                    else { Expect(GreaterToken); }
                }
            }
            Take(QuestionToken);
            return Node(NodeKind.Type, start, children);
        }
        finally { _depth--; }
    }

    private SyntaxNode ParseBinding(int start, bool terminate = true, bool allowDestructuring = false)
    {
        if (Take(ConstKeyword)) { Expect(ValKeyword); }
        else { Advance(); }
        var children = new List<SyntaxNode> { allowDestructuring ? ParseBindingTarget() : ConsumeName() };
        if (Take(ColonToken)) { children.Add(ParseType()); }
        if (Take(EqualsToken)) { children.Add(ParseExpression()); }
        else if (IsText("by")) { Advance(); children.Add(ParseExpression()); }
        if (terminate) { Terminate(); }
        return Node(NodeKind.Binding, start, children);
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
            while (!AtEnd && Kind is not (CloseBraceToken or ElseKeyword or CatchKeyword or FinallyKeyword or CaseKeyword or DefaultKeyword))
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
        if (Kind is IdentifierToken or ThisKeyword or SuperKeyword)
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
        while (Take(DotToken)) { names.Add(ConsumeName()); }
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
        if (Boundary) { Error("SOL1103", "The opening brace must be on the same logical line as its header."); }
    }

    private void Terminate()
    {
        if (Take(SemicolonToken) || AtEnd || Kind == CloseBraceToken || Boundary) { return; }
        Error("SOL1101", "Expected a statement separator.");
    }

    private bool Boundary => _suppressedNewlines == 0 && _index > 0 && CanEnd(lex.Tokens[_index - 1].Kind)
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
    private void Advance() { cancellation.ThrowIfCancellationRequested(); if (!AtEnd) { _index++; } }
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
        var node = new SyntaxNode(kind,
            TextSpan.FromBounds(start, Math.Max(start, _index == 0 ? start : lex.Tokens[_index - 1].Span.End)), items);
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
