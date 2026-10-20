// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using static OpenDot.Sol.Cst.SyntaxKind;

namespace OpenDot.Sol.Cst;

internal sealed partial class Parser
{
    private SyntaxNode ParseIf()
    {
        int start = Start;
        Advance();
        var children = new List<SyntaxNode> { ParseExpression(allowTrailingLambda: false), ParseBody() };
        if (Kind == ElseKeyword)
        {
            if (HasLeadingNewline()) { Error("SOL1103", "The else clause must follow its closing brace on the same logical line."); }
            Advance();
            if (Kind == IfKeyword) { children.Add(ParseStatement()); }
            else { children.Add(ParseBody()); }
        }
        return Node(NodeKind.IfStatement, start, children);
    }

    private SyntaxNode ParseFor()
    {
        int start = Start;
        Advance();
        var children = new List<SyntaxNode>();
        if (Kind is ValKeyword or VarKeyword)
        {
            int bindingStart = Start;
            var binding = ParseBinding(bindingStart, terminate: false, allowDestructuring: true);
            if (Take(InKeyword))
            {
                if (binding.Children.Length != 1) { Error("SOL1101", "An iteration binding cannot have a type, initializer, or delegate."); }
                children.Add(Node(NodeKind.ForEachBinding, bindingStart, [binding]));
                children.Add(ParseExpression(allowTrailingLambda: false));
            }
            else
            {
                children.Add(Node(NodeKind.ForInitializer, bindingStart, [binding]));
                RequireForSeparator();
                int conditionStart = Start;
                children.Add(Node(NodeKind.ForCondition, conditionStart, [ParseExpression(allowTrailingLambda: false)]));
                RequireForSeparator();
                int updateStart = Start;
                var location = ParseExpression(allowTrailingLambda: false);
                if (IsAssignment(Kind)) { Advance(); }
                else { Error("SOL1101", "The final for clause requires an assignment."); }
                children.Add(Node(NodeKind.ForUpdate, updateStart,
                    [Node(NodeKind.Assignment, updateStart, [location, ParseExpression(allowTrailingLambda: false)])]));
            }
        }
        else if (Kind != OpenBraceToken)
        {
            int conditionStart = Start;
            children.Add(Node(NodeKind.ForCondition, conditionStart, [ParseExpression(allowTrailingLambda: false)]));
        }
        children.Add(ParseBody());
        return Node(NodeKind.ForStatement, start, children);
    }

    private void RequireForSeparator()
    {
        if (!Take(SemicolonToken) && !Boundary) { Error("SOL1101", "Expected a separator between for clauses."); }
    }

    private SyntaxNode ParseSwitch()
    {
        int start = Start;
        Advance();
        var children = new List<SyntaxNode> { ParseExpression(allowTrailingLambda: false) };
        RequireBodyBrace();
        Expect(OpenBraceToken);
        int previousSuppression = _suppressedNewlines;
        _suppressedNewlines = 0;
        try
        {
            while (!AtEnd && Kind != CloseBraceToken)
            {
                if (Take(SemicolonToken)) { continue; }
                int before = _index;
                if (Kind is CaseKeyword or DefaultKeyword) { children.Add(ParseCase()); }
                else
                {
                    Error("SOL1101", "Expected case or default in a switch body.");
                    children.Add(Recover(Start));
                }
                EnsureProgress(before);
            }
            Expect(CloseBraceToken);
        }
        finally { _suppressedNewlines = previousSuppression; }
        return Node(NodeKind.SwitchStatement, start, children);
    }

