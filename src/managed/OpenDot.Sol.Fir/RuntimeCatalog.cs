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

    public static RuntimeCatalog Create()
    {
        string Read(string name)
        {
            using var stream = typeof(RuntimeCatalog).Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidDataException("Native bootstrap declarations are missing.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        string[] texts = [Read("SolRT.Core.sol"), Read("SolRT.Stdio.sol")];
        return new(texts.Select((text, index) => AstSnapshot.Create(PsiSnapshot.Create(SyntaxTree.Parse(
            SourceText.FromString(text, index == 0 ? "<solrt>/std/core/Core.sol" : "<solrt>/std/stdio/Stdio.sol"))))).ToImmutableArray());
    }
}
