// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using static OpenDot.Sol.Cst.SyntaxKind;

namespace OpenDot.Sol.Cst;

internal sealed partial class Parser
{
    private SyntaxNode ParseFunction(int start)
    {
        Advance();
        var children = new List<SyntaxNode>();
        if (Kind == LessToken) { children.Add(ParseTypeParameters()); }
        int nameStart = Start;
        bool checkedOperator = IsText("checked") && IsOperatorName(Peek(1).Kind);
        if (checkedOperator) { Advance(); }
        if (Kind == IdentifierToken || IsOperatorName(Kind))
        {
            var kind = checkedOperator || Kind != IdentifierToken ? NodeKind.OperatorName : NodeKind.Name;
            Advance();
            children.Add(Node(kind, nameStart, []));
        }
        else { children.Add(ConsumeName()); }
        children.Add(ParseParameters());
        if (Kind == ColonToken) { children.Add(ParseReturnType()); }
        if (Kind == WhereKeyword) { children.Add(ParseWhere()); }
        if (IsText("throws")) { children.Add(ParseThrows()); }
        if (Take(EqualsToken)) { children.Add(ParseExpression()); Terminate(); }
        else if (Kind == OpenBraceToken) { RequireSameLineBrace(); children.Add(ParseBlock()); }
        else { Terminate(); }
        return Node(NodeKind.FunctionDeclaration, start, children);
    }

    private static bool IsOperatorName(SyntaxKind kind) => kind is PlusToken or MinusToken or StarToken
        or SlashToken or PercentToken or AmpersandToken or PipeToken or CaretToken or BangToken or TildeToken
        or PlusPlusToken or MinusMinusToken or LessLessToken or GreaterGreaterToken or EqualsEqualsToken
        or BangEqualsToken or LessToken or GreaterToken or LessEqualsToken or GreaterEqualsToken or TrueKeyword or FalseKeyword;

    private SyntaxNode ParseParameters(bool constructor = false, SyntaxKind close = CloseParenToken)
    {
        int start = Start;
        Expect(close == CloseParenToken ? OpenParenToken : OpenBracketToken);
        var groups = new List<SyntaxNode>();
        _suppressedNewlines++;
        try
        {
            while (!AtEnd && Kind != close)
            {
                int before = _index;
                int groupStart = Start;
                var group = ParsePrefix();
                if (constructor && Kind is ValKeyword or VarKeyword) { group.Add(ConsumeMarker(NodeKind.Modifier)); }
                if (Kind is RefKeyword or OutKeyword or VarargKeyword) { group.Add(ConsumeMarker(NodeKind.PassingMode)); }
                group.Add(ConsumeName());
                while (Kind == CommaToken && Peek(1).Kind == IdentifierToken)
                {
                    Advance();
                    group.Add(ConsumeName());
                }
                Expect(ColonToken);
                group.Add(ParseType());
                if (Take(EqualsToken)) { group.Add(ParseExpression()); }
                groups.Add(Node(NodeKind.ParameterGroup, groupStart, group));
                EnsureProgress(before);
                if (!Take(CommaToken)) { break; }
            }
            Expect(close);
        }
        finally { _suppressedNewlines--; }
        return Node(NodeKind.ParameterList, start, groups);
    }

    private SyntaxNode ParsePrimaryConstructor()
    {
        int start = Start;
        var children = new List<SyntaxNode>();
        if (IsVisibility())
        {
            children.Add(ConsumeMarker(NodeKind.Modifier));
            Expect(ConstructorKeyword);
        }
        else { Take(ConstructorKeyword); }
        children.Add(ParseParameters(constructor: true));
        if (IsText("throws")) { children.Add(ParseThrows()); }
        return Node(NodeKind.PrimaryConstructor, start, children);
    }

    private bool IsVisibility() => Kind is PublicKeyword or PrivateKeyword or InternalKeyword or ProtectedKeyword;

    private SyntaxNode ParseConstructor(int start)
    {
        Advance();
        var children = new List<SyntaxNode> { ParseParameters() };
        if (IsText("throws")) { children.Add(ParseThrows()); }
        if (Take(ColonToken))
        {
            int targetStart = Start;
            if (Kind is ThisKeyword or SuperKeyword) { Advance(); }
            else { Error("SOL1101", "A constructor delegation must target this or super."); }
            var target = Node(NodeKind.NameExpression, targetStart, []);
            children.Add(Node(NodeKind.ConstructorDelegation, targetStart, [target, ParseArguments()]));
        }
        children.Add(ParseBody());
        return Node(NodeKind.ConstructorDeclaration, start, children);
    }

