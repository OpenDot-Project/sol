// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

/// <summary>Read and write operations on a local symbol referenced by a local named function.</summary>
[Flags]
public enum LocalFunctionCaptureAccess
{
    /// <summary>The local symbol is read.</summary>
    Read = 1,
    /// <summary>The local symbol is assigned or passed as <c>out</c>.</summary>
    Write = 2,
    /// <summary>The local symbol is both read and written, or passed as <c>ref</c>.</summary>
    ReadWrite = Read | Write,
}

/// <summary>
/// One resolved local symbol referenced across a local named-function boundary. This inventory
/// records lexical identity and access only; it makes no closure ABI, storage, or lifetime claim.
/// </summary>
/// <param name="Function">The nested local function that references the symbol.</param>
/// <param name="Local">The exact lexical declaration identity.</param>
/// <param name="References">Resolved source references in byte-offset order.</param>
/// <param name="Access">The union of read and write operations at those references.</param>
public sealed record LocalFunctionCapture(
    AstFunction Function,
    LocalSymbol Local,
    ImmutableArray<AstAtom> References,
    LocalFunctionCaptureAccess Access);

internal static class LocalFunctionCaptureAnalysis
{
    private sealed class CaptureBuilder(AstFunction function, LocalSymbol local)
    {
        internal AstFunction Function { get; } = function;
        internal LocalSymbol Local { get; } = local;
        internal List<AstAtom> References { get; } = [];
        internal LocalFunctionCaptureAccess Access { get; set; }
    }

    internal static ImmutableArray<LocalFunctionCapture> Create(
        ImmutableArray<LexicalScope> scopes,
        Dictionary<AstNode, int> scopeOf,
        Dictionary<AstNode, AstSnapshot> files,
        IEnumerable<NameBinding> bindings,
        CancellationToken cancellationToken)
    {
        var functionsByOrigin = new Dictionary<object, AstFunction>(ReferenceEqualityComparer.Instance);
        foreach (var function in files.Keys.OfType<AstFunction>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            functionsByOrigin.TryAdd(function.Origin.Element, function);
        }

        var functionByLocal = new Dictionary<LocalSymbol, AstFunction>(ReferenceEqualityComparer.Instance);
        foreach (var local in scopes.SelectMany(scope => scope.Symbols).Where(symbol => symbol.Kind == LocalSymbolKind.Function))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (functionsByOrigin.TryGetValue(local.Origin.Element, out var function))
            { functionByLocal.TryAdd(local, function); }
        }
        IEqualityComparer<AstFunction> functionComparer = ReferenceEqualityComparer.Instance;
        var localFunctions = new HashSet<AstFunction>(functionByLocal.Values, functionComparer);
        var scopesById = scopes.ToDictionary(scope => scope.Id);
        var captures = new Dictionary<(AstFunction Function, LocalSymbol Local), CaptureBuilder>();

        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (binding.Lookup.Locals.IsEmpty) { continue; }
            foreach (var local in binding.Lookup.Locals.Where(symbol => symbol.Kind != LocalSymbolKind.ConstructorProperty))
            {
                foreach (var function in CrossingLocalFunctions(scopeOf[binding.Reference], local.ScopeId,
                    scopesById, localFunctions, cancellationToken))
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
            .Select(capture => new LocalFunctionCapture(capture.Function, capture.Local,
                capture.References.OrderBy(reference => reference.Origin.Source.Path, StringComparer.Ordinal)
                    .ThenBy(reference => reference.Origin.Span.Start).ToImmutableArray(), capture.Access))
            .ToImmutableArray();
    }

    private static ImmutableArray<AstFunction> CrossingLocalFunctions(int startScope, int localScope,
        IReadOnlyDictionary<int, LexicalScope> scopes, IReadOnlySet<AstFunction> localFunctions,
        CancellationToken cancellationToken)
    {
        var owners = ImmutableArray.CreateBuilder<AstFunction>();
        for (int? current = startScope; current is { } id && id != localScope; current = scopes[id].ParentId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scopes[id].Owner is AstFunction function && localFunctions.Contains(function))
            { owners.Add(function); }
        }
        return owners.ToImmutable();
    }

    private static LocalFunctionCaptureAccess AccessOf(AstAtom reference, AstSnapshot file)
    {
        for (AstNode? current = reference; current is not null; current = file.GetParent(current))
        {
            if (current is AstAssignment assignment && IsDirectLocalLocation(reference, assignment.Location))
            {
                return assignment.Operation == "=" ? LocalFunctionCaptureAccess.Write
                    : LocalFunctionCaptureAccess.ReadWrite;
            }
            if (current is AstOperation operation && operation.Operation is "++" or "--"
                && operation.Operands.Any(operand => IsDirectLocalLocation(reference, operand)))
            { return LocalFunctionCaptureAccess.ReadWrite; }
            if (current is AstCall call)
            {
                var argument = call.Arguments.FirstOrDefault(candidate =>
                    IsDirectLocalLocation(reference, candidate.Value));
                if (argument is not null)
                {
                    return argument.Mode switch
                    {
                        AstArgumentMode.Ref => LocalFunctionCaptureAccess.ReadWrite,
                        AstArgumentMode.Out or AstArgumentMode.OutVariable => LocalFunctionCaptureAccess.Write,
                        _ => LocalFunctionCaptureAccess.Read,
                    };
                }
            }
            if (current is AstCallable or AstLambda or AstAnonymousFunction) { break; }
        }
        return LocalFunctionCaptureAccess.Read;
    }

    private static bool IsDirectLocalLocation(AstAtom reference, AstNode? location)
    {
        if (location is null) { return false; }
        if (ReferenceEquals(reference, location)) { return true; }
        if (location is AstSequence { IsGrouping: true } group)
        {
            var elements = group.Elements.Take(2).ToArray();
            return elements.Length == 1 && IsDirectLocalLocation(reference, elements[0]);
        }
        return false;
    }
}
