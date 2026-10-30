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

public sealed partial class BodyAnalysis
{
    /// <summary>
    /// Discovers a finite instance graph before propagating failure states. Annotated recursive
    /// cycles are therefore checked as a group rather than being accepted by traversal order.
    /// </summary>
    private sealed class MemberCallProofBuilder(BodyAnalysis analysis, CancellationToken cancellation)
    {
        private readonly record struct InstanceKey(AstFunction Function, int OwnerId, string MethodIds,
            string LexicalIdentity = "");
        internal sealed record Request(BodyCallBinding Binding, GenericMemberBodyProjectionAnalysis? Projection,
            Instance? Target, BodyBindingStatus LocalStatus, string? Reason);

        internal sealed class Instance(int id, SpecializedBodyAnalysis body)
        {
            internal int Id { get; } = id;
            internal SpecializedBodyAnalysis Body { get; } = body;
            internal List<Request> Children { get; } = [];
            internal HashSet<Instance> Parents { get; } = [];
            internal BodyBindingStatus Status { get; set; } = body.Summary.Status;
        }

        private readonly Dictionary<InstanceKey, Instance> _instances = [];
        private readonly Dictionary<int, GenericMemberCallProof> _proofs = [];
        private int _dependencyEdges;
        internal readonly List<Diagnostic> Diagnostics = [];

        internal bool RequiresProof(BodyCallBinding binding) => binding.Target is AstFunction function
            && (binding.LexicalContext is not null || function.TypeParameters.Any()
                || GenericMethodSourcePolicy.DirectOwner(function, analysis.Sources) is { Arity: > 0 });

        internal bool RequiresRootProof(BodyCallBinding binding) => binding.Target is AstFunction function
            && (binding.LexicalContext is not null
                || GenericMethodSourcePolicy.DirectOwner(function, analysis.Sources) is { Arity: > 0 });

        internal Request Collect(BodyCallBinding binding, int depth)
        {
            try { return CollectCore(binding, depth); }
            catch (TypeSpecializationException exception)
            { return Deferred(binding, "Generic member dependency could not be specialized: " + exception.Message); }
        }

        private Request CollectCore(BodyCallBinding binding, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            var function = (AstFunction)binding.Target;
            if (binding.LexicalContext is { } lexical)
            { return CollectLexicalFunction(binding, function, lexical, depth); }
            var directOwner = GenericMethodSourcePolicy.DirectOwner(function, analysis.Sources);
            if (directOwner is null || directOwner.Arity == 0)
            { return CollectFreeFunction(binding, function, directOwner, depth); }
            if (!directOwner.Members.Any(member => ReferenceEquals(member, function)))
            { return Deferred(binding, "Local functions require an enclosing lexical instance proof."); }
            var receiver = binding.SourceReceiver ?? binding.Receiver;
            if (receiver is null || ContainsTypeParameter(receiver) || receiver.IsError
                || binding.TypeArguments.Any(type => type.IsError || ContainsTypeParameter(type)))
            { return Deferred(binding, "Generic member body requires closed receiver and method arguments."); }
            var owner = GenericMethodSourcePolicy.DirectOwner(function, analysis.Sources)!;
            if (owner.SyntaxKind is not (NodeKind.ClassDeclaration or NodeKind.StructDeclaration
                or NodeKind.RefStructDeclaration or NodeKind.InterfaceDeclaration))
            { return Deferred(binding, "This owner kind has no generic member body proof."); }
            var file = BodySourceIndex.Get(analysis.Sources).Files[owner];
            for (var parent = file.GetParent(owner); parent is not null; parent = file.GetParent(parent))
            {
                if (parent is AstTypeDeclaration)
                { return Deferred(binding, "Nested generic owner body analysis is not available."); }
            }
            if (function.Body.Kind == AstBodyKind.Missing)
            { return Deferred(binding, "The selected generic member has no source body."); }
            if ((depth >= 128 || _instances.Count >= 1024)
                && (binding.Receiver is null || !_instances.ContainsKey(new InstanceKey(function,
                    binding.Receiver.Id, TypeIds(binding.TypeArguments)))))
            { return Deferred(binding, "Generic member body dependency budget exceeded."); }

            var projection = analysis.GetGenericMemberBodyForReceiver(function, receiver,
                binding.TypeArguments, cancellation);
            if (projection.Body is not { } body)
            { return new(binding, projection, null, BodyBindingStatus.Deferred, projection.Projection.Reason); }
            var key = new InstanceKey(function, body.ConstructedOwner!.Id, TypeIds(binding.TypeArguments));
            if (_instances.TryGetValue(key, out var existing)) { return ForCall(binding, projection, existing); }
            if (depth >= 128 || _instances.Count >= 1024)
            { return Deferred(binding, "Generic member body dependency budget exceeded."); }

            var instance = AddInstance(key, body, depth);
            return ForCall(binding, projection, instance);
        }

