// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.IO;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;

namespace OpenDot.Sol.Fir;

/// <summary>Compiler-owned declarations for the deliberately restricted native bootstrap library.</summary>
public sealed class RuntimeCatalog
{
    private readonly ImmutableHashSet<AstCallable> _functions;
    private RuntimeCatalog(ImmutableArray<AstSnapshot> files)
    {
        Files = files;
        _functions = files.SelectMany(f => f.Nodes).OfType<AstCallable>().ToImmutableHashSet();
    }
    public ImmutableArray<AstSnapshot> Files { get; }
    public bool Owns(AstCallable function) => _functions.Contains(function);
    internal FirIntrinsicKind? Intrinsic(AstFunction function)
    {
        if (!Owns(function)) { return null; }
        var file = Files.Single(f => f.Nodes.Contains(function));
        AstNode? parent = file.GetParent(function);
        while (parent is not null and not AstTypeDeclaration) { parent = file.GetParent(parent); }
        if (parent is not AstTypeDeclaration owner) { return null; }
        return (owner.Name, function.Name) switch
        {
            ("String", "substring") => FirIntrinsicKind.StringSubstring,
            ("String", "indexOf") => FirIntrinsicKind.StringIndexOf,
            ("String", "compareOrdinal") => FirIntrinsicKind.StringCompare,
            ("String", "hashCode") => FirIntrinsicKind.StringHash,
            ("Gc", "collect") => FirIntrinsicKind.GcCollect,
            _ => null,
        };
    }

    public static RuntimeCatalog Create()
    {
        string Read(string name)
        {
            using var stream = typeof(RuntimeCatalog).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidDataException("Native bootstrap declarations are missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        string[] texts = [Read("SolRT.Core.sol"), Read("SolRT.Stdio.sol"), Read("SolRT.Math.sol")];
        string[] paths = ["<solrt>/std/core/Core.sol", "<solrt>/std/stdio/Stdio.sol", "<solrt>/std/math/Math.sol"];
        return new(texts.Select((text, index) => AstSnapshot.Create(PsiSnapshot.Create(SyntaxTree.Parse(
            SourceText.FromString(text, paths[index]))))).ToImmutableArray());
    }
}
