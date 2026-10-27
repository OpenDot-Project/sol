// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Fir;

public static partial class FirVerifier
{
    private sealed partial class Checker
    {
        private static bool BelongsToSourceBody(AstNode node, AstNode sourceRoot, AstSnapshot file)
        {
            AstLambda? nearest = null;
            for (var parent = file.GetParent(node); parent is not null; parent = file.GetParent(parent))
            {
                if (parent is AstLambda lambda) { nearest = lambda; break; }
                if (ReferenceEquals(parent, sourceRoot)) { break; }
            }
            return sourceRoot is AstLambda sourceLambda
                ? ReferenceEquals(nearest, sourceLambda)
                : nearest is null;
        }

        private void MatchLambdaBlock(FirBlock actual, AstLambda source)
        {
            SameOrigin(actual, source);
            var statements = source.Statements.ToArray();
            bool finalSourceReturns = statements.LastOrDefault() is AstReturn
                || !IsEmpty(_function.ReturnType)
                    && statements.LastOrDefault() is AstExpressionStatement { Expression: not null };
            bool appendUnitReturn = IsEmpty(_function.ReturnType) && !finalSourceReturns;
            int expectedCount = statements.Length + (appendUnitReturn ? 1 : 0);
            if (actual.Statements.Length != expectedCount)
            { Fail("Lambda FIR statement count differs from its source body.", actual.Origin); }

            int common = Math.Min(statements.Length, actual.Statements.Length);
            for (int index = 0; index < common; index++)
            {
                bool tailValue = index == statements.Length - 1 && !IsEmpty(_function.ReturnType)
                    && statements[index] is AstExpressionStatement { Expression: not null };
                MatchStatement(actual.Statements[index], statements[index], tailValue);
            }

            if (appendUnitReturn && actual.Statements.Length > statements.Length)
            {
                var last = actual.Statements[^1];
                if (last is not FirReturn { Value: null }
                    || !ReferenceEquals(last.Origin.Element, source.Origin.Element))
                { Fail("Implicit unit return differs from the source lambda tail.", last.Origin); }
            }
        }
    }
}
