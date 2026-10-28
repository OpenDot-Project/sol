// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class BodyAnalysisLocalTypeTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String {}\n"
        + "open class Throwable {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\n";

    private static BodyAnalysis Analyze(string body)
    {
        var session = AstTestSources.Session("module app\n" + body, Core);
        return BodyAnalysis.Create(SourceAnalysis.Create(session, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
    }

    private static LocalSymbol Local(BodyAnalysis analysis, string name)
        => Assert.Single(analysis.Sources.Lexical.Scopes.SelectMany(scope => scope.Symbols),
            symbol => symbol.Name == name);

    [Fact]
    public void ParametersAndBothWrittenAndInferredLocalTypesAreAvailable()
    {
        var analysis = Analyze("func calculate(input: int): int { val inferred = input; "
            + "val written: long = 1; inferred }");

        Assert.Equal("int", analysis.GetLocalType(Local(analysis, "input"))!.Name);
        Assert.Equal("int", analysis.GetLocalType(Local(analysis, "inferred"))!.Name);
        Assert.Equal("long", analysis.GetLocalType(Local(analysis, "written"))!.Name);
    }

    [Fact]
    public void ConstructorPropertyRetainsItsWrittenType()
    {
        var analysis = Analyze("class Box(val count: long) { func read(): long = count }");
        var property = Assert.Single(analysis.Sources.Lexical.Scopes.SelectMany(scope => scope.Symbols),
            symbol => symbol.Name == "count" && symbol.Kind == LocalSymbolKind.ConstructorProperty);

        Assert.Equal("long", analysis.GetLocalType(property)!.Name);
    }

    [Fact]
    public void ContextualLambdaParameterTypeIsPublishedByBodyAnalysis()
    {
        var analysis = Analyze("func invoke(): int { val callback: (int) -> int = { item -> item }; callback(1) }");

        Assert.Equal("int", analysis.GetLocalType(Local(analysis, "item"))!.Name);
    }

    [Fact]
    public void OutVariableTypeComesFromTheSelectedCallableParameter()
    {
        var analysis = Analyze("func fill(out result: long) { result = 1 }\n"
            + "func read(): long { fill(out var target); target }");

        Assert.Equal("long", analysis.GetLocalType(Local(analysis, "target"))!.Name);
    }

    [Fact]
    public void MissingTypeProofReturnsNullInsteadOfAnErrorType()
    {
        var analysis = Analyze("func inspect() { var unresolved: MissingType }");

        Assert.Null(analysis.GetLocalType(Local(analysis, "unresolved")));
    }

    [Fact]
    public void ErrorNestedInsideACompositeTypeIsNotExposedAsResolved()
    {
        var analysis = Analyze("func inspect() { var unresolved: []MissingType }");

        Assert.Null(analysis.GetLocalType(Local(analysis, "unresolved")));
    }

    [Fact]
    public void UntypedUninitializedLocalHasNoProvenType()
    {
        var analysis = Analyze("func inspect() { var unresolved = [] }");

        Assert.Null(analysis.GetLocalType(Local(analysis, "unresolved")));
    }

    [Fact]
    public void StructurallyIdenticalLocalFromAnotherSnapshotIsRejected()
    {
        const string body = "func read(value: int): int = value";
        var first = Analyze(body);
        var second = Analyze(body);
        var foreign = Local(second, "value");

        Assert.Throws<ArgumentException>(() => first.GetLocalType(foreign));
    }

    [Fact]
    public void NullLocalArgumentIsRejected()
    {
        var analysis = Analyze("func read(value: int): int = value");

        Assert.Throws<ArgumentNullException>(() => analysis.GetLocalType(null!));
    }
}
