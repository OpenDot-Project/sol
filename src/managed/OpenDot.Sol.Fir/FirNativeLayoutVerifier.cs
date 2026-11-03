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

// Checks supplied metadata against source facts. It deliberately does not run the layout builder:
// a forged offset/root map must not become authoritative merely because its record is well typed.
internal sealed class FirNativeLayoutVerifier(BodyAnalysis analysis, CancellationToken cancellation,
    Func<ConstructionSlot, ResolvedType?>? delegateStorageType = null)
{
    private readonly Dictionary<ResolvedType, FirNativeTypeLayout> _types = [];
    private readonly HashSet<ResolvedType> _active = [];
    private readonly HashSet<ResolvedType> _checked = [];
    private long _storageCount;
    private bool _started;

    internal void Verify(ImmutableArray<FirNativeTypeLayout> layouts)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_started) { throw new InvalidOperationException("A native layout verifier can be used only once."); }
        _started = true;
        if (layouts.IsDefault) { Invalid("A native layout graph cannot be default."); }
        if (layouts.Length > 4096) { throw new FirNativeLayoutException("SOL2403", "Native constructed-type count exceeds 4096."); }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<ulong>();
        foreach (var layout in layouts)
        {
            cancellation.ThrowIfCancellationRequested();
            analysis.Types.ValidateOwnership(layout.Type);
            if (!_types.TryAdd(layout.Type, layout) || !identities.Add(layout.Identity) || !ids.Add(layout.TypeId))
            { Invalid("Duplicate native type, identity or descriptor ID."); }
        }
        foreach (var layout in layouts) { Check(layout, 0); }
    }

    private void Check(FirNativeTypeLayout layout, int depth)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_checked.Contains(layout.Type)) { return; }
        if (depth > 128) { throw new FirNativeLayoutException("SOL2403", "Native base-prefix depth exceeds 128."); }
        if (!_active.Add(layout.Type)) { Invalid("A native base-prefix graph is cyclic."); }
        try
        {
            if (layout.Type.Kind != ResolvedTypeKind.Nominal
                || layout.Type.Declaration?.Parts[0] is not AstTypeDeclaration declaration
                || declaration.SyntaxKind is not (NodeKind.ClassDeclaration or NodeKind.ObjectDeclaration or NodeKind.InterfaceDeclaration))
            { Invalid("A native descriptor must identify a source class, object or interface."); return; }
            string identity = FirNativeIdentity.Type(analysis.Types, layout.Type, cancellation);
            bool isInterface = declaration.SyntaxKind == NodeKind.InterfaceDeclaration;
            var runtime = FixedLayout(layout.Type);
            if (layout.Identity != identity || layout.TypeId != (runtime?.Id ?? FirNativeIdentity.TypeId(identity))
                || layout.IsInterface != isInterface || layout.IsRuntimeOwned != (runtime is not null)
                || layout.Alignment != (isInterface ? 8 : 16))
            { Invalid("Native type identity, ownership or alignment does not match its source contract."); }
            var parents = analysis.Types.GetDirectBases(layout.Type, cancellation);
            foreach (var parent in parents) { Require(parent); }
            var concrete = parents.Where(parent => parent.Declaration?.Parts[0].SyntaxKind == NodeKind.ClassDeclaration).ToArray();
            if (concrete.Length > 1 || layout.Base != concrete.SingleOrDefault())
            { Invalid("Native base prefix does not match the constructed source base."); }
            FirNativeTypeLayout? prefix = layout.Base is null ? null : Require(layout.Base);
            if (prefix is not null) { Check(prefix, depth + 1); }
            if (layout.Storage.IsDefault || layout.ReferenceOffsets.IsDefault) { Invalid("Native storage/root inventory cannot be default."); }
            _storageCount += layout.Storage.Length;
            if (_storageCount > 16_000_000) { throw new FirNativeLayoutException("SOL2403", "Native storage verification exceeds its node budget."); }
            int inherited = prefix?.Storage.Length ?? 0;
            if (layout.Storage.Length < inherited || prefix is not null && !layout.Storage.Take(inherited).SequenceEqual(prefix.Storage))
            { Invalid("Inherited storage must preserve the complete base prefix."); }
            var roots = new List<uint>(prefix?.ReferenceOffsets ?? []);
            long end = prefix?.Size ?? 16;
            if (runtime is { } fixedLayout)
            {
                if (layout.Storage.Length != inherited) { Invalid("Runtime-owned storage cannot be redefined by source metadata."); }
                end = fixedLayout.Size;
                roots.Clear();
                if (fixedLayout.Id is 3 or 4 or 5) { roots.AddRange([16u, 24u, 32u]); }
            }
            else if (isInterface)
            {
                if (layout.Storage.Length != 0) { Invalid("An interface descriptor cannot own instance fields."); }
            }
            else
            {
                var slots = ConstructionStorage.ForOwner(layout.Type, cancellation);
                var primary = layout.Type.Declaration.Parts.OfType<AstTypeDeclaration>()
                    .SelectMany(part => part.PrimaryConstructor?.Parameters ?? [])
                    .Where(parameter => parameter.IsProperty).Select(parameter => parameter.Name).ToArray();
                var expected = slots.Select(slot => (Slot: slot, Type: SourceType(slot)))
                    .Select(pair => (pair.Slot, pair.Type, Identity: identity + ";storage:" + pair.Slot.Kind + ":"
                        + (pair.Slot.Kind == ConstructionSlotKind.InterfaceDelegate ? "by" : pair.Slot.Name)
                        + ":" + FirNativeIdentity.Type(analysis.Types, pair.Type, cancellation)))
                    .OrderBy(pair => pair.Slot.Kind == ConstructionSlotKind.PrimaryProperty ? 0 : 1)
                    .ThenBy(pair => pair.Slot.Kind == ConstructionSlotKind.PrimaryProperty ? Array.IndexOf(primary, pair.Slot.Name) : 0)
                    .ThenBy(pair => pair.Identity, StringComparer.Ordinal).ToArray();
                if (layout.Storage.Length != inherited + expected.Length) { Invalid("Native storage inventory differs from source storage."); }
                for (int index = 0; index < expected.Length; index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var field = layout.Storage[inherited + index];
                    var source = expected[index];
                    CheckReferences(source.Type);
                    var shape = Shape(source.Type);
                    end = checked((end + shape.Size - 1) & -(long)shape.Size);
                    if (field.Source != source.Slot || field.ValueType != source.Type || field.Identity != source.Identity
                        || field.Offset != end || field.Size != shape.Size || field.Alignment != shape.Size
                        || field.IsGcReference != shape.Root || field.UsesInterfaceCell != shape.Cell)
                    { Invalid("Native field identity, value representation or offset differs from its source contract."); }
                    if (shape.Root) { roots.Add(checked((uint)end)); }
                    end = checked(end + shape.Size);
                }
                end = checked((end + 7) & ~7L);
            }
            if (layout.Size != end || !layout.ReferenceOffsets.SequenceEqual(roots.Distinct().Order()))
            { Invalid("Native instance size or precise GC root map is inconsistent."); }
            CheckReferences(layout.Type);
            _checked.Add(layout.Type);
        }
        finally { _active.Remove(layout.Type); }
    }

    private FirNativeTypeLayout Require(ResolvedType type)
    {
        analysis.Types.ValidateOwnership(type);
        if (!_types.TryGetValue(type, out var layout)) { Invalid("A referenced constructed type has no native descriptor."); }
        return layout!;
    }

    private void CheckReferences(ResolvedType root)
    {
        var visited = new HashSet<ResolvedType>();
        var pending = new Stack<ResolvedType>(); pending.Push(root);
        while (pending.TryPop(out var type))
        {
            cancellation.ThrowIfCancellationRequested();
            analysis.Types.ValidateOwnership(type);
            if (!visited.Add(type)) { continue; }
            if (type.Kind == ResolvedTypeKind.Nominal) { Require(type); }
            foreach (var child in type.Arguments.Concat(type.Effects)) { pending.Push(child); }
        }
    }

    private ResolvedType SourceType(ConstructionSlot slot)
    {
        ResolvedType? type = slot.Declaration switch
        {
            AstProperty when slot.Kind == ConstructionSlotKind.PropertyDelegate => delegateStorageType?.Invoke(slot),
            AstProperty { Type: { } written } => analysis.Sources.Types.GetType(written),
            AstProperty { Initializer: AstExpression initializer } => analysis.GetExpression(initializer)?.Type,
            AstConstructor constructor when slot.Kind == ConstructionSlotKind.PrimaryProperty
                => constructor.Parameters.Single(parameter => parameter.Name == slot.Name).Type is { } written
                    ? analysis.Sources.Types.GetType(written) : null,
            _ when slot.Kind == ConstructionSlotKind.InterfaceDelegate
                => slot.Declaration.Children.OfType<AstType>().SingleOrDefault() is { } written
                    ? analysis.Sources.Types.GetType(written) : null,
            _ => null,
        };
        if (type is null) { Invalid("Native storage lacks an exact source type."); }
        var parameters = analysis.Sources.Types.GetTypeParameters(slot.Owner.Declaration!.Parts[0]);
        var substitution = parameters.Select((parameter, index) => (parameter, slot.Owner.Arguments[index]))
            .ToDictionary(pair => pair.parameter, pair => pair.Item2);
        return analysis.Types.Substitute(type!, substitution, cancellation);
    }

    private static (int Size, bool Root, bool Cell) Shape(ResolvedType type)
    {
        if (type.Kind == ResolvedTypeKind.Nullable && type.Arguments is [var payload])
        {
            if (payload.Kind is not (ResolvedTypeKind.Nominal or ResolvedTypeKind.Array or ResolvedTypeKind.Function))
            { Invalid("A nullable storage payload must use a reference representation."); }
            return Shape(payload);
        }
        if (type.Kind == ResolvedTypeKind.Array) { return (8, true, false); }
        if (type.Kind == ResolvedTypeKind.Function) { return (8, false, false); }
        if (type.Kind == ResolvedTypeKind.Nominal && type.Declaration?.Parts[0].SyntaxKind
            is NodeKind.ClassDeclaration or NodeKind.ObjectDeclaration or NodeKind.InterfaceDeclaration)
        { return (8, true, type.Declaration.Parts[0].SyntaxKind == NodeKind.InterfaceDeclaration); }
        int size = type.Kind == ResolvedTypeKind.Primitive ? type.Name switch
        {
            "bool" or "byte" or "sbyte" => 1, "short" => 2, "int" or "uint" or "single" => 4,
            "long" or "ulong" or "double" => 8, _ => 0,
        } : 0;
        if (size == 0) { Invalid("Unsupported native storage representation."); }
        return (size, false, false);
    }

    private static (ulong Id, int Size)? FixedLayout(ResolvedType type)
        => type.Declaration is { ModuleName: "std.core", Arity: 0 } declaration ? declaration.Name switch
        {
            "Object" => (0, 16), "String" => (1, 24), "Throwable" => (3, 40),
            "Exception" => (4, 40), "RuntimeException" => (5, 40), _ => null,
        } : null;

    private static void Invalid(string message) => throw new FirNativeLayoutException("SOL2402", message);
}