        private Request CollectFreeFunction(BodyCallBinding binding, AstFunction function,
            AstTypeDeclaration? owner, int depth)
        {
            if (binding.TypeArguments.IsDefaultOrEmpty
                || binding.TypeArguments.Any(type => type.IsError || ContainsTypeParameter(type)))
            { return Deferred(binding, "Generic callable body requires closed method arguments."); }
            if (owner is not null && !GenericMethodSourcePolicy.IsSupportedGenericObjectMethod(function, analysis.Sources))
            { return Deferred(binding, "This callable owner requires a separate lexical or receiver body proof."); }
            var file = BodySourceIndex.Get(analysis.Sources).Files[function];
            for (var parent = file.GetParent(function); parent is not null; parent = file.GetParent(parent))
            {
                if (parent is AstCallable or AstLambda or AstAnonymousFunction)
                { return Deferred(binding, "Local generic functions require an enclosing lexical instance proof."); }
            }
            if (function.Body.Kind == AstBodyKind.Missing)
            { return Deferred(binding, "The selected generic callable has no source body."); }
            var key = new InstanceKey(function, 0, TypeIds(binding.TypeArguments));
            if (_instances.TryGetValue(key, out var existing)) { return ForCall(binding, null, existing); }
            if (depth >= 128 || _instances.Count >= 1024)
            { return Deferred(binding, "Generic callable dependency budget exceeded."); }
            var body = analysis.GetGenericBodyLocal(function, binding.TypeArguments, cancellation);
            return ForCall(binding, null, AddInstance(key, body, depth));
        }

        private Request CollectLexicalFunction(BodyCallBinding binding, AstFunction function,
            LexicalBodyContext context, int depth)
        {
            if (context.UnavailableReason is { } unavailable) { return Deferred(binding, unavailable); }
            if (binding.TypeArguments.IsDefault
                || binding.TypeArguments.Any(type => type.IsError || ContainsTypeParameter(type))
                || context.EnclosingArguments.Any(pair => pair.Argument.IsError || ContainsTypeParameter(pair.Argument))
                || context.Locals.Any(local => local.Type is { } type && (type.IsError || ContainsTypeParameter(type))
                    || local.Narrowed is { } narrowed && (narrowed.IsError || ContainsTypeParameter(narrowed))))
            { return Deferred(binding, "Local callable requires a closed enclosing lexical context."); }
            if (function.Body.Kind == AstBodyKind.Missing)
            { return Deferred(binding, "The selected local callable has no source body."); }
            var key = new InstanceKey(function, 0, TypeIds(binding.TypeArguments), context.Identity);
            if (_instances.TryGetValue(key, out var existing)) { return ForCall(binding, null, existing); }
            if (depth >= 128 || _instances.Count >= 1024)
            { return Deferred(binding, "Local callable dependency budget exceeded."); }
            var body = analysis.GetLexicalBody(function, binding.TypeArguments, context, cancellation);
            return ForCall(binding, null, AddInstance(key, body, depth));
        }

