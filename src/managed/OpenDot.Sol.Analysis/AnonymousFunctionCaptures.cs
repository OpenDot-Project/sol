// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

/// <summary>Source operations applied to a local captured by an anonymous function.</summary>
[Flags]
public enum AnonymousFunctionCaptureAccess
{
    /// <summary>The captured local is read.</summary>
    Read = 1,
    /// <summary>The captured local is assigned or passed as <c>out</c>.</summary>
    Write = 2,
    /// <summary>The captured local is both read and written, or passed as <c>ref</c>.</summary>
    ReadWrite = Read | Write,
}

/// <summary>Candidate source storage class for an anonymous-function local capture.</summary>
public enum AnonymousFunctionCaptureStorage
{
    /// <summary>An immutable value may be copied, depending on its resolved representation.</summary>
    ValueCopy,
    /// <summary>Mutable reads and writes require shared storage.</summary>
    SharedMutableCell,
    /// <summary>A ref/out location whose lifetime must be proven separately.</summary>
    BorrowedLocation,
    /// <summary>A local callable with its own lexical environment.</summary>
    NestedCallableEnvironment,
}

/// <summary>One local symbol needed by an anonymous-function environment.</summary>
/// <param name="Function">The anonymous function whose environment needs this symbol.</param>
/// <param name="Local">The exact lexical declaration identity, independent of its spelling.</param>
/// <param name="References">Resolved source references in source order.</param>
/// <param name="Access">The union of local operations performed through these references.</param>
public sealed record AnonymousFunctionCapture(
    AstAnonymousFunction Function,
    LocalSymbol Local,
    ImmutableArray<AstAtom> References,
    AnonymousFunctionCaptureAccess Access)
{
    /// <summary>Candidate storage class only; this is not an escape or emitted-layout proof.</summary>
    public AnonymousFunctionCaptureStorage Storage => Local.PassingMode is AstPassingMode.Ref or AstPassingMode.Out
        ? AnonymousFunctionCaptureStorage.BorrowedLocation
        : Local.Kind == LocalSymbolKind.Function ? AnonymousFunctionCaptureStorage.NestedCallableEnvironment
        : Local.IsMutable ? AnonymousFunctionCaptureStorage.SharedMutableCell
        : AnonymousFunctionCaptureStorage.ValueCopy;
}

internal static class AnonymousFunctionCaptureAnalysis
{
    private sealed class Builder(AstAnonymousFunction function, LocalSymbol local)
    {
        internal AstAnonymousFunction Function { get; } = function;
        internal LocalSymbol Local { get; } = local;
        internal List<AstAtom> References { get; } = [];
        internal AnonymousFunctionCaptureAccess Access { get; set; }
    }

    internal static ImmutableArray<AnonymousFunctionCapture> Create(
        ImmutableArray<LexicalScope> scopes,
        Dictionary<AstNode, int> scopeOf,
        Dictionary<AstNode, AstSnapshot> files,
        IEnumerable<NameBinding> bindings,
        CancellationToken cancellationToken)
    {
        var scopesById = scopes.ToDictionary(scope => scope.Id);
        var captures = new Dictionary<(AstAnonymousFunction Function, LocalSymbol Local), Builder>();

        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!binding.CapturesOuterLocal || binding.Lookup.Locals.IsEmpty) { continue; }

            foreach (var local in binding.Lookup.Locals.Where(local => local.Kind != LocalSymbolKind.ConstructorProperty))
            {
                foreach (var function in CrossingFunctions(scopeOf[binding.Reference], local.ScopeId, scopesById))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = (function, local);
                    if (!captures.TryGetValue(key, out var capture))
                    {
                        capture = new(function, local);
                        captures.Add(key, capture);
                    }
                    capture.References.Add(binding.Reference);
                    capture.Access |= AccessOf(binding.Reference, files[binding.Reference]);
                }
            }
        }

        return captures.Values
            .OrderBy(capture => capture.Function.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(capture => capture.Function.Origin.Span.Start)
            .ThenBy(capture => capture.Local.Id)
            .Select(capture => new AnonymousFunctionCapture(capture.Function, capture.Local,
                capture.References.OrderBy(reference => reference.Origin.Span.Start).ToImmutableArray(),
                capture.Access))
            .ToImmutableArray();
    }

    private static ImmutableArray<AstAnonymousFunction> CrossingFunctions(int startScope, int localScope,
        IReadOnlyDictionary<int, LexicalScope> scopes)
    {
        var owners = ImmutableArray.CreateBuilder<AstAnonymousFunction>();
        int? current = startScope;
        while (current is { } id && id != localScope)
        {
            if (scopes[id].Owner is AstAnonymousFunction function) { owners.Add(function); }
            current = scopes[id].ParentId;
        }
        return owners.ToImmutable();
    }

    private static AnonymousFunctionCaptureAccess AccessOf(AstAtom reference, AstSnapshot file)
    {
        for (AstNode? current = reference; current is not null; current = file.GetParent(current))
        {
            if (current is AstAssignment assignment && IsDirectLocation(reference, assignment.Location))
            { return assignment.Operation == "=" ? AnonymousFunctionCaptureAccess.Write : AnonymousFunctionCaptureAccess.ReadWrite; }
            if (current is AstOperation operation && operation.Operation is "++" or "--"
                && operation.Operands.Any(operand => IsDirectLocation(reference, operand)))
            { return AnonymousFunctionCaptureAccess.ReadWrite; }
            if (current is AstCall call)
            {
                var argument = call.Arguments.FirstOrDefault(candidate => IsDirectLocation(reference, candidate.Value));
                if (argument is not null)
                {
                    return argument.Mode switch
                    {
                        AstArgumentMode.Ref => AnonymousFunctionCaptureAccess.ReadWrite,
                        AstArgumentMode.Out or AstArgumentMode.OutVariable => AnonymousFunctionCaptureAccess.Write,
                        _ => AnonymousFunctionCaptureAccess.Read,
                    };
                }
            }
            if (current is AstLambda or AstAnonymousFunction
                || current.SyntaxKind == OpenDot.Sol.Cst.NodeKind.AnonymousFunctionExpression)
            { break; }
        }
        return AnonymousFunctionCaptureAccess.Read;
    }

    private static bool IsDirectLocation(AstAtom reference, AstNode? location)
    {
        if (location is null) { return false; }
        if (ReferenceEquals(reference, location)) { return true; }
        if (location is AstSequence { IsGrouping: true } group)
        {
            var elements = group.Elements.Take(2).ToArray();
            return elements.Length == 1 && IsDirectLocation(reference, elements[0]);
        }
        return false;
    }
}
