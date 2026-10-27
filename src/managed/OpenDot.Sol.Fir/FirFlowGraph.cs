// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Fir;

public sealed record FirFlowBlock(int Id, AstOrigin Origin, ImmutableArray<int> Reads,
    ImmutableArray<int> Writes, ImmutableArray<int> Kills, ImmutableArray<int> Successors,
    ImmutableArray<FirCallSite> Calls, bool Returns);

/// <summary>A resolved call and its declared exception set at one FIR operation.</summary>
public sealed record FirCallSite(int Target, AstOrigin Origin, ImmutableArray<ResolvedType> Effects);

/// <summary>CFG side table owned by FIR; this is not a second intermediate language.</summary>
public sealed class FirFlowGraph
{
    private FirFlowGraph(int functionId, ImmutableArray<FirFlowBlock> blocks, ImmutableArray<Diagnostic> diagnostics)
    { FunctionId = functionId; Blocks = blocks; Diagnostics = diagnostics; }
    public int FunctionId { get; }
    public ImmutableArray<FirFlowBlock> Blocks { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }

    internal static FirFlowGraph Create(FirFunction function, ImmutableArray<FirFunction> functions, CancellationToken cancellation)
    {
        var builder = new Builder(function, functions, cancellation);
        builder.Build();
        return new(function.Id, builder.Nodes.Select(n => new FirFlowBlock(n.Id, n.Origin, n.Reads.ToImmutableArray(), n.Writes.ToImmutableArray(),
            n.Kills.ToImmutableArray(), n.Next.ToImmutableArray(), n.Calls.ToImmutableArray(), n.Returns)).ToImmutableArray(), builder.Verify());
    }

    private sealed class Node(int id, AstOrigin origin)
    {
        internal int Id = id;
        internal AstOrigin Origin = origin;
        internal List<int> Next = [];
        internal List<int> Reads = [];
        internal List<int> Writes = [];
        internal List<int> Kills = [];
        internal List<FirCallSite> Calls = [];
        internal bool Returns;
    }

    private sealed class Builder(FirFunction function, ImmutableArray<FirFunction> functions, CancellationToken cancellation)
    {
        internal List<Node> Nodes { get; } = [];
        private int _fallthrough;
        private Node New(AstOrigin origin) { var node = new Node(Nodes.Count, origin); Nodes.Add(node); return node; }
        private void Edge(int from, int to) { if (!Nodes[from].Next.Contains(to)) { Nodes[from].Next.Add(to); } }
        internal void Build()
        {
            var start = New(function.Origin);
            var tail = Statement(function.Body!, start.Id, -1, -1);
            _fallthrough = New(function.Origin).Id;
            if (tail >= 0) { Edge(tail, _fallthrough); }
        }

