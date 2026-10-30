// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    internal readonly Dictionary<(AstNode Site, InterfaceOperationKind Kind), InterfaceOperationUse> InterfaceUses = [];
    internal Func<AstCallable, BodySummary?>? InterfaceSourceBody { get; set; }

    private InterfaceOperation CompleteInterfaceOperation(InterfaceOperation operation)
    {
        if (operation.ValueType is not null || operation.Body is not AstFunction { Body.Kind: AstBodyKind.Expression } function)
        { return operation; }
        return operation with { ValueType = SpecializeInferredInterfaceReturn(function, operation.Owner) };
    }

    private void CompleteInterfaceImplementations()
    {
        foreach (var pair in InterfaceImplementations.ToArray())
        {
            _cancellation.ThrowIfCancellationRequested();
            var records = ImmutableArray.CreateBuilder<InterfaceImplementation>();
            foreach (var record in pair.Value)
            {
                _cancellation.ThrowIfCancellationRequested();
                var contracts = record.Contracts.Select(CompleteInterfaceOperation).ToImmutableArray();
                var implementation = record.Implementation is { } selected ? CompleteInterfaceOperation(selected) : null;
                var status = record.Status;
                string? reason = record.Reason;
                if (implementation is not null)
                {
                    var bodyProof = implementation.DelegateExpression is null && implementation.Body is { } sourceBody
                        ? Bodies.GetValueOrDefault(sourceBody) ?? InterfaceSourceBody?.Invoke(sourceBody) : null;
                    bool valid = true;
                    var location = record.Receiver.Declaration?.Parts[0] ?? implementation.SourceOrigin;
                    foreach (var contract in contracts) { valid &= ValidateInterfaceOperation(implementation, contract, location); }
                    if (!valid || bodyProof?.Status == BodyBindingStatus.Invalid)
                    { status = BodyBindingStatus.Invalid; reason = "The selected source implementation is invalid."; }
                    else if (implementation.ValueType is null || contracts.Any(contract => contract.ValueType is null)
                        || bodyProof?.Status == BodyBindingStatus.Deferred
                        || implementation.DelegateExpression is null && implementation.Body is not null && bodyProof is null)
                    {
                        if (status != BodyBindingStatus.Invalid) { status = BodyBindingStatus.Deferred; reason = "The operation signature is incomplete."; }
                        Defer(location);
                    }
                }
                records.Add(record with { Contracts = contracts, Implementation = implementation, Status = status, Reason = reason });
            }
            InterfaceImplementations[pair.Key] = records.ToImmutable();
        }
        foreach (var pair in Calls.ToArray())
        {
            if (pair.Value.InterfaceImplementation is not { } prior) { continue; }
            var completed = CompletedInterfaceRecord(prior);
            Calls[pair.Key] = pair.Value with { InterfaceImplementation = completed };
            PropagateInterfaceFailure(pair.Key, completed.Status);
        }
        foreach (var pair in InterfaceUses.ToArray())
        {
            var completed = CompletedInterfaceRecord(pair.Value.Selection);
            InterfaceUses[pair.Key] = pair.Value with { Selection = completed };
            PropagateInterfaceFailure(pair.Key.Site, completed.Status);
        }
    }

    private InterfaceImplementation CompletedInterfaceRecord(InterfaceImplementation prior) =>
        InterfaceImplementations.TryGetValue(prior.Receiver.Id, out var records)
            ? records.FirstOrDefault(record => record.Signature == prior.Signature) ?? prior : prior;

    private void PropagateInterfaceFailure(AstNode site, BodyBindingStatus status)
    {
        if (status == BodyBindingStatus.Bound) { return; }
        Defer(site);
        foreach (var node in new[] { site }.Concat(Ancestors(site)))
        {
            if (node is AstExpression expression && Expressions.TryGetValue(expression, out var binding)
                && binding.Status != BodyBindingStatus.Invalid)
            { Expressions[expression] = binding with { Status = status }; }
            if (node is AstCallable callable && Bodies.TryGetValue(callable, out var body)
                && body.Status != BodyBindingStatus.Invalid)
            { Bodies[callable] = body with { Status = status }; }
            if (node is AstLambda lambda && LambdaBodies.TryGetValue(lambda, out var lambdaBody))
            { LambdaBodies[lambda] = lambdaBody with { IsComplete = false }; }
            if (node is AstAnonymousFunction anonymous && AnonymousFunctionBodies.TryGetValue(anonymous, out var anonymousBody))
            { AnonymousFunctionBodies[anonymous] = anonymousBody with { IsComplete = false }; }
        }
    }
}
