// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnalysisImportTests
{
    [Fact]
    public void LogicalModuleIdentityIsIndependentOfPaths()
    {
        var first = AstTestSources.Parse("class First {}", "arbitrary.name", "a/one.sol");
        var second = AstTestSources.Parse("class Second {}", "arbitrary.name", "unrelated/two.sol");
        var session = AnalysisSession.Create(new[] { first, second }, new() { RequireCore = false }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(session.HasErrors);
        var module = Assert.Single(session.Modules).Value;
        Assert.Equal("arbitrary.name", module.Name);
        Assert.Equal(2, module.Files.Length);
        Assert.Equal(new[] { "First", "Second" }, module.Declarations.Select(d => d.Name));
    }

    [Fact]
    public void ImportsAreFileLocalButDeclarationsAreModuleWide()
    {
        var session = AstTestSources.Session(
            "module app\nimport library\nclass Local {}",
            "module app\nfunc use() {}",
            "module library\nclass Exported {}");
        Assert.False(session.HasErrors);
        var files = session.Modules["app"].Files;
        Assert.Equal(LookupKind.Found, session.Lookup(files[0].Root, "Exported").Kind);
        Assert.Equal(LookupKind.Missing, session.Lookup(files[1].Root, "Exported").Kind);
        Assert.Equal(LookupScope.Module, session.Lookup(files[1].Root, "Local").Scope);
    }

    [Fact]
    public void ModuleAliasOnlyProvidesQualifiedAccess()
    {
        var session = AstTestSources.Session("module app\nimport lib = library", "module library\nclass Item {}");
        Assert.False(session.HasErrors);
        var file = session.Modules["app"].Files[0].Root;
        Assert.Equal(LookupKind.Missing, session.Lookup(file, "Item").Kind);
        Assert.Equal(LookupKind.Found, session.LookupQualified(file, "lib", "Item").Kind);
        Assert.Equal(LookupKind.Missing, session.LookupQualified(file, "library", "Item").Kind);
    }

    [Fact]
    public void SelectiveBindingsPreserveLocalEqualsOriginalDirection()
    {
        var session = AstTestSources.Session(
            "module app\nimport library : Local = Original, Other",
            "module library\nclass Original {}\nclass Other {}\nclass HiddenBySelection {}");
        Assert.False(session.HasErrors);
        var file = session.Modules["app"].Files[0].Root;
        var import = Assert.Single(file.Imports);
        Assert.Equal(new[] { "Local", "Other" }, import.Items.Select(i => i.LocalName));
        Assert.Equal(new[] { "Original", "Other" }, import.Items.Select(i => i.OriginalName));
        Assert.Equal("Original", Assert.Single(session.Lookup(file, "Local").Candidates).Name);
        Assert.Equal(LookupKind.Found, session.Lookup(file, "Other").Kind);
        Assert.Equal(LookupKind.Missing, session.Lookup(file, "Original").Kind);
        Assert.Equal(LookupKind.Missing, session.Lookup(file, "HiddenBySelection").Kind);
    }

    [Fact]
    public void ImportOrderDoesNotChooseAnAmbiguousWinner()
    {
        var session = AstTestSources.Session(
            "module app\nimport left\nimport right",
            "module left\nclass Item {}",
            "module right\nclass Item {}");
        Assert.False(session.HasErrors);
        var lookup = session.Lookup(session.Modules["app"].Files[0].Root, "Item");
        Assert.Equal(LookupKind.Ambiguous, lookup.Kind);
        Assert.Equal(new[] { "left.Item", "right.Item" }, lookup.Candidates.Select(c => c.QualifiedName));
    }

    [Fact]
    public void SameModuleFunctionOverloadsRemainAnOverloadSet()
    {
        var session = AstTestSources.Session(
            "module app\nimport library",
            "module library\nfunc parse(value: int) {}\nfunc parse(value: String) {}");
        Assert.False(session.HasErrors);
        var lookup = session.Lookup(session.Modules["app"].Files[0].Root, "parse");
        Assert.Equal(LookupKind.OverloadSet, lookup.Kind);
        Assert.Equal(2, lookup.Candidates.Length);
    }

    [Fact]
    public void FunctionSetsFromDifferentModulesAreAmbiguous()
    {
        var session = AstTestSources.Session(
            "module app\nimport left\nimport right",
            "module left\nfunc use(value: int) {}",
            "module right\nfunc use(value: String) {}");
        Assert.Equal(LookupKind.Ambiguous, session.Lookup(session.Modules["app"].Files[0].Root, "use").Kind);
    }

    [Fact]
    public void CurrentModulePrecedesExplicitImportsAndCore()
    {
        var session = AstTestSources.Session(
            "module app\nimport library\nclass String {}",
            "module library\nclass String {}",
            "module std.core\nclass String {}");
        var lookup = session.Lookup(session.Modules["app"].Files[0].Root, "String");
        Assert.Equal(LookupScope.Module, lookup.Scope);
        Assert.Equal("app.String", Assert.Single(lookup.Candidates).QualifiedName);
    }

    [Fact]
    public void CoreIsImplicitAndExplicitRepeatDoesNotCreateAmbiguity()
    {
        var session = AstTestSources.Session(
            "module first", "module second\nimport std.core", "module std.core\nclass String {}");
        Assert.False(session.HasErrors);
        var implicitLookup = session.Lookup(session.Modules["first"].Files[0].Root, "String");
        Assert.Equal(LookupScope.Core, implicitLookup.Scope);
        var explicitLookup = session.Lookup(session.Modules["second"].Files[0].Root, "String");
        Assert.Equal(LookupKind.Found, explicitLookup.Kind);
        Assert.Same(Assert.Single(implicitLookup.Candidates), Assert.Single(explicitLookup.Candidates));
    }

    [Fact]
    public void PrivateAndInternalDeclarationsAreNotExported()
    {
        var session = AstTestSources.Session(
            "module app\nimport library",
            "module library\nprivate class Hidden {}\ninternal class Internal {}\nclass Visible {}");
        var app = session.Modules["app"].Files[0].Root;
        Assert.Equal(LookupKind.Missing, session.Lookup(app, "Hidden").Kind);
        Assert.Equal(LookupKind.Missing, session.Lookup(app, "Internal").Kind);
        Assert.Equal(LookupKind.Found, session.Lookup(app, "Visible").Kind);
        Assert.Equal(LookupKind.Found, session.Lookup(session.Modules["library"].Files[0].Root, "Hidden").Kind);
    }

    [Fact]
    public void PublicImportsTransitivelyReExportPublicSymbols()
    {
        var session = AstTestSources.Session(
            "module app\nimport api",
            "module api\npublic import middle",
            "module middle\npublic import implementation",
            "module implementation\nclass Item {}\nprivate class Hidden {}");
        Assert.False(session.HasErrors);
        var lookup = session.Lookup(session.Modules["app"].Files[0].Root, "Item");
        Assert.Equal("implementation.Item", Assert.Single(lookup.Candidates).QualifiedName);
        Assert.Empty(session.GetExports("api", "Hidden"));
    }

    [Fact]
    public void PrivateImportDoesNotAccidentallyReExport()
    {
        var session = AstTestSources.Session("module app\nimport api", "module api\nimport implementation", "module implementation\nclass Item {}");
        Assert.Equal(LookupKind.Missing, session.Lookup(session.Modules["app"].Files[0].Root, "Item").Kind);
    }

    [Fact]
    public void SelectivePublicImportExportsTheLocalRenamedBinding()
    {
        var session = AstTestSources.Session(
            "module app\nimport api",
            "module api\npublic import implementation : PublicName = Item",
            "module implementation\nclass Item {}");
        Assert.False(session.HasErrors);
        var file = session.Modules["app"].Files[0].Root;
        Assert.Equal("implementation.Item", Assert.Single(session.Lookup(file, "PublicName").Candidates).QualifiedName);
        Assert.Equal(LookupKind.Missing, session.Lookup(file, "Item").Kind);
    }

    [Fact]
    public void DiamondReExportsDeduplicateTheSameSymbolIdentity()
    {
        var session = AstTestSources.Session(
            "module app\nimport left\nimport right",
            "module left\npublic import base",
            "module right\npublic import base",
            "module base\nclass Item {}");
        var lookup = session.Lookup(session.Modules["app"].Files[0].Root, "Item");
        Assert.Equal(LookupKind.Found, lookup.Kind);
        Assert.Single(lookup.Candidates);
    }

    [Fact]
    public void OrdinaryImportCyclesAreLegalSignatureGroups()
    {
        var session = AstTestSources.Session("module a\nimport b", "module b\nimport a");
        Assert.False(session.HasErrors);
        Assert.True(session.Graph.HasCycle);
        Assert.Equal(new[] { "a", "b" }, Assert.Single(session.Graph.CompilationGroups));
        Assert.False(session.ReExportGraph.HasCycle);
    }

    [Fact]
    public void PublicImportCyclesProduceBoundedDiagnostics()
    {
        var session = AstTestSources.Session("module a\npublic import b\nclass A {}", "module b\npublic import a\nclass B {}");
        Assert.True(session.HasErrors);
        Assert.Equal(2, session.Diagnostics.Count(d => d.Code == "SOL2007"));
        Assert.True(session.ReExportGraph.HasCycle);
        Assert.Single(session.GetExports("a", "B"));
    }

    [Theory]
    [InlineData("import missing", "SOL2001")]
    [InlineData("import library : Missing", "SOL2008")]
    [InlineData("import library : A, A", "SOL2008")]
    [InlineData("import alias = library : A", "SOL2008")]
    public void InvalidImportsHaveSourceBoundDiagnostics(string import, string code)
    {
        var session = AstTestSources.Session("module app\n" + import, "module library\nclass A {}");
        Assert.Contains(session.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.All(session.Diagnostics, diagnostic => diagnostic.Source.Validate(diagnostic.Span));
    }

    [Fact]
    public void QualifiedLookupCannotDiscoverUnlistedModules()
    {
        var session = AstTestSources.Session("module app", "module hidden\nclass A {}");
        var file = session.Modules["app"].Files[0].Root;
        Assert.Equal(LookupKind.Missing, session.LookupQualified(file, "hidden", "A").Kind);
        Assert.Throws<ArgumentException>(() => session.GetExports("absent", "A"));
    }
}