        private int Statement(FirStatement statement, int before, int breakTo, int continueTo)
        {
            cancellation.ThrowIfCancellationRequested();
            if (before < 0) { return -1; }
            switch (statement)
            {
                case FirBlock block:
                    foreach (var child in block.Statements) { before = Statement(child, before, breakTo, continueTo); }
                    return before;
                case FirDeclare declaration:
                    var reset = New(statement.Origin); reset.Kills.Add(declaration.Slot); Edge(before, reset.Id); before = reset.Id;
                    if (declaration.Initializer is null) { return before; }
                    before = Expression(declaration.Initializer, before);
                    var initialized = New(statement.Origin); initialized.Writes.Add(declaration.Slot); Edge(before, initialized.Id); return initialized.Id;
                case FirStore store:
                    before = Expression(store.Value, before);
                    var assigned = New(statement.Origin); assigned.Writes.Add(store.Slot); Edge(before, assigned.Id); return assigned.Id;
                case FirEvaluate evaluation: return Expression(evaluation.Value, before);
                case FirReturn ret:
                    if (ret.Value is not null) { before = Expression(ret.Value, before); }
                    var exit = New(ret.Origin); exit.Returns = true; Edge(before, exit.Id); return -1;
                case FirBreak: Edge(before, breakTo); return -1;
                case FirContinue: Edge(before, continueTo); return -1;
                case FirIf conditional:
                    before = Expression(conditional.Condition, before);
                    var thenStart = New(conditional.Then.Origin); var elseStart = New(conditional.Else?.Origin ?? conditional.Origin);
                    if (conditional.Condition is not FirConstant { Spelling: "false" }) { Edge(before, thenStart.Id); }
                    if (conditional.Condition is not FirConstant { Spelling: "true" }) { Edge(before, elseStart.Id); }
                    int left = Statement(conditional.Then, thenStart.Id, breakTo, continueTo);
                    int right = conditional.Else is null ? elseStart.Id : Statement(conditional.Else, elseStart.Id, breakTo, continueTo);
                    var join = New(conditional.Origin);
                    if (left >= 0) { Edge(left, join.Id); } if (right >= 0) { Edge(right, join.Id); }
                    return left < 0 && right < 0 ? -1 : join.Id;
                case FirLoop loop:
                    if (loop.Initializer is not null) { before = Statement(loop.Initializer, before, breakTo, continueTo); }
                    var test = New(loop.Origin); Edge(before, test.Id);
                    int tested = loop.Condition is null ? test.Id : Expression(loop.Condition, test.Id);
                    var body = New(loop.Body.Origin); var update = New(loop.Origin); var after = New(loop.Origin);
                    if (loop.Condition is not FirConstant { Spelling: "false" }) { Edge(tested, body.Id); }
                    if (loop.Condition is not null and not FirConstant { Spelling: "true" }) { Edge(tested, after.Id); }
                    int last = Statement(loop.Body, body.Id, after.Id, update.Id);
                    if (last >= 0) { Edge(last, update.Id); }
                    int updated = loop.Update is null ? update.Id : Statement(loop.Update, update.Id, after.Id, test.Id);
                    if (updated >= 0) { Edge(updated, test.Id); }
                    return after.Id;
                case FirArrayForEach iteration:
                    var resetItem = New(iteration.Origin); resetItem.Kills.Add(iteration.ItemSlot); Edge(before, resetItem.Id);
                    before = Expression(iteration.Array, resetItem.Id);
                    var eachTest = New(iteration.Origin); Edge(before, eachTest.Id);
                    var eachAfter = New(iteration.Origin);
                    Edge(eachTest.Id, eachAfter.Id);
                    if (iteration.Array is FirArrayLiteral { Elements.IsEmpty: true }) { return eachAfter.Id; }
                    var itemEntry = New(iteration.Origin); itemEntry.Writes.Add(iteration.ItemSlot);
                    var eachBody = New(iteration.Body.Origin); var eachStep = New(iteration.Origin);
                    Edge(eachTest.Id, itemEntry.Id);
                    Edge(itemEntry.Id, eachBody.Id);
                    int eachLast = Statement(iteration.Body, eachBody.Id, eachAfter.Id, eachStep.Id);
                    if (eachLast >= 0) { Edge(eachLast, eachStep.Id); }
                    Edge(eachStep.Id, eachTest.Id);
                    return eachAfter.Id;
                default: throw new InvalidOperationException("Verified statement shape was not handled by CFG construction.");
            }
        }

