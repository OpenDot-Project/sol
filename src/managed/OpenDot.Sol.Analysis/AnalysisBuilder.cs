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

internal sealed partial class AnalysisBuilder
{
    private readonly CancellationToken _cancellation;
    private int _nextSymbol;

    internal AnalysisBuilder(ImmutableArray<AstSnapshot> files, AnalysisOptions options, CancellationToken cancellation)
    {
        Files = files;
        Options = options;
        _cancellation = cancellation;
    }

    internal ImmutableArray<AstSnapshot> Files { get; }
    internal AnalysisOptions Options { get; }
    internal Dictionary<string, ModuleSymbol> Modules { get; } = new(StringComparer.Ordinal);
    internal Dictionary<AstDeclaration, DeclarationSymbol> Symbols { get; } = new(ReferenceEqualityComparer.Instance);
    internal List<Diagnostic> Diagnostics { get; } = [];
    internal Dictionary<string, ImmutableDictionary<string, ImmutableArray<DeclarationSymbol>>> Exports { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, ImmutableDictionary<string, ImmutableArray<string>>> Qualifiers { get; } = new(StringComparer.Ordinal);
    internal ModuleGraph Graph { get; private set; } = null!;
    internal ModuleGraph ReExportGraph { get; private set; } = null!;

    internal AnalysisSession Build()
    {
        var seen = new HashSet<AstSnapshot>(ReferenceEqualityComparer.Instance);
        foreach (var file in Files)
        {
            _cancellation.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(file);
            if (!seen.Add(file))
            {
                throw new ArgumentException("The same AST snapshot was supplied more than once.", nameof(Files));
            }
            foreach (var diagnostic in file.Psi.Syntax.Diagnostics)
            {
                if (Diagnostics.Count < Options.DiagnosticLimit) { Diagnostics.Add(diagnostic); }
            }
            foreach (var lambda in file.Nodes.OfType<AstLambda>())
            {
                _cancellation.ThrowIfCancellationRequested();
                if (lambda.Form == AstLambdaForm.Named && (!lambda.Positions.IsEmpty || lambda.HasUnrepresentablePosition))
                {
                    Report("SOL2090", "A named lambda header cannot mix positional references in its own scope.", lambda);
                }
                if (lambda.HasUnrepresentablePosition)
                {
                    Report("SOL2091", "The positional parameter index cannot fit a supported function arity.", lambda);
                }
            }
        }
        foreach (var group in Files.Where(f => !string.IsNullOrEmpty(f.Root.ModuleName))
            .GroupBy(f => f.Root.ModuleName!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            _cancellation.ThrowIfCancellationRequested();
            var files = group.ToImmutableArray();
            var declarations = BuildSymbols(group.Key, group.Key, files.SelectMany(f => FlattenExtern(f.Root.Declarations)));
            Modules.Add(group.Key, new(group.Key, files, declarations));
        }
        BuildGraphs();
        ValidateImports();
        BuildExports();
        BuildQualifiers();
        ValidateSelectiveImports();
        return new(this);
    }

    private static IEnumerable<AstDeclaration> FlattenExtern(IEnumerable<AstDeclaration> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is AstExtern external)
            {
                foreach (var function in external.Functions) { yield return function; }
            }
            else
            {
                yield return declaration;
            }
        }
    }

