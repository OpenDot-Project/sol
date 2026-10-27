// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Fir;

internal sealed partial class FirBuilder
{
    private const int MaximumSpecializedFunctions = 4096;
    private const int MaximumSpecializationTypeDepth = 128;
    private const long MaximumSpecializedBodyNodes = 16_000_000;

    private readonly record struct SpecializationKey(AstFunction Function, string TypeIds);
    private sealed record PendingSpecialization(AstFunction Function, ImmutableArray<ResolvedType> TypeArguments, int Id);

    private Dictionary<ResolvedType, ResolvedType> CreateSubstitution(AstFunction function,
        ImmutableArray<ResolvedType> arguments)
    {
        var parameters = _analysis.Sources.Types.GetTypeParameters(function);
        if (parameters.Length != arguments.Length)
        {
            if (parameters.Length != 0 || !arguments.IsEmpty)
            { Unsupported(function, "Generic function specialization arity"); }
            return [];
        }
        var result = new Dictionary<ResolvedType, ResolvedType>();
        for (int index = 0; index < parameters.Length; index++) { result.Add(parameters[index], arguments[index]); }
        return result;
    }

    private ResolvedType Specialize(ResolvedType type)
    {
        if (_typeSubstitution.Count == 0) { return type; }
        try { return _analysis.Types.Substitute(type, _typeSubstitution, _cancellation); }
        catch (TypeSpecializationException error) when (error.Failure == SpecializationFailure.ResourceLimit)
        {
            Error("SOL2403", "Generic body type specialization exceeded its resource limit: " + error.Message, _current);
            return type;
        }
        catch (TypeSpecializationException error)
        {
            Unsupported(_current, "Generic body substitution produced a type outside the verified native subset: " + error.Message);
            return type;
        }
    }

    private int SpecializationTarget(AstCallable callable, ImmutableArray<ResolvedType> typeArguments, AstNode call)
    {
        if (callable is not AstFunction function)
        { Unsupported(call, "Callable shape in native generic specialization"); return -1; }
        if (!function.TypeParameters.Any())
        {
            if (!typeArguments.IsEmpty) { Unsupported(call, "Type arguments supplied to a nongeneric target"); return -1; }
            if (_functions.TryGetValue(function, out int ordinary)) { return ordinary; }
            Unsupported(call, "Unregistered native function target"); return -1;
        }
        if (Ancestors(function).OfType<AstTypeDeclaration>().Any())
        { Unsupported(call, "Generic member specialization"); return -1; }
        if (function.Body.Kind == AstBodyKind.Missing || function.ReturnsByReference)
        { Unsupported(call, "Generic specialization requires a source body and value return"); return -1; }

        var arguments = typeArguments.Select(Specialize).ToImmutableArray();
        var parameters = _analysis.Sources.Types.GetTypeParameters(function);
        if (arguments.Length != parameters.Length || arguments.IsDefault)
        { Unsupported(call, "Generic specialization requires every type argument"); return -1; }
        foreach (var argument in arguments)
        {
            if (!SupportedArgument(argument, out int depth))
            { Unsupported(call, "Generic specialization type is outside the verified native subset"); return -1; }
            if (depth > MaximumSpecializationTypeDepth)
            { Error("SOL2403", "Generic specialization exceeded the 128-level type depth limit.", call); return -1; }
        }
        var constraints = _analysis.Types.CheckConstraints(function, arguments, _cancellation);
        if (constraints.Status != ConstraintStatus.Satisfied)
        { Unsupported(call, "Generic specialization constraints are not proven by source analysis"); return -1; }

        string identity = string.Join(",", arguments.Select(argument => argument.Id.ToString(CultureInfo.InvariantCulture)));
        var key = new SpecializationKey(function, identity);
        if (_specializedFunctions.TryGetValue(key, out int existing)) { return existing; }
        if (_specializationCount >= MaximumSpecializedFunctions)
        { Error("SOL2403", "Generic specialization exceeded the 4096-function compilation limit.", call); return -1; }
        int id = _nextFunctionId++;
        _specializationCount++;
        _specializedFunctions.Add(key, id);
        _pendingSpecializations.Enqueue(new(function, arguments, id));
        return id;
    }

    private bool AccountSpecializedBody(AstFunction function)
    {
        if (function.Body.Node is not { } root)
        { Error("SOL2403", "A generic instance has no source body to specialize.", function); return false; }
        long count = 0;
        var pending = new Stack<AstNode>();
        var visited = new HashSet<AstNode>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(node)) { continue; }
            if (++count > MaximumSpecializedBodyNodes - _specializedBodyNodeCount)
            {
                Error("SOL2403", "Generic specialization exceeded the 16,000,000 specialized-body-node compilation limit.", function);
                return false;
            }
            foreach (var child in node.Children) { pending.Push(child); }
        }
        _specializedBodyNodeCount += count;
        return true;
    }

    private static bool SupportedArgument(ResolvedType type, out int depth)
    {
        depth = 0;
        var pending = new Stack<(ResolvedType Type, int Depth)>();
        var maximumDepthByType = new Dictionary<ResolvedType, int>();
        pending.Push((type, 1));
        while (pending.TryPop(out var current))
        {
            if (maximumDepthByType.TryGetValue(current.Type, out int previousDepth)
                && previousDepth >= current.Depth) { continue; }
            maximumDepthByType[current.Type] = current.Depth;
            depth = Math.Max(depth, current.Depth);
            if (current.Type.Kind == ResolvedTypeKind.Parameter) { return false; }
            foreach (var argument in current.Type.Arguments) { pending.Push((argument, current.Depth + 1)); }
            foreach (var effect in current.Type.Effects) { pending.Push((effect, current.Depth + 1)); }
        }
        return FirVerifier.Supported(type) && !type.IsRefLike;
    }
}
