// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Driver;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class BindingIntegrationTests
{
    [Fact]
    public void SameModuleFilesHaveDistinctInspectionIdentityWithoutExposingPaths()
    {
        var session = AstTestSources.Session("module app\nfunc f(x:int) = x", "module app\nfunc g(x:int) = x");
        var analysis = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(BindingJson.Serialize(analysis, TestContext.Current.CancellationToken));
        Assert.Equal(2, document.RootElement.GetProperty("files").GetArrayLength());
        var references = document.RootElement.GetProperty("references").EnumerateArray().ToArray();
        Assert.Equal(2, references.Length);
        Assert.NotEqual(references[0].GetProperty("file").GetInt32(), references[1].GetProperty("file").GetInt32());
    }

    [Fact]
    public void DriverComposesAllSourceAnalysesWithoutLoadingLlvm()
    {
        var compilation = FrontendCompilation.Parse(new[]
        {
            SourceText.FromString("module std.core\nclass Object {}\nclass String {}"),
            SourceText.FromString("module app\nfunc f(x:String):String { val copy = x; copy }"),
        }, cancellationToken: TestContext.Current.CancellationToken);
        var analysis = compilation.AnalyzeSources(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(analysis.HasErrors);
        Assert.Contains(analysis.Lexical.Bindings, binding => binding.Reference.Spelling == "copy" && binding.Lookup.IsLocal);
        Assert.Contains(analysis.Types.Types, type => type.Name == "std.core.String");
    }

    [Fact]
    public void InspectionIsDeterministicAndDoesNotIncludeInputPaths()
    {
        var compilation = FrontendCompilation.Parse(new[]
        {
            SourceText.FromString("module app\nclass Box<T> {}\nfunc f(x:Box<int>):int { val callback = { x }; 1 }", "C:/private/input.sol"),
        }, cancellationToken: TestContext.Current.CancellationToken);
        var analysis = compilation.AnalyzeSources(new() { RequireCore = false }, TestContext.Current.CancellationToken);
        string json = BindingJson.Serialize(analysis, TestContext.Current.CancellationToken);
        Assert.Equal(json, BindingJson.Serialize(analysis, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("C:/private", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schema").GetInt32());
        Assert.True(document.RootElement.GetProperty("types").GetArrayLength() > 1);
        Assert.True(document.RootElement.GetProperty("scopes").GetArrayLength() > 1);
    }

    [Fact]
    public async Task FrozenScopesAndBindingsSupportConcurrentInspection()
    {
        var session = AstTestSources.Session("module app\nfunc f(x:int) = x");
        var analysis = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        string expected = BindingJson.Serialize(analysis, TestContext.Current.CancellationToken);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            () => BindingJson.Serialize(analysis, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));
        Assert.All(results, result => Assert.Equal(expected, result));
    }

    [Fact]
    public void ComposedDiagnosticsRespectTheExplicitSessionBudget()
    {
        var compilation = FrontendCompilation.Parse(new[]
        {
            SourceText.FromString("module app\nimport missing\nfunc f() { val a:Unknown; val a:Other }")
        }, cancellationToken: TestContext.Current.CancellationToken);
        var analysis = compilation.AnalyzeSources(new() { RequireCore = false, DiagnosticLimit = 2 }, TestContext.Current.CancellationToken);
        Assert.True(analysis.HasErrors);
        Assert.True(analysis.Diagnostics.Length <= 2);
        Assert.All(analysis.Diagnostics, diagnostic => diagnostic.Source.Validate(diagnostic.Span));
    }
}
