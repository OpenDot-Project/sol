// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace OpenDot.Sol.Fir;

public static class FirJson
{
    public static string Serialize(VerifiedFirProgram program, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        cancellationToken.ThrowIfCancellationRequested();
        object Node(FirNode node)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object value = node switch
            {
                FirBlock block => new { statements = block.Statements.Select(Node).ToArray() },
                FirConstant constant => new { constant.Spelling },
                FirRead read => new { read.Slot },
                FirUnary unary => new { unary.Operation, value = Node(unary.Value) },
                FirBinary binary => new { binary.Operation, left = Node(binary.Left), right = Node(binary.Right) },
                FirCall call => new { call.Target, arguments = call.Arguments.Select(a => new { a.Parameter, mode = a.Mode.ToString(), value = Node(a.Value) }).ToArray() },
                FirIntrinsic intrinsic => new { operation = intrinsic.Operation.ToString(), order = intrinsic.ParameterOrder, arguments = intrinsic.Arguments.Select(Node).ToArray() },
                FirArrayLength length => new { array = Node(length.Array) },
                FirArrayIndex index => new { array = Node(index.Array), index = Node(index.Index) },
                FirArrayLiteral array => new { elements = array.Elements.Select(Node).ToArray() },
                FirDeclare declaration => new { declaration.Slot, initializer = declaration.Initializer is null ? null : Node(declaration.Initializer) },
                FirStore store => new { store.Slot, value = Node(store.Value) },
                FirEvaluate evaluate => new { value = Node(evaluate.Value) },
                FirReturn ret => new { value = ret.Value is null ? null : Node(ret.Value) },
                FirIf conditional => new { condition = Node(conditional.Condition), then = Node(conditional.Then), @else = conditional.Else is null ? null : Node(conditional.Else) },
                FirLoop loop => new { initializer = loop.Initializer is null ? null : Node(loop.Initializer), condition = loop.Condition is null ? null : Node(loop.Condition), body = Node(loop.Body), update = loop.Update is null ? null : Node(loop.Update) },
                FirBreak or FirContinue => new { },
                _ => throw new InvalidOperationException("Unknown verified FIR node."),
            };
            return new { kind = node.GetType().Name, type = node is FirExpression expression ? expression.Type.Id : (int?)null,
                start = node.Origin.Span.Start, length = node.Origin.Span.Length, value };
        }
        return JsonSerializer.Serialize(new
        {
            schema = 2, verified = true, entry = program.Program.Entry, library = program.Program.IsLibrary,
            functions = program.Program.Functions.Select(f => new { f.Id, f.Name, runtime = f.RuntimeFunction.ToString(),
                returnType = f.ReturnType.Id, effects = f.Effects.Select(effect => effect.Id).ToArray(),
                parameters = f.Parameters.Select(p => p.Id).ToArray(),
                slots = f.Slots.Select(s => new { s.Id, s.Name, type = s.Type.Id, s.Mutable, mode = s.Mode.ToString(), s.IsParameter }).ToArray(),
                body = f.Body is null ? null : Node(f.Body) }).ToArray(),
            types = program.Program.Analysis.Types.Types.Select(t => new { t.Id, kind = t.Kind.ToString(), t.Name, arguments = t.Arguments.Select(a => a.Id).ToArray() }).ToArray(),
            flow = program.FlowGraphs.Select(g => new
            {
                g.FunctionId,
                blocks = g.Blocks.Select(b => new
                {
                    b.Id, b.Reads, b.Writes, b.Kills, b.Successors, b.Returns,
                    calls = b.Calls.Select(call => new
                    {
                        call.Target,
                        effects = call.Effects.Select(effect => effect.Id).ToArray(),
                        start = call.Origin.Span.Start,
                        length = call.Origin.Span.Length,
                    }).ToArray(),
                }).ToArray(),
            }).ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
