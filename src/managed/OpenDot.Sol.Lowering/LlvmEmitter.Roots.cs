// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;

namespace OpenDot.Sol.Lowering;

public sealed partial class LlvmEmitter
{
    private readonly Dictionary<int, int> _rootSlots = [];
    private int _rootCount;

    private void BeginRoots(FirFunction function)
    {
        _rootSlots.Clear(); _rootCount = 0;
        foreach (var slot in function.Slots)
        {
            if (Type(slot.Type) == "ptr" && !(slot.IsParameter && slot.Mode is AstPassingMode.Ref or AstPassingMode.Out))
            { _rootSlots.Add(slot.Id, _rootCount++); }
        }
        Line("%roots = alloca [$ROOT_COUNT$ x ptr], align 8");
        Line("store [$ROOT_COUNT$ x ptr] zeroinitializer, ptr %roots");
        Line("%root_frame = alloca { ptr, ptr, i32, i32 }, align 8");
        Line("%root_base = getelementptr { ptr, ptr, i32, i32 }, ptr %root_frame, i32 0, i32 1");
        Line("store ptr %roots, ptr %root_base");
        Line("%root_count = getelementptr { ptr, ptr, i32, i32 }, ptr %root_frame, i32 0, i32 2");
        Line("store i32 $ROOT_COUNT$, ptr %root_count");
        foreach (var (slot, index) in _rootSlots)
        { Line($"%s{slot} = getelementptr ptr, ptr %roots, i64 {index}"); }
        Line("call void @sol_gc_push_frame(ptr %root_frame)");
    }

    // Each syntactic temporary has a reusable slot across loop iterations; no loop-local alloca growth.
    private Value KeepAlive(Value value)
    {
        if (value.Type != "ptr") { return value; }
        string address = Temp();
        Line($"{address} = getelementptr ptr, ptr %roots, i64 {_rootCount++}");
        Line($"call void @sol_gc_store_ref(ptr {address}, ptr {value.Operand})");
        return value;
    }

    private void Store(Value value, string address)
    {
        if (value.Type == "ptr") { Line($"call void @sol_gc_store_ref(ptr {address}, ptr {value.Operand})"); }
        else { Line($"store {value.Type} {value.Operand}, ptr {address}"); }
    }

    private void EndRoots() => Line("call void @sol_gc_pop_frame(ptr %root_frame)");
}
