// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Fir;

public static partial class FirVerifier
{
    private sealed partial class Checker
    {
        private static AstExpression UnwrapGrouping(AstExpression expression)
        {
            while (expression is AstSequence { IsGrouping: true } grouping)
            {
                var elements = grouping.Elements.Take(2).ToArray();
                if (elements is not [AstExpression nested]) { break; }
                expression = nested;
            }
            return expression;
        }

        private bool IsChecked(ResolvedType type) => !program.Analysis.Types.Types.Any(t => t.Declaration is { ModuleName: "std.core", Name: "RuntimeException" }
            && program.Analysis.Types.ClassifyConversion(type, t, cancellation) != IntrinsicConversion.None);
        private FirSlot? Slot(int id, AstOrigin origin)
        { if (id < 0 || id >= _function.Slots.Length) { Fail("Slot is outside its function.", origin); return null; } return _function.Slots[id]; }
        private void Exact(ResolvedType actual, ResolvedType? expected, AstOrigin origin)
        { if (actual != expected && actual.Kind != ResolvedTypeKind.Bottom) { Fail("FIR type identity mismatch.", origin); } }
        private void Boolean(FirExpression value)
        { if (value.Type.Kind != ResolvedTypeKind.Primitive || value.Type.Name != "bool") { Fail("Condition/result must be bool.", value.Origin); } }
        private void Fail(string message, AstOrigin origin)
        { if (Errors.Count < 1000) { Errors.Add(new("SOL2402", message, origin.Source, origin.Span)); } }
        private bool Enter(FirNode node)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_depth >= 128 || !_active.Add(node)) { Fail("Cyclic or excessively nested FIR.", node.Origin); return false; }
            _depth++; return true;
        }
        private void Leave(FirNode node) { _depth--; _active.Remove(node); }
    }
}
