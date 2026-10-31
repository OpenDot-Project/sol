// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;

namespace OpenDot.Sol.Fuzz;

internal static class SemanticOracle
{
    internal static bool CheckSourceBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 8192) { return false; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        var syntax = SyntaxTree.Parse(SourceText.FromBytes(bytes.ToArray(), "coverage.sol"), 64, 64, token);
        var snapshot = AstSnapshot.Create(PsiSnapshot.Create(syntax, token), token);
        AstVerifier.Verify(snapshot, token);
        string normalized = AstJson.Serialize(snapshot, token);
        var repeated = AstSnapshot.Create(PsiSnapshot.Create(syntax, token), token);
        Corpus.Require(normalized == AstJson.Serialize(repeated, token), "AST normalization is not deterministic.");
        foreach (var node in snapshot.Nodes)
        {
            snapshot.ValidateOwnership(node);
            syntax.Source.Validate(node.Origin.Span);
        }
        var options = new AnalysisOptions { RequireCore = false, DiagnosticLimit = 64 };
        var session = AnalysisSession.Create([snapshot], options, token);
        var repeatedSession = AnalysisSession.Create([snapshot], options, token);
        Corpus.Require(AnalysisJson.Serialize(session, token) == AnalysisJson.Serialize(repeatedSession, token),
            "Declaration analysis is not deterministic.");
        foreach (var diagnostic in session.Diagnostics)
        {
            Corpus.Require(ReferenceEquals(snapshot.Psi.Source, diagnostic.Source), "Foreign semantic diagnostic snapshot.");
            diagnostic.Source.Validate(diagnostic.Span);
        }
        Corpus.Require(session.Diagnostics.Length <= 64, "Semantic diagnostic budget exceeded.");
        return syntax.HasErrors || session.HasErrors;
    }

    internal static bool Check(CorpusCase item)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = deadline.Token;
        using var document = JsonDocument.Parse(item.Bytes, new() { MaxDepth = 8 });
        Corpus.Fields(document.RootElement, "files");
        var files = document.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Corpus.Require(files.Length is > 0 and <= 8, "Invalid semantic source set.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var snapshots = new List<AstSnapshot>();
        foreach (var file in files)
        {
            Corpus.Fields(file, "path", "source");
            string name = Corpus.Text(file, "path"), text = Corpus.Text(file, "source");
            Corpus.Require(name.Length is > 0 and < 64 && !name.Contains('/') && !name.Contains('\\') &&
                !name.Contains(':') && names.Add(name), "Invalid virtual source identity.");
            var syntax = SyntaxTree.Parse(SourceText.FromString(text, name), 64, 64, token);
            var ast = AstSnapshot.Create(PsiSnapshot.Create(syntax, token), token);
            AstVerifier.Verify(ast, token);
            string first = AstJson.Serialize(ast, token);
            var repeated = AstSnapshot.Create(PsiSnapshot.Create(syntax, token), token);
            Corpus.Require(first == AstJson.Serialize(repeated, token), "AST normalization is not deterministic.");
            foreach (var node in ast.Nodes)
            {
                ast.ValidateOwnership(node);
                syntax.Source.Validate(node.Origin.Span);
            }
            snapshots.Add(ast);
        }
        var options = new AnalysisOptions { RequireCore = false, DiagnosticLimit = 64 };
        var session = AnalysisSession.Create(snapshots, options, token);
        var repeatedSession = AnalysisSession.Create(snapshots, options, token);
        Corpus.Require(AnalysisJson.Serialize(session, token) == AnalysisJson.Serialize(repeatedSession, token),
            "Declaration analysis is not deterministic.");
        var reversed = AnalysisSession.Create(snapshots.AsEnumerable().Reverse(), options, token);
        Corpus.Require(GraphProjection(session) == GraphProjection(reversed), "Module graph depends on source enumeration order.");
        foreach (var diagnostic in session.Diagnostics)
        {
            Corpus.Require(snapshots.Any(s => ReferenceEquals(s.Psi.Source, diagnostic.Source)), "Foreign semantic diagnostic snapshot.");
            diagnostic.Source.Validate(diagnostic.Span);
        }
        Corpus.Require(session.Diagnostics.Length <= 64, "Semantic diagnostic budget exceeded.");
        return session.HasErrors;
    }

    private static string GraphProjection(AnalysisSession session)
    {
        var result = new StringBuilder();
        foreach (var module in session.Modules.Values.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            result.Append(module.Name).Append(':').Append(module.Files.Length).Append('|');
            foreach (string edge in session.Graph.Edges[module.Name].Order(StringComparer.Ordinal)) { result.Append(edge).Append(';'); }
            result.AppendLine();
        }
        return result.ToString();
    }
}
