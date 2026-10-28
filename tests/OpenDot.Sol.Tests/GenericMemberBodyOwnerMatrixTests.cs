// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericMemberBodyOwnerMatrixTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}";

    [Theory]
    [InlineData("class")]
    [InlineData("struct")]
    [InlineData("ref struct")]
    [InlineData("interface")]
    public void RebindsMethodsForEverySupportedDirectOwnerKind(string ownerKind)
    {
        var session = AstTestSources.Session("module app\n" + ownerKind
            + " Carrier<T> { func retain(value: T): T = value }", Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var declaration = Assert.Single(session.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>(), candidate => candidate.Name == "Carrier");
        var method = Assert.Single(declaration.Members.OfType<AstFunction>());
        var symbol = sources.Declarations.GetDeclaredSymbol(declaration)!;
        var stringDeclaration = Assert.Single(session.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>(), candidate => candidate.Name == "String");
        var text = analysis.Types.Nominal(sources.Declarations.GetDeclaredSymbol(stringDeclaration)!, []);
        var owner = analysis.Types.Nominal(symbol, [text]);

        var body = analysis.GetGenericMemberBody(method, owner, [], TestContext.Current.CancellationToken);

        Assert.Equal(BodyBindingStatus.Bound, body.Summary.Status);
        Assert.Equal(text, body.Summary.ReturnType);
        Assert.Equal(text, Assert.Single(body.OwnerTypeArguments));
        Assert.Same(symbol, body.ConstructedOwner!.Declaration);
    }

    [Fact]
    public void SubstitutionRecursesThroughOwnerAndMethodArguments()
    {
        const string source = "module app\nclass Pair<A, B> {}\n"
            + "class Holder<T> { func <U> relay(value: Pair<T, []U?>): Pair<T, []U?> = value }";
        var session = AstTestSources.Session(source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var holder = session.Files.SelectMany(file => file.Nodes).OfType<AstTypeDeclaration>()
            .Single(declaration => declaration.Name == "Holder");
        var method = Assert.Single(holder.Members.OfType<AstFunction>());
        var stringDeclaration = session.Files.SelectMany(file => file.Nodes).OfType<AstTypeDeclaration>()
            .Single(declaration => declaration.Name == "String");
        var stringType = analysis.Types.Nominal(sources.Declarations.GetDeclaredSymbol(stringDeclaration)!, []);
        var holderSymbol = sources.Declarations.GetDeclaredSymbol(holder)!;
        var owner = analysis.Types.Nominal(holderSymbol, [stringType]);
        var specialized = analysis.GetGenericMemberBody(method, owner,
            [analysis.Types.GetPrimitive("int")], TestContext.Current.CancellationToken);

        Assert.Equal(BodyBindingStatus.Bound, specialized.Summary.Status);
        var input = specialized.Expressions.Single(binding => binding.Expression is AstAtom { Spelling: "value" });
        Assert.Equal(specialized.Summary.ReturnType, input.Type);
        Assert.Equal("app.Pair", specialized.Summary.ReturnType!.Name);
        Assert.Equal(stringType, specialized.Summary.ReturnType.Arguments[0]);
        var nullableArray = specialized.Summary.ReturnType.Arguments[1];
        Assert.Equal(ResolvedTypeKind.Array, nullableArray.Kind);
        Assert.True(nullableArray.Arguments[0].IsNullable);
        Assert.Equal("int", nullableArray.Arguments[0].Arguments[0].Name);
    }

    [Fact]
    public void MissingSourceBodyIsRejectedBeforeAViewIsBuilt()
    {
        const string source = "module app\nabstract class Holder<T> { abstract func read(value: T): T }";
        var session = AstTestSources.Session(source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var owner = Assert.Single(session.Files.SelectMany(file => file.Nodes).OfType<AstTypeDeclaration>(),
            declaration => declaration.Name == "Holder");
        var method = Assert.Single(owner.Members.OfType<AstFunction>());
        var ownerType = analysis.Types.Nominal(sources.Declarations.GetDeclaredSymbol(owner)!,
            [analysis.Types.GetPrimitive("int")]);

        var failure = Assert.Throws<System.ArgumentException>(() => analysis.GetGenericMemberBody(
            method, ownerType, [], TestContext.Current.CancellationToken));

        Assert.Contains("source body", failure.Message, System.StringComparison.OrdinalIgnoreCase);
    }
}
