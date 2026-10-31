// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    internal sealed record ConstructionCallback(AstExpression Expression, ResolvedType Owner, ResolvedType Receiver,
        bool OwnReceiver, ImmutableHashSet<LocalSymbol> Aliases, ImmutableHashSet<LocalSymbol> Assigned);

    private ConstructionCallback? ConstructionCallbackAt(AstNode? expression, State state)
    {
        if (_constructionFrame is not { } frame) { return null; }
        if (expression is AstSequence { IsGrouping: true } grouping)
        { return ConstructionCallbackAt(grouping.Elements.SingleOrDefault(), state); }
        if (expression is AstLabelledExpression { Lambda: { } labelled }) { expression = labelled; }
        if (expression is AstOperation { Operation: "!!" } operation)
        { return ConstructionCallbackAt(operation.Operands.SingleOrDefault(), state); }
        if (expression is AstLambda or AstAnonymousFunction)
        { return new((AstExpression)expression, frame.Owner, frame.Receiver, frame.OwnReceiver, state.Construction.Aliases, state.Assigned.ToImmutableHashSet()); }
        if (expression is AstAtom atom && Sources.Lexical.Lookup(atom, atom.Spelling).Locals is [var local])
        { return state.Construction.Callbacks.GetValueOrDefault(local); }
        return null;
    }

    private bool ApplyConstructionCallback(AstCall call, ResolvedType signature, State state)
    {
        var callback = ConstructionCallbackAt(call.Target, state);
        if (callback is null) { return false; }
        _cancellation.ThrowIfCancellationRequested();
        Types.ValidateOwnership(callback.Owner); Types.ValidateOwnership(callback.Receiver); Types.ValidateOwnership(signature);
        if (!Files.ContainsKey(call) || !Files.ContainsKey(callback.Expression))
        { throw new ArgumentException("Construction callback belongs to another source workspace.", nameof(call)); }
        var session = _constructionSession!;
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        { Defer(call); MarkConstructionUnknown(state); return true; }
        session.CallbackInstances.Add((callback.Expression, callback.Owner.Id, signature.Id));
        session.Dependencies.Add((call, callback.Owner.Id, null));
        if (session.Dependencies.Count > 8192
            || session.CallbackInstances.Count + session.Helpers.Count + session.ConstructorInstances.Count > 1024
            || session.ActiveCallbacks.Count + session.ActiveHelpers.Count + session.Chain.Count >= 128
            || !session.ActiveCallbacks.Add(callback.Expression))
        { Defer(call); MarkConstructionUnknown(state); return true; }
        try
        {
            var worker = ConstructionWorker(callback.Owner,
                new(callback.Owner, null, callback.Receiver, Helper: true, OwnReceiver: callback.OwnReceiver), session);
            var flow = state.Clone();
            flow.Effects.Clear(); flow.ExceptionSink = [];
            flow.Assigned.UnionWith(callback.Assigned);
            flow.Construction = flow.Construction with { Aliases = flow.Construction.Aliases.Union(callback.Aliases) };
            var lambda = callback.Expression as AstLambda;
            var anonymous = callback.Expression as AstAnonymousFunction;
            var parameterNames = lambda is not null ? lambda.Parameters.Select(parameter =>
                parameter.Children.FirstOrDefault(node => node.SyntaxKind == NodeKind.Name)?.Origin.Element).ToArray()
                : anonymous!.Parameters.Select(parameter => parameter.NameOrigin.Element).ToArray();
            for (int index = 0; index < parameterNames.Length; index++)
            {
                foreach (var local in worker.OwnLocals(callback.Expression).Where(local => ReferenceEquals(local.Origin.Element, parameterNames[index])))
                {
                    worker._localTypes[local] = signature.Arguments[index];
                    if (signature.ParameterModes[index] != AstPassingMode.Out) { flow.Assigned.Add(local); }
                    worker.ConstructionLocalAlias(local, call.Arguments[index].Value, flow);
                }
            }
            var exits = new System.Collections.Generic.List<State>();
            var context = new Context(null, signature.Arguments[^1], lambda, signature, Exits: exits);
            if (callback.Expression.Descendants().Take(65537).Count() > 65536)
            { worker.Defer(call); worker.MarkConstructionUnknown(flow); }
            else if (lambda is not null)
            {
                worker.Statements(lambda.Statements, flow, context, true);
                worker._constructionReturnsReceiver |= flow.Reachable && worker.ConstructionAlias(lambda.TailExpression, flow);
            }
            else if (anonymous!.Body.Node is AstBlock block)
            {
                worker.Statements(block.Statements, flow, context, true);
                worker._constructionReturnsReceiver |= flow.Reachable && worker.ConstructionAlias(anonymous.Body.TailExpression, flow);
            }
            else if (anonymous.Body.Node is AstExpression expression)
            {
                worker.Require(worker.Expression(expression, flow, context, signature.Arguments[^1]), signature.Arguments[^1], expression, flow);
                worker._constructionReturnsReceiver |= worker.ConstructionAlias(expression, flow);
            }
            else { worker.Defer(call); worker.MarkConstructionUnknown(flow); }
            if (flow.Reachable) { exits.Add(flow.Clone()); }
            var normal = exits.FirstOrDefault()?.Clone();
            if (normal is not null)
            { foreach (var exit in exits.Skip(1)) { var prior = normal.Clone(); normal.Join(prior, exit); } }
            var proof = new HelperApproximation(normal?.Construction ?? flow.Construction, normal is not null,
                worker._errorGeneration != 0 ? BodyBindingStatus.Invalid : worker._deferred.Count != 0 ? BodyBindingStatus.Deferred : BodyBindingStatus.Bound,
                (flow.ExceptionSink ?? []).ToImmutableArray(), flow.Effects.Union(exits.SelectMany(exit => exit.Effects)).ToImmutableArray(),
                worker._constructionReturnsReceiver, normal?.Assigned.ToImmutableHashSet());
            ReplayConstruction(worker, call);
            ApplyHelperApproximation(proof, state, call);
            session.Origins.Add(new(callback.Expression, "construction callback", proof.Effects));
            if (proof.Status == BodyBindingStatus.Invalid) { Report("SOL2277", "Construction invokes an invalid receiver callback.", call); }
            if (proof.Status != BodyBindingStatus.Bound) { Defer(call); }
            if (proof.ReturnsReceiver) { _constructionReceiverValues.Add(call); }
            return true;
        }
        finally { session.ActiveCallbacks.Remove(callback.Expression); }
    }
}
