// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private Value?[] CallArgumentValues(AstCall call, State state, Context context)
    {
        var values = new Value?[call.Arguments.Length];
        for (int index = 0; index < values.Length; index++)
        {
            var argument = call.Arguments[index];
            if (argument.Mode == AstArgumentMode.OutVariable || argument.Value is AstLambda or AstLabelledExpression
                || argument.Value is AstSequence { SyntaxKind: NodeKind.ArrayExpression }) { continue; }
            if (argument.Value is AstAtom { SyntaxKind: NodeKind.LiteralExpression } literal
                && literal.Origin.Element.Tokens.FirstOrDefault()?.Kind is SyntaxKind.IntegerLiteralToken or SyntaxKind.FloatLiteralToken)
            { values[index] = new(Types.GetPrimitive(literal.Origin.Element.Tokens.First().Kind == SyntaxKind.IntegerLiteralToken ? "int" : "double"), Literal: literal.Spelling); continue; }
            var evaluation = state.Reachable ? state : state.Clone();
            if (!state.Reachable) { evaluation.ExceptionSink = null; }
            values[index] = argument.Value is AstExpression expression
                ? Expression(expression, evaluation, context, read: argument.Mode != AstArgumentMode.Out) : new(null);
            if (argument.Mode == AstArgumentMode.Ref && values[index]?.Local is { } local)
            { values[index] = values[index]! with { Type = LocalType(local) }; }
        }
        return values;
    }
}