        private Instance AddInstance(InstanceKey key, SpecializedBodyAnalysis body, int depth)
        {
            var instance = new Instance(_instances.Count, body);
            _instances.Add(key, instance);
            RecordDiagnostics(body.Diagnostics);
            if (body.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            { instance.Status = BodyBindingStatus.Invalid; }
            else if (!body.UnverifiedNodes.IsEmpty)
            { instance.Status = MergeProofStatus(instance.Status, BodyBindingStatus.Deferred); }
            foreach (var child in body.Calls)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!RequiresProof(child)) { continue; }
                if (_dependencyEdges >= 8192)
                {
                    instance.Children.Add(Deferred(child, "Generic member dependency edge budget exceeded."));
                    instance.Status = MergeProofStatus(instance.Status, BodyBindingStatus.Deferred);
                    break;
                }
                _dependencyEdges++;
                var request = Collect(child, depth + 1);
                instance.Children.Add(request);
                request.Target?.Parents.Add(instance);
            }
            return instance;
        }

        private void RecordDiagnostics(IEnumerable<Diagnostic> diagnostics)
        {
            // Preserve the same earliest-source ordering as the published analysis while keeping
            // temporary diagnostic storage bounded even if many closed instances fail together.
            var retained = Diagnostics.Concat(diagnostics).Distinct()
                .OrderBy(diagnostic => diagnostic.Source.Path, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.Span.Start).ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .Take(analysis.Sources.Declarations.Options.DiagnosticLimit).ToArray();
            Diagnostics.Clear();
            Diagnostics.AddRange(retained);
        }

        internal void ResolveStatuses()
        {
            var queue = new Queue<Instance>(_instances.Values.OrderBy(instance => instance.Id));
            var queued = _instances.Values.ToHashSet();
            while (queue.TryDequeue(out var instance))
            {
                cancellation.ThrowIfCancellationRequested();
                queued.Remove(instance);
                var status = instance.Status;
                foreach (var request in instance.Children)
                { status = MergeProofStatus(status, Status(request)); }
                if (status == instance.Status) { continue; }
                instance.Status = status;
                foreach (var parent in instance.Parents.OrderBy(parent => parent.Id))
                { if (queued.Add(parent)) { queue.Enqueue(parent); } }
            }
        }

        internal GenericMemberCallProof CreateProof(Request request) =>
            CreateProof(request, new HashSet<int>());

        private GenericMemberCallProof CreateProof(Request request, HashSet<int> active)
        {
            cancellation.ThrowIfCancellationRequested();
            var status = Status(request);
            if (request.Target is not { } target)
            { return new(request.Binding, status, request.Projection, [], request.Reason); }
            if (_proofs.TryGetValue(target.Id, out var prior))
            { return AtCall(prior, request, status); }
            if (!active.Add(target.Id))
            {
                return new(request.Binding, status, request.Projection, [],
                    "Reference to an instance in the checked recursive dependency group.")
                { IsRecursiveReference = true, SpecializedBody = target.Body };
            }
            try
            {
                var children = target.Children.Select(child => CreateProof(child, active)).ToImmutableArray();
                var proof = new GenericMemberCallProof(request.Binding, target.Status, request.Projection,
                    children, target.Status == BodyBindingStatus.Bound ? null
                        : "Selected generic callable dependency group is not fully verified.")
                { SpecializedBody = target.Body };
                _proofs.Add(target.Id, proof);
                return AtCall(proof, request, status);
            }
            finally { active.Remove(target.Id); }
        }

        private static GenericMemberCallProof AtCall(GenericMemberCallProof proof, Request request,
            BodyBindingStatus status) => proof with { Binding = request.Binding, Projection = request.Projection,
                Status = status, Reason = request.Reason ?? proof.Reason };

        private static Request ForCall(BodyCallBinding binding, GenericMemberBodyProjectionAnalysis? projection,
            Instance instance)
        {
            bool agrees = ReferenceEquals(instance.Body.Summary.ReturnType, binding.ReturnType);
            return new(binding, projection, instance, agrees ? BodyBindingStatus.Bound : BodyBindingStatus.Deferred,
                agrees ? null : "Closed member result differs from its selected source signature.");
        }

        private static BodyBindingStatus Status(Request request) => request.Target is { } target
            ? MergeProofStatus(request.LocalStatus, target.Status) : request.LocalStatus;

        private static Request Deferred(BodyCallBinding binding, string reason) =>
            new(binding, null, null, BodyBindingStatus.Deferred, reason);
    }
}