    private SyntaxNode ParseCase()
    {
        int start = Start;
        var children = new List<SyntaxNode>();
        if (Take(CaseKeyword))
        {
            if (Kind == IsKeyword)
            {
                int patternStart = Start;
                Advance();
                children.Add(Node(NodeKind.TypePattern, patternStart, [ParseType()]));
            }
            else if (IsPayloadPattern())
            {
                int patternStart = Start;
                var name = ParseName();
                int bindingsStart = Start;
                Expect(OpenParenToken);
                var bindings = new List<SyntaxNode>();
                // Flat bindings are deliberately distinct from invocation arguments.
                bindings.Add(ConsumeName());
                while (Take(CommaToken)) { bindings.Add(ConsumeName()); }
                Expect(CloseParenToken);
                children.Add(Node(NodeKind.PayloadPattern, patternStart,
                    [name, Node(NodeKind.PatternBindings, bindingsStart, bindings)]));
            }
            else { children.Add(ParseExpression(allowTrailingLambda: false)); }
        }
        else { Expect(DefaultKeyword); }
        Expect(ColonToken);
        while (!AtEnd && Kind is not (CloseBraceToken or CaseKeyword)
            && !(Kind == DefaultKeyword && Peek(1).Kind != LessToken))
        {
            if (Take(SemicolonToken)) { continue; }
            int before = _index;
            children.Add(ParseStatement());
            EnsureProgress(before);
        }
        return Node(NodeKind.SwitchCase, start, children);
    }

    private bool IsPayloadPattern()
    {
        // Keep flat constructor bindings distinct from invocation argument expressions.
        if (Kind != IdentifierToken) { return false; }
        int probe = _index + 1;
        while (PeekAbsolute(probe).Kind == DotToken && PeekAbsolute(probe + 1).Kind == IdentifierToken)
        {
            cancellation.ThrowIfCancellationRequested();
            probe += 2;
        }
        return PeekAbsolute(probe).Kind == OpenParenToken && !HasLeadingNewline(PeekAbsolute(probe));
    }

    private SyntaxNode ParseTry(NodeKind kind)
    {
        int start = Start;
        Advance();
        var children = new List<SyntaxNode> { ParseBody() };
        bool handler = false;
        while (Kind == CatchKeyword)
        {
            handler = true;
            int catchStart = Start;
            Advance();
            Expect(OpenParenToken);
            var name = ConsumeName();
            Expect(ColonToken);
            var type = ParseType();
            Expect(CloseParenToken);
            children.Add(Node(NodeKind.CatchClause, catchStart, [name, type, ParseBody()]));
        }
        if (Kind == FinallyKeyword)
        {
            handler = true;
            int finallyStart = Start;
            Advance();
            children.Add(Node(NodeKind.FinallyClause, finallyStart, [ParseBody()]));
        }
        if (!handler) { Error("SOL1101", "A try requires at least one catch or finally clause."); }
        return Node(kind, start, children);
    }

    private SyntaxNode ParseCleanup(NodeKind kind)
    {
        int start = Start;
        Advance();
        return Node(kind, start, [ParseBody()]);
    }

    private SyntaxNode ParseBody()
    {
        RequireBodyBrace();
        if (Kind == OpenBraceToken) { return ParseBlock(); }
        // Leave the next token to its enclosing statement or recovery loop.
        return Node(NodeKind.Error, Start, []);
    }

    private void RequireBodyBrace()
    {
        if (Kind != OpenBraceToken) { Error("SOL1101", "Expected a braced control-flow body."); }
        else if (HasLeadingNewline()) { Error("SOL1103", "The opening brace must be on the same logical line as its header."); }
    }

    private bool HasLeadingNewline() => HasLeadingNewline(Current);

    private bool HasLeadingNewline(SyntaxToken token) => token.LeadingTrivia.Any(t => t.Kind == TriviaKind.Newline
        || t.Kind == TriviaKind.BlockComment && HasNewline(t.Span));

    private SyntaxNode ParseBindingTarget()
    {
        if (Kind != OpenParenToken) { return ConsumeName(); }
        int start = Start;
        Advance();
        var names = new List<SyntaxNode> { ConsumeName() };
        while (Take(CommaToken)) { names.Add(ConsumeName()); }
        Expect(CloseParenToken);
        return Node(NodeKind.Destructuring, start, names);
    }

    private static bool IsAssignment(SyntaxKind kind) => kind is EqualsToken or PlusEqualsToken
        or MinusEqualsToken or StarEqualsToken or SlashEqualsToken or PercentEqualsToken
        or AmpersandEqualsToken or PipeEqualsToken or CaretEqualsToken or LessLessEqualsToken or GreaterGreaterEqualsToken;
}
