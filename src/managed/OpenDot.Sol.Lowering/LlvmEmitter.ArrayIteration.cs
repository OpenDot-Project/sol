// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Fir;

namespace OpenDot.Sol.Lowering;

public sealed partial class LlvmEmitter
{
    private readonly Dictionary<FirArrayForEach, (long Index, long Length)> _arrayIterationSlots =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    private void AllocateArrayIterationSlots(FirBlock body)
    {
        _arrayIterationSlots.Clear();
        foreach (var iteration in ArrayIterations(body))
        {
            // These allocas are emitted in the function entry block so nested loops reuse stack
            // storage rather than growing the stack each time an enclosing loop repeats.
            _arrayIterationSlots.Add(iteration, (Op(LlvmOperation.Alloca, T("i32")), Op(LlvmOperation.Alloca, T("i32"))));
        }
    }

    private void ArrayForEach(FirArrayForEach iteration)
    {
        if (!_arrayIterationSlots.TryGetValue(iteration, out var slots))
        { throw new InvalidOperationException("Verified array iteration has no lowering storage."); }

        // The source expression and array length are each evaluated once. Managed arrays have a
        // fixed length, so the saved pointer and bound define the complete traversal.
        var array = Expression(iteration.Array);
        long length = Call("sol_rt_array_length", array.Operand);
        Op(LlvmOperation.Store, args: [Int(0), slots.Index]);
        Op(LlvmOperation.Store, args: [length, slots.Length]);

        long test = NewBlock("array.for.test");
        long body = NewBlock("array.for.body");
        long update = NewBlock("array.for.update");
        long after = NewBlock("array.for.exit");
        Branch(test);

        Start(test);
        long index = Op(LlvmOperation.Load, T("i32"), [slots.Index]);
        long bound = Op(LlvmOperation.Load, T("i32"), [slots.Length]);
        long hasNext = Compare("<", false, false, index, bound);
        Conditional(hasNext, body, after);

        Start(body);
        var elementType = iteration.Array.Type.Arguments[0];
        long address = Call("sol_rt_array_address", array.Operand, index, Int(ElementWidth(elementType)));
        string type = Type(elementType);
        long item = Op(LlvmOperation.Load, T(type), [address], type == "ptr" ? 1 : 0);
        Store(new(type, item, elementType), Address(iteration.ItemSlot));
        _loops.Push((after, update));
        Statement(iteration.Body);
        _loops.Pop();
        if (!_terminated) { Branch(update); }

        Start(update);
        // Continue enters this block too, matching counted-loop update semantics and keeping
        // concurrent collection work bounded for long-running traversals.
        Call("sol_gc_poll");
        index = Op(LlvmOperation.Load, T("i32"), [slots.Index]);
        long next = Op(LlvmOperation.Add, T("i32"), [index, Int(1)]);
        Op(LlvmOperation.Store, args: [next, slots.Index]);
        Branch(test);
        Start(after);
    }

    private static IEnumerable<FirArrayForEach> ArrayIterations(FirNode root)
    {
        if (root is FirArrayForEach iteration) { yield return iteration; }

        IEnumerable<FirNode> children = root switch
        {
            FirBlock node => node.Statements,
            FirDeclare { Initializer: not null } node => [node.Initializer],
            FirStore node => [node.Value],
            FirEvaluate node => [node.Value],
            FirReturn { Value: not null } node => [node.Value],
            FirIf node => node.Else is null ? [node.Condition, node.Then] : [node.Condition, node.Then, node.Else],
            FirLoop node => new FirNode?[] { node.Initializer, node.Condition, node.Body, node.Update }.OfType<FirNode>(),
            FirArrayForEach node => [node.Array, node.Body],
            _ => [],
        };

        foreach (var child in children)
        {
            foreach (var nested in ArrayIterations(child)) { yield return nested; }
        }
    }
}