    private ImmutableArray<DeclarationSymbol> BuildSymbols(string module, string owner,
        IEnumerable<AstDeclaration> declarations)
    {
        var result = ImmutableArray.CreateBuilder<DeclarationSymbol>();
        var source = declarations.Where(d => d is not AstInitializer).ToArray();
        foreach (var declaration in source)
        {
            _cancellation.ThrowIfCancellationRequested();
            ValidateDeclaration(declaration);
        }
        foreach (var group in source.GroupBy(GroupKey).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            _cancellation.ThrowIfCancellationRequested();
            var parts = group.ToImmutableArray();
            var first = parts[0];
            string name = Name(first);
            int arity = first is AstTypeDeclaration type ? type.Arity
                : first is AstCallable callable ? callable.TypeParameters.Count() : 0;
            var kind = Kind(first);
            string qualified = owner + "." + name;
            ImmutableArray<DeclarationSymbol> members = [];
            if (first is AstTypeDeclaration)
            {
                ValidatePartial(parts);
                members = BuildSymbols(module, qualified + "`" + arity,
                    parts.OfType<AstTypeDeclaration>().SelectMany(t => t.Members));
            }
            else if (parts.Length > 1)
            {
                foreach (var duplicate in parts.Skip(1))
                {
                    Report("SOL2003", "Duplicate declaration signature: " + qualified, duplicate);
                }
            }
            var symbol = new DeclarationSymbol(_nextSymbol++, kind, module, qualified, name, arity,
                parts, members, Signature(first));
            foreach (var part in parts) { Symbols.Add(part, symbol); }
            result.Add(symbol);
        }
        foreach (var group in result.GroupBy(s => s.Name, StringComparer.Ordinal))
        {
            bool compatible = group.All(s => s.Kind == SymbolKind.Function)
                || group.All(s => s.Kind == SymbolKind.Constructor)
                || group.All(s => s.Kind == SymbolKind.Indexer)
                || group.All(s => s.Kind == SymbolKind.Type);
            if (group.Count() > 1 && !compatible)
            {
                foreach (var conflict in group.Skip(1))
                {
                    Report("SOL2003", "Conflicting declarations for name: " + conflict.Name, conflict.Parts[0]);
                }
            }
        }
        return result.ToImmutable();
    }

    private static string Name(AstDeclaration declaration)
    {
        return declaration switch
        {
            AstConstructor => "constructor",
            AstIndexer => "this",
            AstTypeDeclaration { IsCompanion: true, Name: null } => "<companion>",
            _ => declaration.Name ?? "<missing>",
        };
    }

    private static SymbolKind Kind(AstDeclaration declaration)
    {
        return declaration switch
        {
            AstTypeDeclaration => SymbolKind.Type,
            AstConstructor => SymbolKind.Constructor,
            AstIndexer => SymbolKind.Indexer,
            AstEnumVariant => SymbolKind.EnumVariant,
            AstCallable => SymbolKind.Function,
            _ => SymbolKind.Property,
        };
    }

    private static string GroupKey(AstDeclaration declaration)
    {
        if (declaration is AstTypeDeclaration type)
        {
            return "type:" + Name(type) + "`" + type.Arity;
        }
        return Kind(declaration) + ":" + Name(declaration) + ":" + Signature(declaration);
    }

    private static string Signature(AstDeclaration declaration)
    {
        if (declaration is not AstCallable callable) { return ""; }
        string variant = declaration is AstFunction { IsCheckedOperator: true } ? "checked " : "";
        var typeNames = callable.TypeParameters.Select(p => p.Name ?? "").ToArray();
        return variant + "`" + typeNames.Length + "(" + string.Join(',', callable.Parameters.Select(parameter =>
        {
            string type = parameter.Type is null ? "<missing>" : SignatureType(parameter.Type, typeNames);
            string mode = parameter.PassingMode switch
            {
                AstPassingMode.Ref or AstPassingMode.Out => "ref ",
                AstPassingMode.Vararg => "vararg ",
                _ => "",
            };
            return mode + type;
        })) + ")";
    }

    private static string SignatureType(AstType type, string[] typeNames)
    {
        string result = type.Shape.CanonicalName;
        // Replace identifier tokens only, so T does not rewrite an unrelated Tree or module path.
        var output = new System.Text.StringBuilder();
        for (int index = 0; index < result.Length;)
        {
            if (!char.IsLetter(result[index]) && result[index] != '_')
            {
                output.Append(result[index++]);
                continue;
            }
            int start = index++;
            while (index < result.Length && (char.IsLetterOrDigit(result[index]) || result[index] == '_')) { index++; }
            string word = result[start..index];
            int ordinal = Array.IndexOf(typeNames, word);
            bool qualified = start > 0 && result[start - 1] == '.' || index < result.Length && result[index] == '.';
            output.Append(ordinal < 0 || qualified ? word : "#" + ordinal);
        }
        return output.ToString();
    }

    private void Report(string code, string message, AstNode node)
    {
        if (Diagnostics.Count < Options.DiagnosticLimit)
        {
            Diagnostics.Add(new(code, message, node.Origin.Source, node.Origin.Span));
        }
    }
}
