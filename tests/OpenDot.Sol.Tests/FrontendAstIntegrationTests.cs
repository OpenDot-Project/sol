// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cli;
using OpenDot.Sol.Driver;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FrontendAstIntegrationTests
{
    [Theory]
    [InlineData("--emit=ast", EmitKind.Ast)]
    [InlineData("--emit=cst", EmitKind.Cst)]
    [InlineData("--emit=link", EmitKind.Link)]
    public void EmitSelectionRemainsAnOptionWithoutCompilerSubcommands(string option, EmitKind expected)
    {
        var parsed = CompilerOptions.Parse(new[] { "input.sol", option });
        Assert.Equal(CompilerCommand.Compile, parsed.Command);
        Assert.Equal(expected, parsed.Emit);
    }

    [Fact]
    public void DriverCreatesAstForTheExactExplicitSourceSet()
    {
        var compilation = FrontendCompilation.Parse(new[]
        {
            SourceText.FromString("module app\nclass C {}", "elsewhere/a.sol"),
            SourceText.FromString("module app\nfunc use() {}", "unrelated/b.sol"),
        }, cancellationToken: TestContext.Current.CancellationToken);
        var ast = compilation.CreateAst(TestContext.Current.CancellationToken);
        Assert.Equal(2, ast.Length);
        Assert.All(ast, snapshot => Assert.Equal("app", snapshot.Root.ModuleName));
        Assert.Same(compilation.Trees[0].Source, ast[0].Psi.Source);
        Assert.Same(compilation.Trees[1].Source, ast[1].Psi.Source);
    }

    [Fact]
    public void DriverAnalysisRetainsCoreImportsAndMergedPartialIdentity()
    {
        var compilation = FrontendCompilation.Parse(new[]
        {
            SourceText.FromString("module std.core\nclass String {}"),
            SourceText.FromString("module app\npartial class C { func first(value: String) {} }"),
            SourceText.FromString("module app\npartial class C { func second() {} }"),
        }, cancellationToken: TestContext.Current.CancellationToken);
        var session = compilation.AnalyzeDeclarations(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(session.HasErrors);
        Assert.Equal(2, Assert.Single(session.Modules["app"].Declarations).Parts.Length);
        var lookup = session.Lookup(session.Modules["app"].Files[0].Root, "String");
        Assert.Equal(LookupScope.Core, lookup.Scope);
    }

    [Fact]
    public void DriverDoesNotConfuseSyntaxSuccessWithDeclarationAnalysisSuccess()
    {
        var compilation = FrontendCompilation.Parse(new[]
        {
            SourceText.FromString("module app\nfunc f(a:int) {}\nfunc f(b:int) {}"),
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(compilation.HasErrors);
        var session = compilation.AnalyzeDeclarations(new() { RequireCore = false }, TestContext.Current.CancellationToken);
        Assert.True(session.HasErrors);
        Assert.Contains(session.Diagnostics, diagnostic => diagnostic.Code == "SOL2003");
    }

    [Fact]
    public void PublicModuleAliasesAreAvailableThroughTransitiveReExports()
    {
        var session = AstTestSources.Session(
            "module app\nimport api",
            "module api\npublic import middle",
            "module middle\npublic import lib = implementation",
            "module implementation\nclass Item {}");
        Assert.False(session.HasErrors);
        var file = session.Modules["app"].Files[0].Root;
        Assert.Equal(LookupKind.Missing, session.Lookup(file, "Item").Kind);
        var qualified = session.LookupQualified(file, "lib", "Item");
        Assert.Equal("implementation.Item", Assert.Single(qualified.Candidates).QualifiedName);
    }

    [Fact]
    public void PrivateAliasesDoNotBecomePublicModuleQualifiers()
    {
        var session = AstTestSources.Session("module app\nimport api", "module api\nimport lib = implementation", "module implementation\nclass Item {}");
        Assert.Equal(LookupKind.Missing, session.LookupQualified(session.Modules["app"].Files[0].Root, "lib", "Item").Kind);
    }

    [Fact]
    public void ReExportedAliasCollisionsStayAmbiguous()
    {
        var session = AstTestSources.Session(
            "module app\nimport left\nimport right",
            "module left\npublic import lib = first",
            "module right\npublic import lib = second",
            "module first\nclass Item {}",
            "module second\nclass Item {}");
        Assert.Equal(LookupKind.Ambiguous, session.LookupQualified(session.Modules["app"].Files[0].Root, "lib", "Item").Kind);
    }

    [Fact]
    public void ImplicitCoreAlsoHasQualifiedAccess()
    {
        var session = AstTestSources.Session("module app", "module std.core\nclass String {}");
        Assert.Equal(LookupKind.Found, session.LookupQualified(session.Modules["app"].Files[0].Root, "std.core", "String").Kind);
    }

    [Fact]
    public void DefaultPublicAndExplicitPublicPartialPartsAreCompatible()
    {
        var session = AstTestSources.Session("module app\npartial class C {}", "module app\npublic partial class C {}");
        Assert.False(session.HasErrors);
        Assert.Equal(2, Assert.Single(session.Modules["app"].Declarations).Parts.Length);
    }
}
