// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using static OpenDot.Sol.Cst.SyntaxKind;

namespace OpenDot.Sol.Cst;

internal sealed partial class Parser
{
    private SyntaxNode ParseLambda()
    {
        int start = Start;
        int previousSuppression = _suppressedNewlines;
        _suppressedNewlines = 0;
        try
        {
            Expect(OpenBraceToken);
            var children = new List<SyntaxNode>();
            if (Kind is RefKeyword or OutKeyword || Kind == IdentifierToken
                && Peek(1).Kind is CommaToken or ColonToken or ArrowToken)
            {
                int parametersStart = Start;
                var parameters = new List<SyntaxNode>();
                do
                {
                    int parameterStart = Start;
                    var parameter = new List<SyntaxNode>();
                    if (Kind is RefKeyword or OutKeyword)
                    {
                        int modeStart = Start;
                        Advance();
                        parameter.Add(Node(NodeKind.PassingMode, modeStart, []));
                    }
                    parameter.Add(ConsumeName());
                    if (Take(ColonToken)) { parameter.Add(ParseType()); }
                    parameters.Add(Node(NodeKind.LambdaParameter, parameterStart, parameter));
                } while (Take(CommaToken));
                Expect(ArrowToken);
                children.Add(Node(NodeKind.LambdaParameters, parametersStart, parameters));
            }
            else if (Kind == ArrowToken)
            {
                int parametersStart = Start;
                Advance();
                children.Add(Node(NodeKind.LambdaParameters, parametersStart, []));
            }
            while (!AtEnd && Kind != CloseBraceToken)
            {
                if (Take(SemicolonToken)) { continue; }
                int before = _index;
                children.Add(ParseStatement());
                EnsureProgress(before);
            }
            Expect(CloseBraceToken);
            return Node(NodeKind.LambdaExpression, start, children);
        }
        finally { _suppressedNewlines = previousSuppression; }
    }

    private SyntaxNode ParseAnonymousFunction()
    {
        int start = Start;
        Advance();
        var children = new List<SyntaxNode> { ParseParameters(allowOmittedTypes: true) };
        if (Take(ColonToken)) { children.Add(ParseType()); }
        if (IsText("throws"))
        {
            Advance();
            do { children.Add(ParseType()); } while (Take(CommaToken));
        }
        if (Take(EqualsToken)) { children.Add(ParseExpression()); }
        else { children.Add(ParseBody()); }
        return Node(NodeKind.AnonymousFunctionExpression, start, children);
    }

    private SyntaxNode ParseArguments(SyntaxKind open = OpenParenToken, SyntaxKind close = CloseParenToken)
    {
        int start = Start;
        _suppressedNewlines++;
        try
        {
            Expect(open);
            var arguments = new List<SyntaxNode>();
            while (!AtEnd && Kind != close)
            {
                int before = _index;
                int argumentStart = Start;
                var children = new List<SyntaxNode>();
                if (Kind == IdentifierToken && Peek(1).Kind == EqualsToken)
                {
                    children.Add(ConsumeName());
                    Advance();
                }
                if (Kind is RefKeyword or OutKeyword or StarToken)
                {
                    int valueStart = Start;
                    var kind = Kind switch
                    {
                        RefKeyword => NodeKind.RefArgument,
                        OutKeyword => NodeKind.OutArgument,
                        _ => NodeKind.SpreadArgument,
                    };
                    Advance();
                    if (kind == NodeKind.OutArgument && Take(VarKeyword))
                    {
                        children.Add(Node(NodeKind.OutVariable, valueStart, [ConsumeName()]));
                    }
                    else { children.Add(Node(kind, valueStart, [ParseExpression()])); }
                }
                else { children.Add(ParseExpression()); }
                arguments.Add(Node(NodeKind.Argument, argumentStart, children));
                EnsureProgress(before);
                if (!Take(CommaToken)) { break; }
            }
            Expect(close);
            return Node(NodeKind.ArgumentList, start, arguments);
        }
        finally { _suppressedNewlines--; }
    }
}
