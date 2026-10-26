// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class TypeBindingBuilder
{
    private ResolvedType? FindParameter(string name, AstType origin)
    {
        var file = _owners[origin];
        for (AstNode? owner = origin; owner is not null; owner = file.GetParent(owner))
        {
            if (owner is AstTypeDeclaration { IsCompanion: true }) { break; }
            var parameters = owner switch
            {
                AstTypeDeclaration type => type.TypeParameters,
                AstCallable callable => callable.TypeParameters,
                _ => [],
            };
            int index = 0;
            foreach (var parameter in parameters)
            {
                if (parameter.Name == name)
                {
                    return Parameters[(AstDeclaration)owner][index];
                }
                index++;
            }
        }
        return null;
    }

    private IEnumerable<DeclarationSymbol> FindTypes(string name, AstType origin)
    {
        var file = _owners[origin];
        string[] parts = name.Split('.');
        if (parts.Length == 1)
        {
            for (AstNode? node = origin; node is not null; node = file.GetParent(node))
            {
                if (node is not AstTypeDeclaration declaration) { continue; }
                var container = Session.GetDeclaredSymbol(declaration);
                if (container is null) { continue; }
                var found = container.FindMembers(name).Where(s => s.Kind == SymbolKind.Type).ToArray();
                if (container.Name == name) { found = [.. found, container]; }
                if (found.Length != 0) { return found; }
            }
            return Session.Lookup(file.Root, name).Candidates.Where(s => s.Kind == SymbolKind.Type);
        }
        // Try explicit module/alias qualification, longest visible qualifier first.
        for (int prefix = parts.Length - 1; prefix > 0; prefix--)
        {
            var lookup = Session.LookupQualified(file.Root, string.Join('.', parts.Take(prefix)), parts[prefix]);
            var candidates = lookup.Candidates.Where(s => s.Kind == SymbolKind.Type);
            for (int index = prefix + 1; index < parts.Length; index++)
            {
                string member = parts[index];
                candidates = candidates.SelectMany(s => s.FindMembers(member)).Where(s => s.Kind == SymbolKind.Type).ToArray();
            }
            var found = candidates.ToArray();
            if (found.Length != 0) { return found; }
        }
        var nested = FindTypes(parts[0], origin);
        foreach (string member in parts.Skip(1))
        {
            nested = nested.SelectMany(s => s.FindMembers(member)).Where(s => s.Kind == SymbolKind.Type).ToArray();
        }
        return nested;
    }

    private void ValidateResolvedSignatures()
    {
        foreach (var module in Session.Modules.Values)
        {
            var pending = new Stack<ImmutableArray<DeclarationSymbol>>();
            pending.Push(module.Declarations);
            while (pending.TryPop(out var members))
            {
                _cancellation.ThrowIfCancellationRequested();
                var keys = new Dictionary<string, DeclarationSymbol>(StringComparer.Ordinal);
                foreach (var symbol in members)
                {
                    if (!symbol.Members.IsEmpty) { pending.Push(symbol.Members); }
                    if (symbol.Parts.FirstOrDefault() is not AstCallable callable) { continue; }
                    var parameters = callable.Parameters.Select(p => p.Type is null ? null : References[p.Type]).ToArray();
                    if (parameters.Any(t => t is null || t.IsError || t.Kind == ResolvedTypeKind.Parameter)) { continue; }
                    string variant = callable is AstFunction { IsCheckedOperator: true } ? "checked " : "";
                    string key = variant + symbol.Name + "`" + symbol.Arity + "(" + string.Join(',', callable.Parameters.Select((p, index) =>
                        (p.PassingMode switch
                        {
                            AstPassingMode.Ref or AstPassingMode.Out => "ref:",
                            AstPassingMode.Vararg => "vararg:",
                            _ => "value:",
                        }) + parameters[index]!.Id)) + ")";
                    if (keys.TryGetValue(key, out var previous) && !ReferenceEquals(symbol, previous))
                    {
                        Report("SOL2003", "Duplicate signature after resolving imported type names: " + symbol.Name, callable);
                    }
                    else { keys.TryAdd(key, symbol); }
                }
            }
        }
    }
}
