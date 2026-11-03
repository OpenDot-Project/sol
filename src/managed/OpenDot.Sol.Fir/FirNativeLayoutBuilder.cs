// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Fir;

// Computes source-corresponding native storage. A fresh builder owns one bounded graph; nothing
// is published until every scheduled type has a complete layout. Initializer sequencing is not
// inferred from these offsets and remains in the source construction records.
internal sealed class FirNativeLayoutBuilder(BodyAnalysis analysis, CancellationToken cancellation,
    Func<ConstructionSlot, ResolvedType?>? delegateStorageType = null, Func<string, ulong>? identityHash = null)
{
    internal const int MaximumTypes = 4096;
    private readonly Dictionary<ResolvedType, FirNativeTypeLayout> _layouts = [];
    private readonly HashSet<ResolvedType> _scheduled = [];
    private readonly HashSet<ResolvedType> _activeBases = [];
    private readonly Dictionary<ulong, string> _identities = [];
    private readonly Queue<ResolvedType> _pending = [];
    private bool _started;
    private long _storageCount;

    internal ImmutableArray<FirNativeTypeLayout> Build(IEnumerable<ResolvedType> roots)
    {
        cancellation.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(roots);
        if (_started) { throw new InvalidOperationException("A native layout graph builder can be used only once."); }
        _started = true;
        foreach (var root in roots) { ScheduleReferences(root); }
        while (_pending.TryDequeue(out var type)) { Layout(type, 0); }
        cancellation.ThrowIfCancellationRequested();
        return _layouts.Values.OrderBy(layout => layout.Identity, StringComparer.Ordinal).ToImmutableArray();
    }

    private void ScheduleReferences(ResolvedType type)
    {
        cancellation.ThrowIfCancellationRequested();
        analysis.Types.ValidateOwnership(type);
        // Canonical encoding also checks closedness and the complete type/effect depth budget.
        _ = FirNativeIdentity.Type(analysis.Types, type, cancellation);
        var pending = new Stack<ResolvedType>();
        var visited = new HashSet<ResolvedType>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!visited.Add(current)) { continue; }
            if (current.Kind == ResolvedTypeKind.Nominal && _scheduled.Add(current))
            {
                if (_scheduled.Count > MaximumTypes) { throw Limit("Native constructed-type count exceeds 4096."); }
                _pending.Enqueue(current);
            }
            foreach (var argument in current.Arguments.Concat(current.Effects)) { pending.Push(argument); }
        }
    }

    private FirNativeTypeLayout Layout(ResolvedType owner, int depth)
    {
        cancellation.ThrowIfCancellationRequested();
        analysis.Types.ValidateOwnership(owner);
        if (_layouts.TryGetValue(owner, out var existing)) { return existing; }
        if (depth > 128) { throw Limit("Native base-prefix depth exceeds 128."); }
        if (!_activeBases.Add(owner)) { throw Unsupported("A cyclic base graph cannot have native storage."); }
        try
        {
            if (owner.Declaration?.Parts[0] is not AstTypeDeclaration declaration
                || declaration.SyntaxKind is not (NodeKind.ClassDeclaration or NodeKind.ObjectDeclaration or NodeKind.InterfaceDeclaration))
            { throw Unsupported("Native storage requires a class, object or interface declaration."); }
            string identity = FirNativeIdentity.Type(analysis.Types, owner, cancellation);
            bool isInterface = declaration.SyntaxKind == NodeKind.InterfaceDeclaration;
            var bases = analysis.Types.GetDirectBases(owner, cancellation);
            foreach (var parent in bases) { ScheduleReferences(parent); }
            var concrete = bases.Where(parent => parent.Declaration?.Parts[0].SyntaxKind == NodeKind.ClassDeclaration).ToArray();
            if (concrete.Length > 1) { throw Unsupported("A native class requires a single concrete base prefix."); }
            ResolvedType? baseType = concrete.SingleOrDefault();
            var prefix = baseType is null ? null : Layout(baseType, depth + 1);
            var runtime = RuntimeLayout(owner);
            ulong typeId = runtime?.Id ?? (identityHash ?? FirNativeIdentity.TypeId)(identity);
            if (_identities.TryGetValue(typeId, out string? previous) && previous != identity)
            { throw new FirNativeLayoutException("SOL2402", "Native type identity hash collision."); }
            _identities[typeId] = identity;
            var storage = ImmutableArray.CreateBuilder<FirNativeStorage>();
            var references = ImmutableArray.CreateBuilder<uint>();
            if (prefix is not null) { storage.AddRange(prefix.Storage); references.AddRange(prefix.ReferenceOffsets); }
            long end = prefix?.Size ?? 16;
            if (runtime is { } fixedLayout)
            {
                end = fixedLayout.Size;
                references.Clear();
                if (fixedLayout.Id is 3 or 4 or 5)
                { references.Add(16); references.Add(24); references.Add(32); }
            }
            else if (!isInterface)
            {
                var slots = ConstructionStorage.ForOwner(owner, cancellation);
                _storageCount += slots.Length;
                if (_storageCount > 16_000_000) { throw Limit("Native storage expansion exceeds the node budget."); }
                var primary = owner.Declaration.Parts.OfType<AstTypeDeclaration>()
                    .SelectMany(part => part.PrimaryConstructor?.Parameters ?? [])
                    .Where(parameter => parameter.IsProperty).Select((parameter, index) => (parameter.Name, index))
                    .ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.Ordinal);
                var ordered = slots.Select(slot => (Slot: slot, Type: StorageType(slot)))
                    .Select(pair => (pair.Slot, pair.Type, Identity: StorageIdentity(identity, pair.Slot, pair.Type)))
                    .OrderBy(pair => pair.Slot.Kind == ConstructionSlotKind.PrimaryProperty ? 0 : 1)
                    .ThenBy(pair => pair.Slot.Kind == ConstructionSlotKind.PrimaryProperty ? primary[pair.Slot.Name] : 0)
                    .ThenBy(pair => pair.Identity, StringComparer.Ordinal);
                foreach (var (slot, value, key) in ordered)
                {
                    cancellation.ThrowIfCancellationRequested();
                    ScheduleReferences(value);
                    var shape = StorageShape(value);
                    end = Align(end, shape.Alignment);
                    if (end > uint.MaxValue - shape.Size) { throw Limit("Native field offsets exceed the descriptor range."); }
                    storage.Add(new(slot, value, key, end, shape.Size, shape.Alignment, shape.Reference, shape.Cell));
                    if (shape.Reference) { references.Add(checked((uint)end)); }
                    end = checked(end + shape.Size);
                }
                end = Align(end, 8);
            }
            var result = new FirNativeTypeLayout(owner, identity, typeId, end, isInterface ? 8 : 16,
                baseType, storage.ToImmutable(), references.Distinct().Order().ToImmutableArray(), isInterface, runtime is not null);
            _layouts.Add(owner, result);
            return result;
        }
        finally { _activeBases.Remove(owner); }
    }

    private ResolvedType StorageType(ConstructionSlot slot)
    {
        var substitutions = analysis.Sources.Types.GetTypeParameters(slot.Owner.Declaration!.Parts[0])
            .Select((parameter, index) => (parameter, argument: slot.Owner.Arguments[index]))
            .ToDictionary(pair => pair.parameter, pair => pair.argument);
        ResolvedType? written = slot.Declaration switch
        {
            AstProperty property when slot.Kind == ConstructionSlotKind.PropertyDelegate => delegateStorageType?.Invoke(slot),
            AstProperty { Type: { } type } => analysis.Sources.Types.GetType(type),
            AstProperty { Initializer: AstExpression initializer } => analysis.GetExpression(initializer)?.Type,
            AstConstructor constructor when slot.Kind == ConstructionSlotKind.PrimaryProperty
                => constructor.Parameters.Single(parameter => parameter.Name == slot.Name).Type is { } type
                    ? analysis.Sources.Types.GetType(type) : null,
            _ when slot.Kind == ConstructionSlotKind.InterfaceDelegate
                => slot.Declaration.Children.OfType<AstType>().SingleOrDefault() is { } type
                    ? analysis.Sources.Types.GetType(type) : null,
            _ => null,
        };
        if (written is null) { throw Unsupported("Native storage is missing an exact source value/delegate type."); }
        return analysis.Types.Substitute(written, substitutions, cancellation);
    }

    private string StorageIdentity(string owner, ConstructionSlot slot, ResolvedType value)
        => owner + ";storage:" + slot.Kind + ":" + (slot.Kind == ConstructionSlotKind.InterfaceDelegate ? "by" : slot.Name)
            + ":" + FirNativeIdentity.Type(analysis.Types, value, cancellation);

    private static (int Size, int Alignment, bool Reference, bool Cell) StorageShape(ResolvedType type)
    {
        if (type.Kind == ResolvedTypeKind.Nullable && type.Arguments is [var payload])
        {
            if (payload.Kind == ResolvedTypeKind.Function) { return (8, 8, false, false); }
            if (payload.Kind is ResolvedTypeKind.Nominal or ResolvedTypeKind.Array) { return StorageShape(payload); }
            throw Unsupported("Nullable native storage requires a supported reference or function value.");
        }
        if (type.Kind == ResolvedTypeKind.Array) { return (8, 8, true, false); }
        if (type.Kind == ResolvedTypeKind.Function) { return (8, 8, false, false); }
        if (type.Kind == ResolvedTypeKind.Nominal && type.Declaration?.Parts[0].SyntaxKind
            is NodeKind.ClassDeclaration or NodeKind.ObjectDeclaration or NodeKind.InterfaceDeclaration)
        { return (8, 8, true, type.Declaration.Parts[0].SyntaxKind == NodeKind.InterfaceDeclaration); }
        int size = type.Kind == ResolvedTypeKind.Primitive ? type.Name switch
        {
            "bool" or "byte" or "sbyte" => 1, "short" => 2,
            "int" or "uint" or "single" => 4, "long" or "ulong" or "double" => 8,
            _ => 0,
        } : 0;
        return size == 0 ? throw Unsupported("Storage type is outside the native value family.") : (size, size, false, false);
    }

    private static (ulong Id, int Size)? RuntimeLayout(ResolvedType type)
        => type.Declaration is { ModuleName: "std.core", Arity: 0 } declaration ? declaration.Name switch
        {
            "Object" => (0, 16), "String" => (1, 24), "Throwable" => (3, 40),
            "Exception" => (4, 40), "RuntimeException" => (5, 40), _ => null,
        } : null;

    private static long Align(long value, int alignment) => checked((value + alignment - 1) & -(long)alignment);
    private static FirNativeLayoutException Unsupported(string message) => new("SOL2400", message);
    private static FirNativeLayoutException Limit(string message) => new("SOL2403", message);
}
