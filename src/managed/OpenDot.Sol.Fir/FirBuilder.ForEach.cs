// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Fir;

internal sealed partial class FirBuilder
{
    private FirStatement? ForEach(AstFor loop)
    {
        var binding = loop.Binding;
        var iterable = loop.Iterable;
        var body = loop.Body;
        if (binding is null || iterable is null || body is null)
        {
            Unsupported(loop, "Array iteration shape");
            return null;
        }

        var names = binding.BindingNames.ToArray();
        if (names is not [var name] || string.IsNullOrWhiteSpace(name) || name == "_"
            || binding.Type is not null || binding.Initializer is not null || binding.Delegate is not null)
        {
            Unsupported(loop, "Single-name array iteration binding");
            return null;
        }

        var source = _analysis.GetExpression(iterable);
        var arrayType = source?.Type is { } sourceType ? Specialize(sourceType) : null;
        if (source is null || source.Status != BodyBindingStatus.Bound
            || arrayType is null || arrayType.Kind != ResolvedTypeKind.Array || arrayType.Arguments.Length != 1
            || !FirVerifier.Supported(arrayType))
        {
            Unsupported(loop, "Dynamic array iteration in the verified element subset");
            return null;
        }

        var scopes = _analysis.Sources.Lexical.Scopes.Where(scope => ReferenceEquals(scope.Owner, loop)).ToArray();
        var locals = scopes.Length == 1
            ? scopes[0].Symbols.Where(symbol => symbol.Kind == LocalSymbolKind.Variable
                && symbol.Name == name && ReferenceEquals(symbol.Origin.Element, binding.Origin.Element)).ToArray()
            : [];
        if (locals is not [var local] || local.IsMutable != binding.IsMutable || _slots.ContainsKey(local))
        {
            Unsupported(loop, "Iteration binding identity");
            return null;
        }

        // Keep the iterable as one FIR expression; its source side effects execute once before entry.
        var array = Expression(iterable);
        var elementType = arrayType.Arguments[0];
        var slot = new FirSlot(_slots.Count, local.Name, elementType, local.IsMutable,
            AstPassingMode.Value, false, local.Origin);
        _slots.Add(local, slot);
        var firBody = Block(body);
        return new FirArrayForEach(slot.Id, array, firBody, loop.Origin);
    }
}
