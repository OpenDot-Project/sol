// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Ast;

public sealed partial class AstSnapshot
{
    private static AstAnonymousFunction AnonymousFunction(int id, AstOrigin origin,
        ImmutableArray<AstNode> children)
    {
        var parameterList = children.FirstOrDefault(child => child.SyntaxKind == NodeKind.ParameterList);
        int parameterEnd = parameterList?.Origin.Span.End ?? origin.Span.Start;
        var types = children.OfType<AstType>().OrderBy(type => type.Origin.Span.Start).ToImmutableArray();
        var tokens = origin.Element.Tokens.OrderBy(token => token.Span.Start).ToArray();
        var throwsToken = tokens.FirstOrDefault(token => token.Span.Start >= parameterEnd
            && origin.Source.GetText(token.Span) == "throws");

        AstType? returnType = null;
        ImmutableArray<AstType> thrownTypes = [];
        if (throwsToken is not null)
        {
            thrownTypes = types.Where(type => type.Origin.Span.Start > throwsToken.Span.End).ToImmutableArray();
            var precedingTypes = types.Where(type => type.Origin.Span.End <= throwsToken.Span.Start).ToArray();
            if (precedingTypes.Length != 0 && HasReturnTypeSeparator(tokens, parameterEnd, precedingTypes[0].Origin.Span.Start))
            { returnType = precedingTypes[^1]; }
        }
        else if (types.Length != 0 && HasReturnTypeSeparator(tokens, parameterEnd, types[0].Origin.Span.Start))
        {
            returnType = types[0];
        }

        AstNode? bodyNode = children.LastOrDefault(child => child is AstBlock or AstExpression);
        var body = bodyNode is null ? new AstBody(AstBodyKind.Missing, null)
            : new AstBody(bodyNode is AstBlock ? AstBodyKind.Block : AstBodyKind.Expression, bodyNode);
        return new(id, origin, children, Parameters(children), returnType, thrownTypes, body);
    }

    private static bool HasReturnTypeSeparator(SyntaxToken[] tokens, int parameterEnd, int typeStart)
        => tokens.Any(token => token.Kind == SyntaxKind.ColonToken
            && token.Span.Start >= parameterEnd && token.Span.End <= typeStart);
}
