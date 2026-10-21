// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;

namespace OpenDot.Sol.Lowering;

public sealed partial class LlvmEmitter
{
    private static int ElementKind(ResolvedType type) => FirVerifier.IsString(type) ? 10 : type.Name switch
    { "bool" => 0, "sbyte" => 1, "byte" => 2, "short" => 3, "int" => 4, "uint" => 5, "long" => 6, "ulong" => 7, "single" => 8, "double" => 9, _ => throw new InvalidOperationException("Unverified array element type.") };
    private static int ElementWidth(ResolvedType type) => ElementKind(type) switch { <= 2 => 1, 3 => 2, 4 or 5 or 8 => 4, _ => 8 };
    private Value ArrayLiteral(FirArrayLiteral array)
    {
        var element = array.Type.Arguments[0];
        string result = Temp();
        Line($"{result} = call ptr @sol_rt_array_new(i32 {array.Elements.Length}, i32 {ElementKind(element)})");
        KeepAlive(new("ptr", result, array.Type)); // The partially initialized array must survive element evaluation.
        for (int index = 0; index < array.Elements.Length; index++)
        {
            var value = Expression(array.Elements[index]);
            string address = Temp();
            Line($"{address} = call ptr @sol_rt_array_address(ptr {result}, i32 {index}, i32 {ElementWidth(element)})");
            Store(value, address);
        }
        return new("ptr", result, array.Type);
    }
}
