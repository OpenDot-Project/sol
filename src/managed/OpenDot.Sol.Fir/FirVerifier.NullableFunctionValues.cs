// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Fir;

public static partial class FirVerifier
{
    private sealed partial class Checker
    {
        private void Assignable(ResolvedType actual, ResolvedType? expected, AstOrigin origin)
        {
            if (expected is not null && IsAssignable(actual, expected))
            { return; }
            Exact(actual, expected, origin);
        }

        private void CheckRead(FirRead read)
        {
            var slot = Slot(read.Slot, read.Origin);
            if (slot is null || read.Type == slot.Type) { return; }
            bool narrowed = IsNullableFunction(slot.Type) && read.Type == slot.Type.Arguments[0]
                && _sourceExpressions.TryGetValue(read.Origin.Element, out var source)
                && source.Status == BodyBindingStatus.Bound && source.Type is { } sourceType
                && Substitute(sourceType) == read.Type;
            if (!narrowed) { Fail("A local read differs from its declared slot type without a source-proven non-null refinement.", read.Origin); }
        }

        private static bool IsFunctionNullCheck(FirBinary binary) => binary.Operation is "==" or "!="
            && ((IsFunctionValue(binary.Left.Type) && IsNullSentinel(binary.Right.Type))
                || (IsFunctionValue(binary.Right.Type) && IsNullSentinel(binary.Left.Type)));

        private static bool IsFunctionValue(ResolvedType type) => type.Kind == ResolvedTypeKind.Function
            || IsNullableFunction(type);
    }
}
