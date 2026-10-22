// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnalysisSessionTests
{
    [Fact]
    public void MissingImportTargetProducesDiagnosticsWithoutAnInvalidGraphVertex()
    {
        var session = AstTestSources.Session("module app\nimport\nclass C {}");
        Assert.True(session.HasErrors);
        Assert.DoesNotContain("", session.Graph.Edges.Keys);
        Assert.Contains(session.Diagnostics, diagnostic => diagnostic.Code == "SOL2001");
    }

    [Theory]
    [InlineData("val callback = { x -> $0 }", "SOL2090")]
    [InlineData("val callback = { $999999999999999999999999 }", "SOL2091")]
    public void LambdaShapeErrorsAreDiagnosedWithoutInventingAnExpectedType(string declaration, string code)
    {
        var session = AstTestSources.Session("module app\n" + declaration);
        Assert.Contains(session.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void NestedLambdaPositionsDoNotInvalidateAnOuterNamedHeader()
    {
        var session = AstTestSources.Session("module app\nval callback = { x -> val nested = { $0 }; x }");
        Assert.DoesNotContain(session.Diagnostics, diagnostic => diagnostic.Code == "SOL2090");
    }

    [Fact]
    public void DefaultSessionRequiresAnExplicitCoreModule()
    {
        var ast = AstTestSources.Parse("class C {}");
        var session = AnalysisSession.Create(new[] { ast }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(session.Diagnostics, diagnostic => diagnostic.Code == "SOL2001");
        Assert.Contains(session.Diagnostics, diagnostic => diagnostic.Message.Contains("std.core", StringComparison.Ordinal));
    }

    [Fact]
    public void BootstrapModeIsExplicitAndDoesNotSynthesizeCoreTypes()
    {
        var ast = AstTestSources.Parse("class C {}");
        var session = AnalysisSession.Create(new[] { ast }, new() { RequireCore = false }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(session.HasErrors);
        Assert.False(session.Modules.ContainsKey("std.core"));
        Assert.Equal(LookupKind.Missing, session.Lookup(ast.Root, "Object").Kind);
    }

    [Fact]
    public void AllPublicLookupsRejectForeignSnapshotsAndSymbols()
    {
        var first = AstTestSources.Session("module app\nclass C {}");
        var second = AstTestSources.Session("module app\nclass C {}");
        var foreignFile = second.Files[0].Root;
        var foreignSymbol = Assert.Single(second.Modules["app"].Declarations);
        Assert.Throws<ArgumentException>(() => first.Lookup(foreignFile, "C"));
        Assert.Throws<ArgumentException>(() => first.LookupQualified(foreignFile, "app", "C"));
        Assert.Throws<ArgumentException>(() => first.GetDeclaredSymbol(foreignSymbol.Parts[0]));
        Assert.Throws<ArgumentException>(() => first.Lookup(first.Files[0].Root, "x", foreignSymbol));
    }

    [Fact]
    public void SessionConstructionRejectsDuplicateSnapshotsAndInvalidOptions()
    {
        var ast = AstTestSources.Parse("class C {}");
        Assert.Throws<ArgumentException>(() => AnalysisSession.Create(new[] { ast, ast }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => AnalysisSession.Create(new[] { ast }, new() { DistributionName = " " }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnalysisSession.Create(new[] { ast }, new() { DiagnosticLimit = 0 }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => AnalysisSession.Create((AstSnapshot[])null!, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ParseDiagnosticsRemainVisibleInAnAnalysisSession()
    {
        var syntax = SyntaxTree.Parse(SourceText.FromString("module app\nclass C { func invalid( }"), cancellationToken: TestContext.Current.CancellationToken);
        var session = AnalysisSession.Create(new[] { syntax }, new() { RequireCore = false }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(session.HasErrors);
        Assert.Contains(session.Diagnostics, diagnostic => diagnostic.Code.StartsWith("SOL11", StringComparison.Ordinal));
    }

    [Fact]
    public void DiagnosticBudgetAppliesAcrossFilesAndDeclarationPhases()
    {
        var sources = Enumerable.Range(0, 20).Select(index => SyntaxTree.Parse(SourceText.FromString(
            "module app\nimport absent" + index + "\nclass Duplicate {}", "file" + index + ".sol"), cancellationToken: TestContext.Current.CancellationToken));
        var session = AnalysisSession.Create(sources, new() { RequireCore = false, DiagnosticLimit = 3 }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, session.Diagnostics.Length);
    }

    [Fact]
    public void EmptyExplicitSourceSetIsInspectable()
    {
        var session = AnalysisSession.Create(Array.Empty<AstSnapshot>(), new() { RequireCore = false }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(session.Modules);
        Assert.Empty(session.Files);
        Assert.Empty(session.Graph.CompilationGroups);
        Assert.False(session.HasErrors);
    }

    [Fact]
    public void InspectionJsonIsDeterministicAndContainsNoSourcePaths()
    {
        var session = AstTestSources.Session("module app\nclass C {}\nfunc f(a:int) {}", "module core\nclass Object {}");
        string json = AnalysisJson.Serialize(session, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(json, AnalysisJson.Serialize(session, cancellationToken: TestContext.Current.CancellationToken));
        Assert.DoesNotContain("file0.sol", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("modules").GetArrayLength());
        Assert.Equal(3, document.RootElement.GetProperty("symbols").GetArrayLength());
    }

    [Fact]
    public void CancellationDoesNotPublishAPartiallyBuiltSession()
    {
        var ast = AstTestSources.Parse("class C {}");
        var cancelled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => AnalysisSession.Create(new[] { ast }, cancellationToken: cancelled));
        var session = AnalysisSession.Create(new[] { ast }, new() { RequireCore = false }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Throws<OperationCanceledException>(() => AnalysisJson.Serialize(session, cancelled));
    }

    [Fact]
    public async Task IndependentLookupsAndInspectionAreSafeForConcurrentReaders()
    {
        var session = AstTestSources.Session("module app\nimport lib", "module lib\nclass Item {}");
        string expected = AnalysisJson.Serialize(session, cancellationToken: TestContext.Current.CancellationToken);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            Assert.Equal(LookupKind.Found, session.Lookup(session.Modules["app"].Files[0].Root, "Item").Kind);
            return AnalysisJson.Serialize(session, cancellationToken: TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken));
        Assert.All(await Task.WhenAll(tasks), result => Assert.Equal(expected, result));
    }

    [Fact]
    public void CaseSensitiveModuleNamesDoNotCollapseOnWindows()
    {
        var session = AstTestSources.Session("module App\nclass Upper {}", "module app\nclass Lower {}");
        Assert.Equal(2, session.Modules.Count);
        Assert.True(session.Modules.ContainsKey("App"));
        Assert.True(session.Modules.ContainsKey("app"));
    }

    [Fact]
    public void ExternFunctionsParticipateInModuleDeclarationLookup()
    {
        var session = AstTestSources.Session("module app\nextern \"c\" { func native(value: int): int; }");
        Assert.False(session.HasErrors);
        var lookup = session.Lookup(session.Files[0].Root, "native");
        Assert.Equal(SymbolKind.Function, Assert.Single(lookup.Candidates).Kind);
    }
}