    private SyntaxNode ParseInit(int start)
    {
        Advance();
        return Node(NodeKind.InitDeclaration, start, [ParseBody()]);
    }

    private SyntaxNode ParseReturnType()
    {
        int start = Start;
        Expect(ColonToken);
        var children = new List<SyntaxNode>();
        if (Kind == RefKeyword)
        {
            children.Add(ConsumeMarker(NodeKind.PassingMode));
            if (IsText("readonly")) { children.Add(ConsumeMarker(NodeKind.Modifier)); }
        }
        children.Add(ParseType());
        return Node(NodeKind.ReturnType, start, children);
    }

    private SyntaxNode ParseProperty(int start)
    {
        if (Kind is not (ValKeyword or VarKeyword or ConstKeyword))
        {
            Error("SOL1101", "Expected val or var after a property ref modifier.");
            return Recover(start);
        }
        return ParseBinding(start, property: true);
    }

    private SyntaxNode ParseBinding(int start, bool terminate = true, bool allowDestructuring = false, bool property = false)
    {
        if (Take(ConstKeyword)) { Expect(ValKeyword); }
        else { Advance(); }
        var children = new List<SyntaxNode> { allowDestructuring ? ParseBindingTarget() : ConsumeName() };
        if (Take(ColonToken)) { children.Add(ParseType()); }
        bool accessors = property && Kind == OpenBraceToken;
        if (accessors) { children.Add(ParseAccessors()); }
        if (Take(EqualsToken)) { children.Add(ParseExpression()); }
        else if (IsText("by"))
        {
            int delegateStart = Start;
            Advance();
            if (accessors) { Error("SOL1101", "A delegated property cannot declare custom accessors."); }
            children.Add(Node(NodeKind.Delegation, delegateStart, [ParseExpression()]));
        }
        if (terminate) { Terminate(); }
        return Node(accessors ? NodeKind.PropertyDeclaration : NodeKind.Binding, start, children);
    }

    private SyntaxNode ParseAccessors()
    {
        int start = Start;
        RequireSameLineBrace();
        Advance();
        var accessors = new List<SyntaxNode>();
        while (!AtEnd && Kind != CloseBraceToken)
        {
            if (Take(SemicolonToken)) { continue; }
            int before = _index;
            int accessorStart = Start;
            var children = ParsePrefix();
            bool getter = IsText("get");
            if (getter || IsText("set") || Kind == InitKeyword) { children.Add(ConsumeMarker(NodeKind.Name)); }
            else
            {
                Error("SOL1101", "Expected get, set, or init in an accessor block.");
                accessors.Add(Recover(accessorStart));
                continue;
            }
            if (IsText("throws")) { children.Add(ParseThrows()); }
            if (Kind == OpenBraceToken) { children.Add(ParseBody()); }
            else if (Take(EqualsToken))
            {
                int bodyStart = Start;
                var body = ParseExpression();
                if (IsAssignment(Kind))
                {
                    if (getter) { Error("SOL1101", "A short getter requires an expression, not an assignment statement."); }
                    Advance();
                    body = Node(NodeKind.Assignment, bodyStart, [body, ParseExpression()]);
                }
                children.Add(body);
                Terminate();
            }
            else { Terminate(); }
            accessors.Add(Node(NodeKind.AccessorDeclaration, accessorStart, children));
            EnsureProgress(before);
        }
        if (accessors.Count == 0) { Error("SOL1101", "An accessor block requires at least one accessor."); }
        Expect(CloseBraceToken);
        return Node(NodeKind.AccessorList, start, accessors);
    }

    private SyntaxNode ParseIndexer(int start)
    {
        Expect(ThisKeyword);
        var children = new List<SyntaxNode> { ParseParameters(close: CloseBracketToken) };
        Expect(ColonToken);
        children.Add(ParseType());
        if (Kind == OpenBraceToken) { children.Add(ParseAccessors()); }
        else { Error("SOL1101", "An indexer requires an accessor block."); }
        return Node(NodeKind.IndexerDeclaration, start, children);
    }
}
