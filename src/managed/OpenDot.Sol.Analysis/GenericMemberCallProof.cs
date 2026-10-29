// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

/// <summary>Body evidence for a selected generic callable in a member dependency graph.</summary>
internal sealed record GenericMemberCallProof(BodyCallBinding Binding, BodyBindingStatus Status,
    GenericMemberBodyProjectionAnalysis? Projection, ImmutableArray<GenericMemberCallProof> Dependencies,
    string? Reason)
{
    internal bool IsRecursiveReference { get; init; }
    internal SpecializedBodyAnalysis? SpecializedBody { get; init; }
}

public sealed partial class BodyAnalysis
{
    private ImmutableDictionary<AstCall, GenericMemberCallProof> _genericMemberCalls =
        ImmutableDictionary<AstCall, GenericMemberCallProof>.Empty;

    internal GenericMemberCallProof? GetGenericMemberCallProof(AstCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!_nodes.Contains(call))
        { throw new ArgumentException("The call belongs to another source analysis.", nameof(call)); }
        return _genericMemberCalls.GetValueOrDefault(call);
    }

    private void AnalyzeGenericMemberCalls(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (Sources.HasErrors) { return; }
        var worker = new MemberCallProofBuilder(this, cancellation);
        var proofs = ImmutableDictionary.CreateBuilder<AstCall, GenericMemberCallProof>();
        var requests = new List<MemberCallProofBuilder.Request>();
        foreach (var binding in Calls.OrderBy(binding => binding.Call.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(binding => binding.Call.Origin.Span.Start))
        {
            cancellation.ThrowIfCancellationRequested();
            if (worker.RequiresRootProof(binding)) { requests.Add(worker.Collect(binding, 0)); }
        }
        worker.ResolveStatuses();
        foreach (var request in requests) { proofs.Add(request.Binding.Call, worker.CreateProof(request)); }
        _genericMemberCalls = proofs.ToImmutable();
        var incomplete = proofs.Values.Where(proof => proof.Status != BodyBindingStatus.Bound).ToArray();
        if (incomplete.Length == 0) { return; }
        IsComplete = false;
        UnverifiedNodes = UnverifiedNodes.Concat(incomplete.Select(proof => proof.Binding.Call)).Distinct()
            .OrderBy(node => node.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(node => node.Origin.Span.Start).ToImmutableArray();
        Diagnostics = Diagnostics.Concat(worker.Diagnostics).Distinct()
            .OrderBy(diagnostic => diagnostic.Source.Path, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Span.Start).ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .Take(Sources.Declarations.Options.DiagnosticLimit).ToImmutableArray();
        PropagateMemberCallProofStates(incomplete, cancellation);
    }

    private void PropagateMemberCallProofStates(IEnumerable<GenericMemberCallProof> incomplete,
        CancellationToken cancellation)
    {
        var files = BodySourceIndex.Get(Sources).Files;
        foreach (var proof in incomplete)
        {
            var file = files[proof.Binding.Call];
            for (AstNode? node = proof.Binding.Call; node is not null; node = file.GetParent(node))
            {
                cancellation.ThrowIfCancellationRequested();
                if (node is AstExpression expression && _expressions.TryGetValue(expression, out var value))
                { _expressions = _expressions.SetItem(expression, value with { Status = MergeProofStatus(value.Status, proof.Status) }); }
                if (node is AstCallable callable && _bodies.TryGetValue(callable, out var body))
                { _bodies = _bodies.SetItem(callable, body with { Status = MergeProofStatus(body.Status, proof.Status) }); }
                if (node is AstLambda lambda && _lambdaBodies.TryGetValue(lambda, out var lambdaBody))
                { _lambdaBodies = _lambdaBodies.SetItem(lambda, lambdaBody with { IsComplete = false }); }
                if (node is AstAnonymousFunction anonymous && _anonymousFunctionBodies.TryGetValue(anonymous, out var anonymousBody))
                { _anonymousFunctionBodies = _anonymousFunctionBodies.SetItem(anonymous, anonymousBody with { IsComplete = false }); }
            }
        }
        // Preserve the source ordering established by the original binding snapshot.
        Expressions = Expressions.Select(value => _expressions[value.Expression]).ToImmutableArray();
        Bodies = Bodies.Select(body => _bodies[body.Declaration]).ToImmutableArray();
        LambdaBodies = LambdaBodies.Select(body => _lambdaBodies[body.Lambda]).ToImmutableArray();
        AnonymousFunctionBodies = AnonymousFunctionBodies.Select(body => _anonymousFunctionBodies[body.Function]).ToImmutableArray();
    }

    private static BodyBindingStatus MergeProofStatus(BodyBindingStatus current, BodyBindingStatus proof) =>
        current == BodyBindingStatus.Invalid || proof == BodyBindingStatus.Invalid ? BodyBindingStatus.Invalid
            : current == BodyBindingStatus.Deferred || proof == BodyBindingStatus.Deferred ? BodyBindingStatus.Deferred
            : BodyBindingStatus.Bound;

}
