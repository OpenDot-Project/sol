// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericObjectMethodFunctionValueBoundaryTests
{
    [Fact]
    public void DirectSingletonQualifierProducesTheSelectedGenericMethodReference()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(
            "object Identity { func <T> identity(value: T): T = value }\n"
                + "object Main { func main(args: []String): () {\n"
                + "val callback: (String) -> String = Identity.identity; val answer = callback(\"sol\")\n"
                + "} }")], TestContext.Current.CancellationToken);

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var program = compilation.Program.Program;
        var reference = Assert.Single(Assert.Single(program.Functions, function => function.Name == "main")
            .Body!.Statements.OfType<FirDeclare>().Select(declaration => declaration.Initializer)
            .OfType<FirFunctionReference>());
        var target = program.Functions[reference.Target];
        Assert.Equal("identity", target.Name);
        Assert.Equal("std.core.String", Assert.Single(target.SpecializationArguments).Name);
    }

    [Fact]
    public void RenamedDirectTypeImportStillProvesTheExactObjectOwner()
    {
        var library = AstTestSources.Parse(
            "object Identity { func <T> identity(value: T): T = value }",
            "library", "identity.sol");
        var application = AstTestSources.Parse(
            "import library : TextIdentity = Identity\nimport libraryAlias = library\n"
                + "object Main { func main(args: []String): () {\n"
                + "val callback: (String) -> String = TextIdentity.identity; val answer = callback(\"sol\")\n"
                + "val qualified: (String) -> String = libraryAlias.Identity.identity; val result = qualified(\"dot\")\n"
                + "} }", "app", "main.sol");

        var compilation = FirCompilation.Create([library, application], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null, string.Join('\n', compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.Empty(compilation.Diagnostics);
        var program = compilation.Program.Program;
        var references = Assert.Single(program.Functions, function => function.Name == "main")
            .Body!.Statements.OfType<FirDeclare>().Select(declaration => declaration.Initializer)
            .OfType<FirFunctionReference>().ToArray();
        Assert.Equal(2, references.Length);
        Assert.All(references, reference =>
        {
            Assert.Equal("identity", program.Functions[reference.Target].Name);
            Assert.Equal("std.core.String", Assert.Single(program.Functions[reference.Target].SpecializationArguments).Name);
        });
    }

    [Fact]
    public void SameOwnerUnqualifiedReferenceRemainsSupported()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(
            "object Identity {\n"
                + "func <T> identity(value: T): T = value\n"
                + "func callback(): (String) -> String = identity\n"
                + "}\n"
                + "object Main { func main(args: []String): () {\n"
                + "val value = Identity.callback()(\"sol\")\n"
                + "} }")], TestContext.Current.CancellationToken);

        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        var program = compilation.Program.Program;
        var callback = Assert.Single(program.Functions, function => function.Name == "callback");
        var reference = Assert.Single(callback.Body!.Statements.OfType<FirReturn>()
            .Select(statement => statement.Value).OfType<FirFunctionReference>());
        Assert.Equal("identity", program.Functions[reference.Target].Name);
        Assert.Equal("std.core.String", Assert.Single(program.Functions[reference.Target].SpecializationArguments).Name);
    }

    [Theory]
    [InlineData("val alias = Identity", "alias.identity")]
    [InlineData("", "getIdentity().identity")]
    public void LocalAndComputedReceiversFailClosedWithSol2400(string setup, string reference)
    {
        var source = "object Identity { func <T> identity(value: T): T = value }\n"
            + "func getIdentity(): Identity = Identity\n"
            + "object Main { func main(args: []String): () { " + setup + "\n"
            + " val callback: (String) -> String = " + reference
            + "; val answer = callback(\"sol\") } }";
        var compilation = FirCompilation.Create([AstTestSources.Parse(source)], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Message.Contains("direct, verified singleton reference", System.StringComparison.Ordinal));
    }

    [Fact]
    public void VerifierRejectsAFunctionReferenceForgedFromALocalAliasReceiver()
    {
        var source = "object Identity { func <T> identity(value: T): T = value }\n"
            + "object Main { func main(args: []String): () {\n"
            + "val alias = Identity; val callback: (String) -> String = alias.identity\n"
            + "val direct: (String) -> String = Identity.identity\n"
            + "} }";
        var (program, builderDiagnostics) = BuildUnverified(source);
        Assert.Contains(builderDiagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Message.Contains("direct, verified singleton reference", System.StringComparison.Ordinal));

        var main = Assert.Single(program.Functions, function => function.Name == "main");
        var aliasExpression = Assert.Single(program.Analysis.Expressions,
            binding => binding.Expression is AstMember
                { Name: "identity", Receiver: AstAtom { Spelling: "alias" } }).Expression;
        var directReference = Assert.Single(main.Body!.Statements.OfType<FirDeclare>()
            .Select(declaration => declaration.Initializer).OfType<FirFunctionReference>());
        var invalidDeclaration = Assert.Single(main.Body.Statements.OfType<FirDeclare>(), declaration =>
            declaration.Initializer is FirConstant
            && ReferenceEquals(declaration.Initializer.Origin.Element, aliasExpression.Origin.Element));
        var forgedReference = new FirFunctionReference(directReference.Target, directReference.Type, aliasExpression.Origin);
        var forgedMain = ReplaceInitializer(main, invalidDeclaration, forgedReference);

        var result = FirVerifier.Verify(Replace(program, forgedMain), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("generic object method function reference receiver",
                System.StringComparison.OrdinalIgnoreCase));
    }

    private static (FirProgram Program, ImmutableArray<OpenDot.Sol.Diagnostics.Diagnostic> Diagnostics)
        BuildUnverified(string text)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var input = ImmutableArray.Create(AstTestSources.Parse(text));
        var catalog = RuntimeCatalog.Create();
        var session = AnalysisSession.Create(input.AddRange(catalog.Files), cancellationToken: cancellation);
        var bodies = BodyAnalysis.Create(SourceAnalysis.Create(session, cancellation), cancellation);
        Assert.False(bodies.HasErrors, string.Join('\n', bodies.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        var builder = new FirBuilder(bodies, catalog, input, cancellation);
        return (builder.Build(), builder.Diagnostics.ToImmutableArray());
    }

    private static FirProgram Replace(FirProgram program, FirFunction replacement)
        => program with { Functions = program.Functions.SetItem(replacement.Id, replacement) };

    private static FirFunction ReplaceInitializer(FirFunction function, FirDeclare declaration,
        FirExpression replacement)
    {
        var statements = function.Body!.Statements.ToBuilder();
        int index = -1;
        for (int candidate = 0; candidate < statements.Count; candidate++)
        {
            if (ReferenceEquals(statements[candidate], declaration)) { index = candidate; break; }
        }
        Assert.True(index >= 0);
        statements[index] = declaration with { Initializer = replacement };
        return function with { Body = function.Body with { Statements = statements.ToImmutable() } };
    }
}
