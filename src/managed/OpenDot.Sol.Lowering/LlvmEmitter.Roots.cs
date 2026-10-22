// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;

namespace OpenDot.Sol.Lowering;

public sealed partial class LlvmEmitter
{
    private long _roots, _rootFrame;
    private int _rootCount, _rootCapacity;
    private void BeginRoots(FirFunction function)
    {
        _rootCount = 0;
        var slots = function.Slots.Where(s => Type(s.Type) == "ptr" && !(s.IsParameter && s.Mode is AstPassingMode.Ref or AstPassingMode.Out)).ToArray();
        _rootCapacity = Math.Max(1, checked(slots.Length + CountTemporaryRoots(function.Body!)));
        long arrayType = Op(LlvmOperation.ArrayType, T("ptr"), data: _rootCapacity);
        _roots = Op(LlvmOperation.Alloca, arrayType, data: 8);
        Op(LlvmOperation.Store, args: [Op(LlvmOperation.Zero, arrayType), _roots]);
        long frameType = Op(LlvmOperation.StructType, args: [T("ptr"), T("ptr"), T("i32"), T("i32")]);
        _rootFrame = Op(LlvmOperation.Alloca, frameType, data: 8);
        Op(LlvmOperation.Store, args: [_roots, Field(frameType, _rootFrame, 1)]);
        Op(LlvmOperation.Store, args: [Int(_rootCapacity), Field(frameType, _rootFrame, 2)]);
        foreach (var slot in slots) { _addresses.Add(slot.Id, RootAddress(_rootCount++)); }
        Call("sol_gc_push_frame", _rootFrame);
    }
    private long RootAddress(int index) => Gep(T("ptr"), _roots, Int(index, "i64"));
    private Value KeepAlive(Value value)
    {
        if (value.Type != "ptr") { return value; }
        if (_rootCount >= _rootCapacity) { throw new InvalidOperationException("FIR root inventory is incomplete."); }
        Call("sol_gc_store_ref", RootAddress(_rootCount++), value.Operand); return value;
    }
    private void Store(Value value, long address)
    {
        if (value.Type == "ptr") { Call("sol_gc_store_ref", address, value.Operand); }
        else { Op(LlvmOperation.Store, args: [value.Operand, address]); }
    }
    private void EndRoots() => Call("sol_gc_pop_frame", _rootFrame);

    // A prepass counts syntactic root sites. Each loop iteration reuses those stack slots.
    private static int CountTemporaryRoots(FirNode node)
    {
        int own = node is FirExpression expression && Type(expression.Type) == "ptr" ? 1 : 0;
        if (node is FirArrayLiteral) { ++own; } // Partially initialized array before evaluating its elements.
        IEnumerable<FirNode> children = node switch
        {
            FirBlock n => n.Statements, FirDeclare { Initializer: not null } n => [n.Initializer],
            FirStore n => [n.Value], FirEvaluate n => [n.Value], FirReturn { Value: not null } n => [n.Value],
            FirIf n => n.Else is null ? [n.Condition, n.Then] : [n.Condition, n.Then, n.Else],
            FirLoop n => new FirNode?[] { n.Initializer, n.Condition, n.Body, n.Update }.OfType<FirNode>(),
            FirUnary n => [n.Value], FirBinary n => [n.Left, n.Right], FirArrayLength n => [n.Array],
            FirArrayIndex n => [n.Array, n.Index], FirArrayLiteral n => n.Elements,
            FirIntrinsic n => n.Arguments, FirCall n => n.Arguments.Select(a => a.Value), _ => [],
        };
        return checked(own + children.Sum(CountTemporaryRoots));
    }
}
