// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using static OpenDot.Sol.Cst.SyntaxKind;

namespace OpenDot.Sol.Cst;

internal sealed partial class Parser
{
    private SyntaxNode ParseType()
    {
        int start = Start;
        if (!Enter()) { return Recover(start); }
        try
        {
            var children = new List<SyntaxNode>();
            if (Take(OpenBracketToken))
            {
                if (Kind == IntegerLiteralToken) { children.Add(ConsumeMarker(NodeKind.LiteralExpression)); }
                Expect(CloseBracketToken);
                children.Add(ParseType());
            }
            else if (Take(OpenParenToken))
            {
                bool passingMode = false;
                _suppressedNewlines++;
                try
                {
                    while (!AtEnd && Kind != CloseParenToken)
                    {
                        int before = _index;
                        if (Kind is RefKeyword or OutKeyword)
                        {
                            passingMode = true;
                            children.Add(ConsumeMarker(NodeKind.PassingMode));
                        }
                        children.Add(ParseType());
                        EnsureProgress(before);
                        if (!Take(CommaToken)) { break; }
                    }
                    Expect(CloseParenToken);
                    if (Take(ArrowToken))
                    {
                        children.Add(ParseType());
                        if (IsText("throws")) { children.Add(ParseThrows()); }
                    }
                    else if (passingMode) { Error("SOL1101", "Ref/out type modes require a function type."); }
                }
                finally { _suppressedNewlines--; }
            }
            else if (!Take(BangToken))
            {
                children.Add(ParseName());
                if (Kind == LessToken) { children.Add(ParseTypeArguments()); }
            }
            while (Take(QuestionToken)) { }
            var type = Node(NodeKind.Type, start, children);
            _typeEnded = true;
            return type;
        }
        finally { _depth--; }
    }

    private SyntaxNode ParseTypeArguments()
    {
        int start = Start;
        Expect(LessToken);
        _suppressedNewlines++;
        try
        {
            var types = new List<SyntaxNode>();
            if (IsTypeClose()) { Error("SOL1101", "A type argument list cannot be empty."); }
            while (!AtEnd && !IsTypeClose())
            {
                int before = _index;
                types.Add(ParseType());
                EnsureProgress(before);
                if (!Take(CommaToken)) { break; }
            }
            TakeTypeClose();
            return Node(NodeKind.TypeArgumentList, start, types);
        }
        finally { _suppressedNewlines--; }
    }

    private SyntaxNode ParseTypeParameters()
    {
        int start = Start;
        Expect(LessToken);
        var parameters = new List<SyntaxNode>();
        _suppressedNewlines++;
        try
        {
            if (IsTypeClose()) { Error("SOL1101", "A type parameter list cannot be empty."); }
            while (!AtEnd && !IsTypeClose())
            {
                int before = _index;
                int parameterStart = Start;
                var children = new List<SyntaxNode>();
                while (Kind == AtToken) { children.Add(ParseAnnotation()); }
                children.Add(ConsumeName());
                if (Take(ColonToken)) { children.Add(ParseType()); }
                parameters.Add(Node(NodeKind.TypeParameter, parameterStart, children));
                EnsureProgress(before);
                if (!Take(CommaToken)) { break; }
            }
            TakeTypeClose();
        }
        finally { _suppressedNewlines--; }
        return Node(NodeKind.TypeParameterList, start, parameters);
    }

    private bool IsTypeClose() => Kind is GreaterToken or GreaterGreaterToken or GreaterEqualsToken or GreaterGreaterEqualsToken;

    private void TakeTypeClose()
    {
        if (Take(GreaterToken)) { return; }
        if (Kind is GreaterGreaterToken or GreaterEqualsToken or GreaterGreaterEqualsToken)
        {
            cancellation.ThrowIfCancellationRequested();
            _lastEnd = Start + 1;
            _tokenOffset++;
            _typeEnded = true;
        }
        else { Expect(GreaterToken); }
    }

    private SyntaxNode ParseWhere()
    {
        int start = Start;
        Advance();
        var constraints = new List<SyntaxNode>();
        do
        {
            int constraintStart = Start;
            var children = new List<SyntaxNode> { ConsumeName() };
            Expect(ColonToken);
            if (IsText("allows"))
            {
                int allowsStart = Start;
                Advance();
                Expect(RefKeyword);
                Expect(StructKeyword);
                children.Add(Node(NodeKind.AntiConstraint, allowsStart, []));
            }
            else { children.Add(ParseType()); }
            constraints.Add(Node(NodeKind.Constraint, constraintStart, children));
        } while (Take(CommaToken));
        return Node(NodeKind.WhereClause, start, constraints);
    }

    private SyntaxNode ParseThrows()
    {
        int start = Start;
        Advance();
        var types = new List<SyntaxNode>();
        do { types.Add(ParseType()); } while (Take(CommaToken));
        return Node(NodeKind.ThrowsClause, start, types);
    }

    private bool LooksLikeGenericApplication()
    {
        int depth = 0;
        for (int distance = 0; distance < 4096; distance++)
        {
            cancellation.ThrowIfCancellationRequested();
            var kind = Peek(distance).Kind;
            if (kind == LessToken) { depth++; }
            else if (kind == GreaterToken) { depth--; }
            else if (kind == GreaterGreaterToken) { depth -= 2; }
            else if (kind is not (IdentifierToken or DotToken or CommaToken or OpenParenToken or CloseParenToken
                or OpenBracketToken or CloseBracketToken or IntegerLiteralToken or QuestionToken or BangToken
                or ArrowToken or RefKeyword or OutKeyword)) { return false; }
            if (depth < 0) { return false; }
            if (depth == 0) { return Peek(distance + 1).Kind is OpenParenToken or DotToken; }
        }
        Error("SOL1104", "The generic application lookahead budget was exceeded.");
        return false;
    }
}
