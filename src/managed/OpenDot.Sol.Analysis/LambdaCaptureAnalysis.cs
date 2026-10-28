// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

[Flags]
public enum LambdaCaptureAccess
{
    /// <summary>The captured symbol is read by the lambda body.</summary>
    Read = 1,
    /// <summary>The captured symbol is assigned or passed as <c>out</c>.</summary>
    Write = 2,
    /// <summary>The body both reads and writes the captured symbol, or passes it as <c>ref</c>.</summary>
    ReadWrite = Read | Write,
}

public enum LambdaCaptureStorage
{
    /// <summary>Immutable source binding; a representation-dependent value copy is a candidate.</summary>
    ValueCopy,
    /// <summary>Mutable source binding; every closure that observes it must share one cell.</summary>
    SharedMutableCell,
    /// <summary>Ref/out parameter storage whose lifetime must be proven before any closure escape.</summary>
    BorrowedLocation,
    /// <summary>Local function binding that can carry its own lexical environment.</summary>
    NestedCallableEnvironment,
}

/// <summary>
/// One local captured by one lambda. This is lexical inventory only: it does not prove that a
/// reference, ref-like value, or closure may escape, and it does not describe an emitted ABI.
/// </summary>
/// <param name="Lambda">The closure whose lexical environment needs this symbol.</param>
/// <param name="Local">The exact resolved declaration; names alone do not identify captures.</param>
/// <param name="References">Source occurrences ordered by byte offset within their file.</param>
/// <param name="Access">Union of read/write operations performed by those occurrences.</param>
public sealed record LambdaCapture(
    AstLambda Lambda,
    LocalSymbol Local,
    ImmutableArray<AstAtom> References,
    LambdaCaptureAccess Access)
{
    /// <summary>Candidate storage class; ref/out locations remain borrowed and cannot escape unchecked.</summary>
    public LambdaCaptureStorage Storage => Local.PassingMode is AstPassingMode.Ref or AstPassingMode.Out
        ? LambdaCaptureStorage.BorrowedLocation
        : Local.Kind == LocalSymbolKind.Function ? LambdaCaptureStorage.NestedCallableEnvironment
        : Local.IsMutable ? LambdaCaptureStorage.SharedMutableCell : LambdaCaptureStorage.ValueCopy;
}

internal static class LambdaCaptureAnalysis
{
    private sealed class CaptureBuilder(AstLambda lambda, LocalSymbol local)
    {
        internal AstLambda Lambda { get; } = lambda;
        internal LocalSymbol Local { get; } = local;
        internal List<AstAtom> References { get; } = [];
        internal LambdaCaptureAccess Access { get; set; }
    }

    internal static ImmutableArray<LambdaCapture> Create(
        ImmutableArray<LexicalScope> scopes,
        Dictionary<AstNode, int> scopeOf,
        Dictionary<AstNode, AstSnapshot> files,
        IEnumerable<NameBinding> bindings,
        CancellationToken cancellationToken)
    {
        var scopesById = scopes.ToDictionary(scope => scope.Id);
        var captures = new Dictionary<(AstLambda Lambda, LocalSymbol Local), CaptureBuilder>();

        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!binding.CapturesOuterLocal || binding.Lookup.Locals.IsEmpty) { continue; }

            AstSnapshot file = files[binding.Reference];
            LambdaCaptureAccess access = AccessOf(binding.Reference, file);
            foreach (var local in binding.Lookup.Locals.Where(local => local.Kind != LocalSymbolKind.ConstructorProperty))
            {
                var owners = CrossingLambdas(scopeOf[binding.Reference], local.ScopeId, scopesById);
                foreach (var lambda in owners)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = (lambda, local);
                    if (!captures.TryGetValue(key, out var capture))
                    {
                        capture = new(lambda, local);
                        captures.Add(key, capture);
                    }
                    capture.References.Add(binding.Reference);
                    capture.Access |= access;
                }
            }
        }

        return captures.Values
            .OrderBy(capture => capture.Lambda.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(capture => capture.Lambda.Origin.Span.Start)
            .ThenBy(capture => capture.Local.Id)
            .Select(capture => new LambdaCapture(capture.Lambda, capture.Local,
                capture.References.OrderBy(reference => reference.Origin.Span.Start).ToImmutableArray(),
                capture.Access))
            .ToImmutableArray();
    }

    private static ImmutableArray<AstLambda> CrossingLambdas(int startScope, int localScope,
        IReadOnlyDictionary<int, LexicalScope> scopes)
    {
        var owners = ImmutableArray.CreateBuilder<AstLambda>();
        int? current = startScope;
        while (current is { } id && id != localScope)
        {
            var scope = scopes[id];
            if (scope.Owner is AstLambda lambda) { owners.Add(lambda); }
            current = scope.ParentId;
        }
        return owners.ToImmutable();
    }

    private static LambdaCaptureAccess AccessOf(AstAtom reference, AstSnapshot file)
    {
        for (AstNode? current = reference; current is not null; current = file.GetParent(current))
        {
            if (current is AstAssignment assignment && IsDirectLocalLocation(reference, assignment.Location))
            {
                return assignment.Operation == "=" ? LambdaCaptureAccess.Write : LambdaCaptureAccess.ReadWrite;
            }
            if (current is AstOperation operation && operation.Operation is "++" or "--"
                && operation.Operands.Any(operand => IsDirectLocalLocation(reference, operand)))
            { return LambdaCaptureAccess.ReadWrite; }
            if (current is AstCall call)
            {
                var argument = call.Arguments.FirstOrDefault(candidate =>
                    IsDirectLocalLocation(reference, candidate.Value));
                if (argument is not null)
                {
                    return argument.Mode switch
                    {
                        AstArgumentMode.Ref => LambdaCaptureAccess.ReadWrite,
                        AstArgumentMode.Out or AstArgumentMode.OutVariable => LambdaCaptureAccess.Write,
                        _ => LambdaCaptureAccess.Read,
                    };
                }
            }
            if (current is AstLambda || current.SyntaxKind == OpenDot.Sol.Cst.NodeKind.AnonymousFunctionExpression)
            { break; }
        }
        return LambdaCaptureAccess.Read;
    }

    private static bool IsDirectLocalLocation(AstAtom reference, AstNode? location)
    {
        if (location is null) { return false; }
        if (ReferenceEquals(reference, location)) { return true; }
        if (location is AstSequence { IsGrouping: true } group)
        {
            var elements = group.Elements.ToArray();
            return elements.Length == 1 && IsDirectLocalLocation(reference, elements[0]);
        }
        return false;
    }
}
