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
using OpenDot.Sol.Psi;

namespace OpenDot.Sol.Analysis;

/// <summary>An immutable declaration-analysis session. It does not perform expression type checking.</summary>
public sealed partial class AnalysisSession
{
    private readonly Dictionary<AstDeclaration, DeclarationSymbol> _symbols;
    private readonly Dictionary<AstFile, AstSnapshot> _files;
    private readonly ImmutableDictionary<string, ImmutableDictionary<string, ImmutableArray<DeclarationSymbol>>> _exports;
    private readonly ImmutableDictionary<string, ImmutableDictionary<string, ImmutableArray<string>>> _qualifiers;

    internal AnalysisSession(AnalysisBuilder builder)
    {
        Files = builder.Files;
        Options = builder.Options;
        Modules = builder.Modules.ToImmutableDictionary(StringComparer.Ordinal);
        _symbols = builder.Symbols;
        _files = new(ReferenceEqualityComparer.Instance);
        foreach (var file in Files) { _files.Add(file.Root, file); }
        Graph = builder.Graph;
        ReExportGraph = builder.ReExportGraph;
        _exports = builder.Exports.ToImmutableDictionary(StringComparer.Ordinal);
        _qualifiers = builder.Qualifiers.ToImmutableDictionary(StringComparer.Ordinal);
        Diagnostics = builder.Diagnostics.OrderBy(d => d.Source.Path, StringComparer.Ordinal)
            .ThenBy(d => d.Span.Start).ThenBy(d => d.Code, StringComparer.Ordinal)
            .ThenBy(d => d.Message, StringComparer.Ordinal).ToImmutableArray();
    }

    public AnalysisOptions Options { get; }
    public ImmutableArray<AstSnapshot> Files { get; }
    public ImmutableDictionary<string, ModuleSymbol> Modules { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    public ModuleGraph Graph { get; }
    public ModuleGraph ReExportGraph { get; }

    public static AnalysisSession Create(IEnumerable<SyntaxTree> sources, AnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        return Create(sources.Select(source => AstSnapshot.Create(PsiSnapshot.Create(source, cancellationToken),
            cancellationToken)), options, cancellationToken);
    }

    public static AnalysisSession Create(IEnumerable<AstSnapshot> files, AnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DistributionName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.DiagnosticLimit);
        return new AnalysisBuilder(files.ToImmutableArray(), options, cancellationToken).Build();
    }

    public DeclarationSymbol? GetDeclaredSymbol(AstDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var file = Files.FirstOrDefault(f => ReferenceEquals(f.Psi, declaration.Origin.Element.Snapshot));
        if (file is null)
        {
            throw new ArgumentException("The declaration is from another analysis session.", nameof(declaration));
        }
        file.ValidateOwnership(declaration);
        return _symbols.GetValueOrDefault(declaration);
    }

    public ImmutableArray<DeclarationSymbol> GetExports(string moduleName, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Modules.ContainsKey(moduleName))
        {
            throw new ArgumentException("Unknown source module.", nameof(moduleName));
        }
        return _exports[moduleName].GetValueOrDefault(name, []);
    }

    public NameLookup Lookup(AstFile file, string name, DeclarationSymbol? container = null)
    {
        ValidateFile(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (container is not null)
        {
            ValidateSymbol(container);
            var members = NameLookup.From(container.FindMembers(name), LookupScope.Member);
            if (members.Kind != LookupKind.Missing) { return members; }
        }
        if (file.ModuleName is not null && Modules.TryGetValue(file.ModuleName, out var module))
        {
            var local = NameLookup.From(module.Declarations.Where(d => d.Name == name), LookupScope.Module);
            if (local.Kind != LookupKind.Missing) { return local; }
        }
        var imported = NameLookup.From(Imported(file, name), LookupScope.Import);
        if (imported.Kind != LookupKind.Missing) { return imported; }
        return NameLookup.From(_exports.GetValueOrDefault("std.core")?.GetValueOrDefault(name, []) ?? [], LookupScope.Core);
    }

    public NameLookup LookupQualified(AstFile file, string qualifier, string name)
    {
        ValidateFile(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var candidates = new List<DeclarationSymbol>();
        foreach (var import in file.Imports)
        {
            var targets = new List<string>();
            if ((import.Alias ?? import.ModuleName) == qualifier) { targets.Add(import.ModuleName); }
            if (import.Alias is null && !import.IsSelective && _qualifiers.TryGetValue(import.ModuleName, out var aliases))
            {
                targets.AddRange(aliases.GetValueOrDefault(qualifier, []));
            }
            foreach (var target in targets)
            {
                if (_exports.TryGetValue(target, out var exports))
                {
                    candidates.AddRange(exports.GetValueOrDefault(name, []));
                }
            }
        }
        if (qualifier == "std.core" && _exports.TryGetValue("std.core", out var core))
        {
            candidates.AddRange(core.GetValueOrDefault(name, []));
        }
        if (qualifier == file.ModuleName && Modules.TryGetValue(qualifier, out var own))
        {
            candidates.AddRange(own.Declarations.Where(d => d.Name == name));
        }
        return NameLookup.From(candidates, LookupScope.Import);
    }

    private IEnumerable<DeclarationSymbol> Imported(AstFile file, string name)
    {
        foreach (var import in file.Imports)
        {
            if (import.Alias is not null || !_exports.TryGetValue(import.ModuleName, out var exports))
            {
                continue;
            }
            if (!import.IsSelective)
            {
                foreach (var symbol in exports.GetValueOrDefault(name, [])) { yield return symbol; }
            }
            else
            {
                foreach (var item in import.Items.Where(item => item.LocalName == name))
                {
                    foreach (var symbol in exports.GetValueOrDefault(item.OriginalName, [])) { yield return symbol; }
                }
            }
        }
    }

    private void ValidateFile(AstFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!_files.ContainsKey(file))
        {
            throw new ArgumentException("The file belongs to another analysis session.", nameof(file));
        }
    }

    private void ValidateSymbol(DeclarationSymbol symbol)
    {
        if (!_symbols.Values.Any(owned => ReferenceEquals(owned, symbol)))
        {
            throw new ArgumentException("The symbol belongs to another analysis session.", nameof(symbol));
        }
    }

}
