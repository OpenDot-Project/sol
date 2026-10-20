// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Text.Json;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AstMutationTests
{
    private const string ValidSource = """
        module mutation.sample
        import library : Local = Item
        partial class Box<T>(val value: T) {
            var count: int { get; private set; } = 0
            constructor(value: T, count: int): this(value) { this.count = count }
            func read(fallback: T): T {
                try {
                    if count > 0 { return value }
                    fallback
                } catch (error: IOException) {
                    fallback
                } finally {
                    close()
                }
            }
        }
        partial class Box<T> {
            func map(callback: (T)->T): T = callback(value)
        }
        val transformed = map(1, named = ref location, out var written) { $0 + 1 }
        """;

    [Fact]
    public void EverySingleTokenDeletionProducesAConsistentRecoverySnapshot()
    {
        var source = SourceText.FromString(ValidSource);
        var original = SyntaxTree.Parse(source, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(original.HasErrors);
        foreach (var token in original.Tokens.Where(token => token.Kind != SyntaxKind.EndOfFileToken))
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            byte[] mutated = new byte[source.Length - token.Span.Length];
            source.Bytes[..token.Span.Start].CopyTo(mutated);
            source.Bytes[token.Span.End..].CopyTo(mutated.AsSpan(token.Span.Start));
            CheckMutation(mutated);
        }
    }

    [Fact]
    public void SeededByteInsertionsRetainDiagnosticAndAstSourceOwnership()
    {
        byte[] original = SourceText.FromString(ValidSource).Bytes.ToArray();
        var random = new Random(20261021);
        for (int sample = 0; sample < 80; sample++)
        {
            int position = random.Next(original.Length + 1);
            int count = random.Next(1, 5);
            byte[] mutated = new byte[original.Length + count];
            original.AsSpan(0, position).CopyTo(mutated);
            original.AsSpan(position).CopyTo(mutated.AsSpan(position + count));
            for (int index = 0; index < count; index++)
            {
                mutated[position + index] = (byte)random.Next(256);
            }
            CheckMutation(mutated);
        }
    }

    [Fact]
    public void EquivalentSnapshotsHaveEqualInspectionButIndependentIdentity()
    {
        var first = CheckMutation(SourceText.FromString(ValidSource).Bytes.ToArray());
        var second = CheckMutation(SourceText.FromString(ValidSource).Bytes.ToArray());
        Assert.Equal(AstJson.Serialize(first, TestContext.Current.CancellationToken),
            AstJson.Serialize(second, TestContext.Current.CancellationToken));
        Assert.NotSame(first.Root, second.Root);
        Assert.Throws<ArgumentException>(() => first.GetParent(second.Root));
    }

    private static AstSnapshot CheckMutation(byte[] bytes)
    {
        var source = SourceText.FromBytes(bytes);
        var syntax = SyntaxTree.Parse(source, errorLimit: 40, maxDepth: 48,
            cancellationToken: TestContext.Current.CancellationToken);
        var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        AstVerifier.Verify(ast, TestContext.Current.CancellationToken);
        Assert.Equal(bytes, source.Bytes.ToArray());
        Assert.Equal(syntax.HasErrors, ast.HasSyntaxErrors);
        Assert.All(ast.Nodes, node => Assert.Same(source, node.Origin.Source));
        using var document = JsonDocument.Parse(AstJson.Serialize(ast, TestContext.Current.CancellationToken));
        Assert.Equal(ast.Nodes.Length, document.RootElement.GetProperty("nodes").GetArrayLength());
        var session = AnalysisSession.Create(new[] { ast }, new() { RequireCore = false, DiagnosticLimit = 50 },
            TestContext.Current.CancellationToken);
        Assert.True(session.Diagnostics.Length <= 50);
        Assert.All(session.Diagnostics, diagnostic =>
        {
            Assert.Same(source, diagnostic.Source);
            source.Validate(diagnostic.Span);
        });
        var binding = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.All(binding.Diagnostics, diagnostic =>
        {
            Assert.Same(source, diagnostic.Source);
            source.Validate(diagnostic.Span);
        });
        return ast;
    }
}