        private int Expression(FirExpression expression, int before)
        {
            switch (expression)
            {
                case FirRead read:
                    var node = New(read.Origin); node.Reads.Add(read.Slot); Edge(before, node.Id); return node.Id;
                case FirCallValueReference:
                    // Its source evaluation already completed earlier in this call plan.
                    return before;
                case FirUnary unary: return Expression(unary.Value, before);
                case FirBinary { Operation: "&&" or "||" } conditional:
                    int left = Expression(conditional.Left, before); var right = New(conditional.Right.Origin); var join = New(conditional.Origin);
                    Edge(left, right.Id); Edge(left, join.Id);
                    Edge(Expression(conditional.Right, right.Id), join.Id); return join.Id;
                case FirBinary binary: return Expression(binary.Right, Expression(binary.Left, before));
                case FirArrayLength length: return Expression(length.Array, before);
                case FirIntrinsic intrinsic:
                    foreach (var argument in intrinsic.Arguments) { before = Expression(argument, before); }
                    return before;
                case FirArrayIndex index: return Expression(index.Index, Expression(index.Array, before));
                case FirArrayLiteral array:
                    foreach (var element in array.Elements) { before = Expression(element, before); }
                    return before;
                case FirCall call:
                    foreach (var argument in call.Arguments)
                    {
                        if (argument.Mode != AstPassingMode.Out) { before = Expression(argument.Value, before); }
                    }
                    var completed = New(call.Origin); Edge(before, completed.Id);
                    completed.Calls.Add(new(call.Target, call.Origin, functions[call.Target].Effects));
                    foreach (var argument in call.Arguments.Where(a => a.Mode == AstPassingMode.Out))
                    { completed.Writes.Add(((FirRead)argument.Value).Slot); }
                    if (call.Type.Kind == Analysis.ResolvedTypeKind.Bottom) { return New(call.Origin).Id; }
                    return completed.Id;
                default: return before;
            }
        }

        internal ImmutableArray<Diagnostic> Verify()
        {
            var reachable = new HashSet<int>(); var pending = new Queue<int>(); pending.Enqueue(0);
            while (pending.TryDequeue(out int node))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!reachable.Add(node)) { continue; }
                foreach (int next in Nodes[node].Next) { pending.Enqueue(next); }
            }
            var predecessors = Nodes.Select(_ => new List<int>()).ToArray();
            foreach (var node in Nodes) { foreach (int next in node.Next) { predecessors[next].Add(node.Id); } }
            var all = function.Slots.Select(s => s.Id).ToHashSet();
            var initial = function.Parameters.Where(p => p.Mode != AstPassingMode.Out).Select(p => p.Id).ToHashSet();
            var inputs = Nodes.Select(_ => new HashSet<int>(all)).ToArray();
            var outputs = Nodes.Select(_ => new HashSet<int>(all)).ToArray();
            inputs[0] = initial;
            bool changed;
            int iterations = 0;
            do
            {
                changed = false;
                if (++iterations > Nodes.Count + 2) { throw new InvalidOperationException("Definite assignment failed to converge."); }
                foreach (var node in Nodes.Where(n => reachable.Contains(n.Id)))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var assigned = node.Id == 0 ? new HashSet<int>(initial) : new HashSet<int>(all);
                    foreach (int previous in predecessors[node.Id].Where(reachable.Contains)) { assigned.IntersectWith(outputs[previous]); }
                    inputs[node.Id] = assigned;
                    var written = new HashSet<int>(assigned); written.ExceptWith(node.Kills); written.UnionWith(node.Writes);
                    if (!written.SetEquals(outputs[node.Id])) { outputs[node.Id] = written; changed = true; }
                }
            } while (changed);
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            foreach (var node in Nodes.Where(n => reachable.Contains(n.Id)))
            {
                foreach (int slot in node.Reads.Where(s => !inputs[node.Id].Contains(s)))
                { diagnostics.Add(new("SOL2410", "FIR read before definite assignment: " + function.Slots[slot].Name, node.Origin.Source, node.Origin.Span)); }
                if (node.Returns)
                {
                    foreach (var slot in function.Parameters.Where(p => p.Mode == AstPassingMode.Out && !inputs[node.Id].Contains(p.Id)))
                    { diagnostics.Add(new("SOL2411", "FIR normal return leaves out parameter unassigned: " + slot.Name, node.Origin.Source, node.Origin.Span)); }
                }
            }
            if (reachable.Contains(_fallthrough))
            { diagnostics.Add(new("SOL2412", "FIR has a reachable fallthrough without a return.", function.Origin.Source, function.Origin.Span)); }
            return diagnostics.ToImmutable();
        }
    }
}
