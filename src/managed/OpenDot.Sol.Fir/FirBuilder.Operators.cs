// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Fir;

internal sealed partial class FirBuilder
{
    private FirCall? LowerOperator(AstNode site, string name, IReadOnlyList<FirExpression> operands,
        ResolvedType resultType, AstOrigin origin)
    {
        var binding = BoundOperator(site);
        if (binding is null) { return null; }
        if (binding.Operator != name || binding.UsesNullableLifting || !binding.TypeArguments.IsEmpty
            || binding.ReturnType is null || Specialize(binding.ReturnType) != resultType
            || binding.ParameterTypes.Length != operands.Count
            || binding.Target.Parameters.Length != operands.Count
            || binding.Target.Parameters.Any(parameter => parameter.PassingMode != AstPassingMode.Value))
        {
            Unsupported(site, "Operator target is outside the verified direct-call subset");
            return null;
        }

        if (!FirVerifier.Supported(resultType)
            || operands.Any(operand => !FirVerifier.Supported(operand.Type))
            || binding.ParameterTypes.Any(parameter => !FirVerifier.Supported(Specialize(parameter))))
        {
            Unsupported(site, "Operator call uses a type without a verified native layout");
            return null;
        }

        for (int index = 0; index < operands.Count; index++)
        {
            if (Specialize(binding.ParameterTypes[index]) != operands[index].Type)
            {
                Unsupported(site, "Operator operands require an implicit conversion");
                return null;
            }
        }

        int target = SpecializationTarget(binding.Target, [], site);
        if (target < 0) { return null; }
        var arguments = operands.Select((operand, index) =>
            new FirArgument(index, AstPassingMode.Value, operand)).ToImmutableArray();
        return new(target, arguments, resultType, origin);
    }

    private static bool IsSupportedOperator(string operation, int arity) => arity switch
    {
        1 => operation is "+" or "-" or "!" or "~",
        2 => operation is "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^"
            or "<<" or ">>" or "==" or "!=" or "<" or ">" or "<=" or ">=",
        _ => false,
    };
}
