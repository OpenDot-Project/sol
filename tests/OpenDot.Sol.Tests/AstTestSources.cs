// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

internal static class AstTestSources
{
    internal static AstSnapshot Parse(string declarations, string module = "test", string path = "input.sol")
    {
        var syntax = SyntaxTree.Parse(SourceText.FromString("module " + module + "\n" + declarations, path));
        Assert.False(syntax.HasErrors, string.Join("\n", syntax.Diagnostics.Select(d => d.Message)));
        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax));
        AstVerifier.Verify(ast);
        return ast;
    }

    internal static AnalysisSession Session(params string[] files)
    {
        var trees = files.Select((text, index) => SyntaxTree.Parse(SourceText.FromString(text, "file" + index + ".sol")));
        return AnalysisSession.Create(trees, new() { RequireCore = false });
    }
}
