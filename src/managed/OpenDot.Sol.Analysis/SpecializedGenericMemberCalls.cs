// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

public sealed partial class BodyAnalysis
{
    private void AnalyzeSpecializedMemberCalls(SpecializedBodyAnalysis body, CancellationToken cancellation)
    {
        var worker = new MemberCallProofBuilder(this, cancellation);
        var requests = new List<MemberCallProofBuilder.Request>();
        foreach (var binding in body.Calls)
        {
            cancellation.ThrowIfCancellationRequested();
            if (worker.RequiresProof(binding)) { requests.Add(worker.Collect(binding, 0)); }
        }
        worker.ResolveStatuses();
        var proofs = requests.Select(worker.CreateProof).ToImmutableArray();
        body.ApplyMemberCallProofs(proofs, worker.Diagnostics, cancellation);
    }
}

internal sealed partial class SpecializedBodyAnalysis
{
    private SpecializedBodyAnalysis(SpecializedBodyAnalysis local)
    {
        Sources = local.Sources;
        Declaration = local.Declaration;
        OwnerTypeArguments = local.OwnerTypeArguments;
        TypeArguments = local.TypeArguments;
        ConstructedOwner = local.ConstructedOwner;
        Summary = local.Summary;
        _expressions = local._expressions;
        _bodies = local._bodies;
        _lambdaBodies = local._lambdaBodies;
        _anonymousFunctionBodies = local._anonymousFunctionBodies;
        _calls = local._calls;
        _operators = local._operators;
        _nodes = local._nodes;
        Expressions = local.Expressions;
        Calls = local.Calls;
        OperatorBindings = local.OperatorBindings;
        Bodies = local.Bodies;
        LambdaBodies = local.LambdaBodies;
        AnonymousFunctionBodies = local.AnonymousFunctionBodies;
        UnverifiedNodes = local.UnverifiedNodes;
        Diagnostics = local.Diagnostics;
    }

    internal SpecializedBodyAnalysis CopyForDependencyProof() => new(this);

    private ImmutableDictionary<AstCall, GenericMemberCallProof> _genericMemberCalls =
        ImmutableDictionary<AstCall, GenericMemberCallProof>.Empty;

    internal GenericMemberCallProof? GetGenericMemberCallProof(AstCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!_nodes.Contains(call))
        { throw new ArgumentException("The call belongs to another source analysis.", nameof(call)); }
        return _genericMemberCalls.GetValueOrDefault(call);
    }

    internal void ApplyMemberCallProofs(ImmutableArray<GenericMemberCallProof> proofs,
        IEnumerable<Diagnostic> diagnostics, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        _genericMemberCalls = proofs.ToImmutableDictionary(proof => proof.Binding.Call);
        var incomplete = proofs.Where(proof => proof.Status != BodyBindingStatus.Bound).ToArray();
        if (incomplete.Length == 0) { return; }
        var files = BodySourceIndex.Get(Sources).Files;
        foreach (var proof in incomplete)
        {
            var file = files[proof.Binding.Call];
            for (AstNode? node = proof.Binding.Call; node is not null; node = file.GetParent(node))
            {
                cancellation.ThrowIfCancellationRequested();
                if (node is AstExpression expression && _expressions.TryGetValue(expression, out var value))
                { _expressions = _expressions.SetItem(expression, value with { Status = Merge(value.Status, proof.Status) }); }
                if (node is AstCallable callable && _bodies.TryGetValue(callable, out var summary))
                { _bodies = _bodies.SetItem(callable, summary with { Status = Merge(summary.Status, proof.Status) }); }
                if (node is AstLambda lambda && _lambdaBodies.TryGetValue(lambda, out var lambdaBody))
                { _lambdaBodies = _lambdaBodies.SetItem(lambda, lambdaBody with { IsComplete = false }); }
                if (node is AstAnonymousFunction anonymous && _anonymousFunctionBodies.TryGetValue(anonymous, out var anonymousBody))
                { _anonymousFunctionBodies = _anonymousFunctionBodies.SetItem(anonymous, anonymousBody with { IsComplete = false }); }
            }
        }
        Summary = _bodies[Declaration];
        Expressions = Expressions.Select(value => _expressions[value.Expression]).ToImmutableArray();
        Bodies = Bodies.Select(summary => _bodies[summary.Declaration]).ToImmutableArray();
        LambdaBodies = LambdaBodies.Select(summary => _lambdaBodies[summary.Lambda]).ToImmutableArray();
        AnonymousFunctionBodies = AnonymousFunctionBodies.Select(summary => _anonymousFunctionBodies[summary.Function]).ToImmutableArray();
        UnverifiedNodes = UnverifiedNodes.Concat(incomplete.Select(proof => proof.Binding.Call)).Distinct()
            .OrderBy(node => node.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(node => node.Origin.Span.Start).ToImmutableArray();
        Diagnostics = Diagnostics.Concat(diagnostics).Distinct()
            .OrderBy(diagnostic => diagnostic.Source.Path, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Span.Start).ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .Take(Sources.Declarations.Options.DiagnosticLimit).ToImmutableArray();
    }

    private static BodyBindingStatus Merge(BodyBindingStatus left, BodyBindingStatus right) =>
        left == BodyBindingStatus.Invalid || right == BodyBindingStatus.Invalid ? BodyBindingStatus.Invalid
            : left == BodyBindingStatus.Deferred || right == BodyBindingStatus.Deferred ? BodyBindingStatus.Deferred
            : BodyBindingStatus.Bound;
}
