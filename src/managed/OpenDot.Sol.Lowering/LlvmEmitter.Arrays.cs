// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;

namespace OpenDot.Sol.Lowering;

public sealed partial class LlvmEmitter
{
    private static int ElementKind(ResolvedType type) => type.Kind == ResolvedTypeKind.Array ? 11
        : FirVerifier.IsString(type) ? 10 : type.Name switch
        { "bool" => 0, "sbyte" => 1, "byte" => 2, "short" => 3, "int" => 4, "uint" => 5, "long" => 6, "ulong" => 7, "single" => 8, "double" => 9, _ => throw new InvalidOperationException("Unverified array element type.") };
    private static int ElementWidth(ResolvedType type) => ElementKind(type) switch { <= 2 => 1, 3 => 2, 4 or 5 or 8 => 4, _ => 8 };
    private Value ArrayLiteral(FirArrayLiteral array)
    {
        var element = array.Type.Arguments[0];
        long result = Call("sol_rt_array_new", Int(array.Elements.Length), Int(ElementKind(element)));
        KeepAlive(new("ptr", result, array.Type));
        for (int index = 0; index < array.Elements.Length; index++)
        {
            var value = Expression(array.Elements[index]);
            Store(value, Call("sol_rt_array_address", result, Int(index), Int(ElementWidth(element))));
        }
        return new("ptr", result, array.Type);
    }
}
