// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Fir;

public static partial class FirVerifier
{
    private sealed partial class Checker
    {
        private bool IsSyntheticVarargArray(FirArrayLiteral array)
        {
            var binding = GetSourceCall(array.Origin.Element);
            if (binding is null || !ReferenceEquals(binding.Call.Origin.Element, array.Origin.Element)) { return false; }
            for (int parameterIndex = 0; parameterIndex < binding.Target.Parameters.Length; parameterIndex++)
            {
                var parameter = binding.Target.Parameters[parameterIndex];
                if (parameter.PassingMode != AstPassingMode.Vararg || parameter.Type is null
                    || parameterIndex >= binding.ParameterTypes.Length) { continue; }
                var group = binding.Arguments.Where(mapping => mapping.ParameterIndex == parameterIndex).ToArray();
                bool empty = group.Length == 0 && binding.EmptyVarargParameters.Contains(parameterIndex)
                    && array.Elements.IsEmpty;
                bool expanded = group.Length != 0 && group.All(mapping => mapping.IsExpandedVararg
                    && !mapping.IsArrayArgument && !mapping.IsSpread) && group.Length == array.Elements.Length;
                if (!empty && !expanded) { continue; }
                try
                {
                    var expected = program.Analysis.Types.GetArray(Substitute(binding.ParameterTypes[parameterIndex]));
                    if (array.Type != expected) { continue; }
                }
                catch (ArgumentException) { continue; }
                if (expanded)
                {
                    bool originsMatch = true;
                    for (int index = 0; index < group.Length; index++)
                    {
                        int argumentIndex = group[index].ArgumentIndex;
                        if (argumentIndex < 0 || argumentIndex >= binding.Call.Arguments.Length
                            || binding.Call.Arguments[argumentIndex].Value is not AstExpression sourceValue
                            || !ReferenceEquals(UnwrapGrouping(sourceValue).Origin.Element, array.Elements[index].Origin.Element))
                        { originsMatch = false; break; }
                    }
                    if (!originsMatch) { continue; }
                }
                return true;
            }
            return false;
        }

        private void SameOrigin(FirNode actual, AstNode source)
        {
            if (!ReferenceEquals(actual.Origin.Element, source.Origin.Element))
            { Fail("FIR node origin differs from its source node.", actual.Origin); }
        }
    }
}
