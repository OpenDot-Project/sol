// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Analysis;

internal sealed partial class TypeBindingBuilder
{
    private void ValidateBases()
    {
        var definitions = new List<DeclarationSymbol>();
        var pending = new Stack<DeclarationSymbol>(Session.Modules.Values.SelectMany(m => m.Declarations));
        while (pending.TryPop(out var symbol))
        {
            _cancellation.ThrowIfCancellationRequested();
            if (symbol.Kind == SymbolKind.Type) { definitions.Add(symbol); }
            foreach (var member in symbol.Members) { pending.Push(member); }
        }
        var edges = definitions.ToDictionary(s => s.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => new List<string>(), StringComparer.Ordinal);
        foreach (var symbol in definitions)
        {
            var concrete = new HashSet<int>();
            foreach (var part in symbol.Parts.OfType<AstTypeDeclaration>())
            {
                foreach (var entry in part.Bases)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    var syntax = entry.Children.OfType<AstType>().FirstOrDefault();
                    if (syntax is null) { continue; }
                    var type = References[syntax];
                    if (type.IsError) { continue; }
                    var target = type.Declaration;
                    if (type.Kind != ResolvedTypeKind.Nominal || target is null)
                    {
                        Report("SOL2113", "A base must be a non-null nominal class or interface.", syntax);
                        continue;
                    }
                    var targetPart = (AstTypeDeclaration)target.Parts[0];
                    bool isInterface = targetPart.SyntaxKind == NodeKind.InterfaceDeclaration;
                    bool allowClass = part.SyntaxKind is NodeKind.ClassDeclaration or NodeKind.ObjectDeclaration;
                    if (!isInterface && (!allowClass || targetPart.SyntaxKind != NodeKind.ClassDeclaration))
                    {
                        Report("SOL2113", "This declaration cannot inherit the specified base kind.", syntax);
                    }
                    if (!isInterface)
                    {
                        concrete.Add(type.Id);
                        bool intrinsicObject = target.ModuleName == "std.core" && target.Name == "Object";
                        if (!intrinsicObject && !targetPart.Modifiers.Any(m => m is "open" or "abstract" or "sealed"))
                        {
                            Report("SOL2113", "A final class cannot be inherited.", syntax);
                        }
                    }
                    if (entry.Children.Any(n => n.SyntaxKind == NodeKind.Delegation) && !isInterface)
                    {
                        Report("SOL2113", "Base delegation requires an interface.", syntax);
                    }
                    edges[symbol.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]
                        .Add(target.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            if (concrete.Count > 1)
            {
                Report("SOL2113", "A type can have only one compatible concrete base across all parts.", symbol.Parts[^1]);
            }
        }
        var graph = ModuleGraph.Create(edges.Select(pair =>
            new KeyValuePair<string, IEnumerable<string>>(pair.Key, pair.Value)), _cancellation);
        foreach (var group in graph.CompilationGroups.Where(g => g.Length > 1
            || g.Length == 1 && graph.Edges[g[0]].Contains(g[0], StringComparer.Ordinal)))
        {
            foreach (var member in group)
            {
                var symbol = definitions.First(s => s.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == member);
                Report("SOL2114", "An inheritance cycle includes " + symbol.QualifiedName, symbol.Parts[0]);
            }
        }
    }
}
