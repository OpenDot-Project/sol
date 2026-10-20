// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.IO;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class SourceSpecificationTests
{
    [Fact]
    public void PublishedSourceContractsAndExamplesParseWithoutClaimingSemanticSupport()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "spec");
        var files = Directory.GetFiles(root, "*.sol", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        Assert.Contains(files, path => Path.GetDirectoryName(path) == root);
        Assert.Contains(files, path => Path.GetRelativePath(root, path).StartsWith("examples", StringComparison.Ordinal));
        foreach (string path in files)
        {
            var source = SourceText.FromBytes(File.ReadAllBytes(path), Path.GetRelativePath(root, path));
            var tree = SyntaxTree.Parse(source, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(tree.HasErrors, source.Path + ": " + string.Join('\n', tree.Diagnostics.Select(d => d.Message)));
            Assert.False(string.IsNullOrWhiteSpace(tree.ModuleName));
            var ast = AstSnapshot.Create(PsiSnapshot.Create(tree, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
            AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        }
    }
}
