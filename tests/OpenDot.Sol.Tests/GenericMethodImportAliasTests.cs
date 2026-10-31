// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericMethodImportAliasTests
{
    [Fact]
    public void SelectiveObjectRenameAndModuleAliasQualifyTheCanonicalSingleton()
    {
        var library = AstTestSources.Parse(
            "object Utility { func <T> identity(value: T): T = value }", "library", "library/utility.sol");
        var app = AstTestSources.Parse(
            "import library : Id = Utility\n"
            + "import libraryAlias = library\n"
            + "func use(): () {\n"
            + "    val renamed = Id.identity<int>(42)\n"
            + "    val moduleQualified = libraryAlias.Utility.identity<int>(43)\n"
            + "}", "app", "app/main.sol");

        var compilation = FirCompilation.Create([library, app], TestContext.Current.CancellationToken, library: true);

        Assert.True(compilation.Program is not null, string.Join('\n', compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.Empty(compilation.Diagnostics);
        var method = library.Nodes.OfType<AstFunction>().Single(function => function.Name == "identity");
        var program = compilation.Program.Program;
        var use = Assert.Single(program.Functions, function => function.Name == "use");
        var calls = use.Body!.Statements.OfType<FirDeclare>()
            .Select(declaration => declaration.Initializer).OfType<FirCall>().ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call =>
        {
            var target = program.Functions[call.Target];
            Assert.Same(method.Origin.Element, target.Origin.Element);
            Assert.Equal("int", Assert.Single(target.SpecializationArguments).Name);
        });
    }

    [Fact]
    public void LocalObjectValueAliasRemainsOutsideTheReceiverFreeSubset()
    {
        var source = AstTestSources.Parse(
            "object Utility { func <T> identity(value: T): T = value }\n"
            + "func use(): () { val alias = Utility; val answer = alias.identity<int>(42) }",
            "app", "app/local-alias.sol");

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken, library: true);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Message.Contains("direct, verified singleton reference", System.StringComparison.Ordinal));
    }

    [Fact]
    public void SameNamedObjectsFromDifferentModulesKeepTheirCanonicalTargets()
    {
        var first = AstTestSources.Parse(
            "object Utility { func <T> identity(value: T): T = value }", "first", "first/utility.sol");
        var second = AstTestSources.Parse(
            "object Utility { func <T> identity(value: T): T = value }", "second", "second/utility.sol");
        var app = AstTestSources.Parse(
            "import first : FirstUtility = Utility\n"
            + "import secondModule = second\n"
            + "func use(): () {\n"
            + "    val firstValue = FirstUtility.identity<int>(1)\n"
            + "    val secondValue = secondModule.Utility.identity<int>(2)\n"
            + "}", "app", "app/main.sol");

        var compilation = FirCompilation.Create([first, second, app], TestContext.Current.CancellationToken, library: true);

        Assert.True(compilation.Program is not null, string.Join('\n', compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.Empty(compilation.Diagnostics);
        var program = compilation.Program.Program;
        var use = Assert.Single(program.Functions, function => function.Name == "use");
        var calls = use.Body!.Statements.OfType<FirDeclare>()
            .Select(declaration => declaration.Initializer).OfType<FirCall>().ToArray();
        Assert.Equal(2, calls.Length);

        var targets = calls.Select(call => program.Functions[call.Target]).ToArray();
        var firstMethod = first.Nodes.OfType<AstFunction>().Single(function => function.Name == "identity");
        var secondMethod = second.Nodes.OfType<AstFunction>().Single(function => function.Name == "identity");
        Assert.Same(firstMethod.Origin.Element, targets[0].Origin.Element);
        Assert.Same(secondMethod.Origin.Element, targets[1].Origin.Element);
        Assert.NotSame(targets[0].Origin.Element, targets[1].Origin.Element);
    }
}
