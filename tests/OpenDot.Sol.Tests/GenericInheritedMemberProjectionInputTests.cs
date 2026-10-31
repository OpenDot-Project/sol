// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericInheritedMemberProjectionInputTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}";

    [Fact]
    public void NullMethodAndReceiverAreRejectedBeforeGraphTraversal()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "class Leaf<T>: Root<T> {}");
        var method = Method(analysis, "echo");
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));
        var token = TestContext.Current.CancellationToken;

        var nullMethod = Assert.Throws<ArgumentNullException>(() =>
            analysis.GetGenericMemberBodyForReceiver(null!, receiver, [], token));
        var nullReceiver = Assert.Throws<ArgumentNullException>(() =>
            analysis.GetGenericMemberBodyForReceiver(method, null!, [], token));

        Assert.Equal("function", nullMethod.ParamName);
        Assert.Equal("receiver", nullReceiver.ParamName);
    }

    [Fact]
    public void FreeGenericFunctionsCannotMasqueradeAsGenericOwnerMembers()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "func <U> identity(value: U): U = value\n"
            + "class Leaf<T>: Root<T> {}");
        var method = analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstFunction>().Single(function => function.Name == "identity");
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));

        var error = Assert.Throws<ArgumentException>(() => analysis.GetGenericMemberBodyForReceiver(
            method, receiver, [analysis.Types.GetPrimitive("int")], TestContext.Current.CancellationToken));

        Assert.Contains("directly declared generic owner method", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MethodsOnNonGenericOwnersAreOutsideTheProjectionContract()
    {
        var analysis = Analyze("module app\n"
            + "interface Root { func echo(value: String): String = value }\n"
            + "class Leaf: Root {}");
        var method = Method(analysis, "echo");
        var receiver = Constructed(analysis, "Leaf");

        var error = Assert.Throws<ArgumentException>(() => analysis.GetGenericMemberBodyForReceiver(
            method, receiver, [], TestContext.Current.CancellationToken));

        Assert.Contains("directly declared generic owner method", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultMethodArgumentVectorIsNotConfusedWithAnExplicitEmptyVector()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func <U> echo(value: U): U = value }\n"
            + "class Leaf<T>: Root<T> {}");
        var method = Method(analysis, "echo");
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));
        ImmutableArray<ResolvedType> defaultVector = default;

        var error = Assert.Throws<ArgumentException>(() => analysis.GetGenericMemberBodyForReceiver(
            method, receiver, defaultVector, TestContext.Current.CancellationToken));

        Assert.Equal("methodTypeArguments", error.ParamName);
        Assert.Contains("every ordered method type argument", error.Message, StringComparison.Ordinal);
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session(source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static AstFunction Method(BodyAnalysis analysis, string name) =>
        analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>().SelectMany(type => type.Members)
            .OfType<AstFunction>().Single(method => method.Name == name);

    private static AstTypeDeclaration Type(BodyAnalysis analysis, string name) =>
        analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>().Single(type => type.Name == name);

    private static ResolvedType Nominal(BodyAnalysis analysis, string name) => analysis.Types.Nominal(
        analysis.Sources.Declarations.GetDeclaredSymbol(Type(analysis, name))!, []);

    private static ResolvedType Constructed(BodyAnalysis analysis, string name, params ResolvedType[] arguments) =>
        analysis.Types.Nominal(analysis.Sources.Declarations.GetDeclaredSymbol(Type(analysis, name))!,
            arguments.ToImmutableArray());
}
