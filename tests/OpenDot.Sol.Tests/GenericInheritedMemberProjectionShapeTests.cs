// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericInheritedMemberProjectionShapeTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}";

    [Fact]
    public void PreservesEveryBuiltinNumericTypeAcrossTheOwnerProjection()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "class Leaf<T>: Root<T> {}");
        var method = Method(analysis, "echo");
        var declaration = TypeDeclaration(analysis, "Leaf");
        string[] primitiveNames = ["sbyte", "short", "int", "long", "byte", "uint", "ulong",
            "half", "single", "double", "quad"];

        foreach (string primitiveName in primitiveNames)
        {
            var primitive = analysis.Types.GetPrimitive(primitiveName);
            var receiver = analysis.Types.Nominal(
                analysis.Sources.Declarations.GetDeclaredSymbol(declaration)!, [primitive]);
            var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
                TestContext.Current.CancellationToken);

            Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
            var owner = Assert.Single(result.Projection.Candidates).ConstructedOwner;
            Assert.Equal(primitive, Assert.Single(owner.Arguments));
            Assert.Equal(primitive, result.Body!.Summary.ReturnType);
            Assert.Equal(BodyBindingStatus.Bound, result.Body.Summary.Status);
        }
    }

    [Fact]
    public void ReordersAndComposesOwnerTypeArgumentsInsteadOfCopyingReceiverArguments()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<A, B> { func select(value: B): B = value }\n"
            + "interface Middle<X, Y>: Root<Y, X> {}\n"
            + "class Leaf<T, U>: Middle<T, U> {}");
        var method = Method(analysis, "select");
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"),
            analysis.Types.GetPrimitive("int"));

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        Assert.Equal(analysis.Types.GetPrimitive("int"), candidate.ConstructedOwner.Arguments[0]);
        Assert.Equal(Nominal(analysis, "String"), candidate.ConstructedOwner.Arguments[1]);
        Assert.Equal(Nominal(analysis, "String"), result.Body!.Summary.ReturnType);
        Assert.Equal(BodyBindingStatus.Bound, result.Body.Summary.Status);
    }

    [Fact]
    public void KeepsNullableOwnerTypeArgumentsDistinctFromNonNullableArguments()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "class Leaf<T>: Root<T> {}");
        var method = Method(analysis, "echo");
        var nullableString = analysis.Types.GetNullable(Nominal(analysis, "String"));
        var receiver = Constructed(analysis, "Leaf", nullableString);

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        Assert.Same(nullableString, Assert.Single(candidate.ConstructedOwner.Arguments));
        Assert.Same(nullableString, result.Body!.Summary.ReturnType);
        Assert.Equal(BodyBindingStatus.Bound, result.Body.Summary.Status);
    }

    [Fact]
    public void PreservesFunctionParameterModesAndEffectsInsideTheConstructedOwner()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "class Leaf<T>: Root<T> {}");
        var method = Method(analysis, "echo");
        var text = Nominal(analysis, "String");
        var ioException = Nominal(analysis, "IOException");
        var function = analysis.Types.GetFunction([text], text, [AstPassingMode.Ref], [ioException]);
        var receiver = Constructed(analysis, "Leaf", function);

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var candidate = Assert.Single(result.Projection.Candidates);
        var projectedFunction = Assert.Single(candidate.ConstructedOwner.Arguments);
        Assert.Same(function, projectedFunction);
        Assert.Equal(ResolvedTypeKind.Function, projectedFunction.Kind);
        Assert.Equal(AstPassingMode.Ref, Assert.Single(projectedFunction.ParameterModes));
        Assert.Equal(text, projectedFunction.Arguments[0]);
        Assert.Equal(text, projectedFunction.Arguments[1]);
        Assert.Equal(ioException, Assert.Single(projectedFunction.Effects));
        Assert.Equal(function, result.Body!.Summary.ReturnType);
        Assert.Equal(BodyBindingStatus.Bound, result.Body.Summary.Status);
    }

    [Fact]
    public void ReboundBodyUsesTheConstructedDeclaringOwnerForThis()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func <U> echo(value: T, marker: U): U { this; marker } }\n"
            + "interface Middle<V>: Root<[]V> {}\n"
            + "class Leaf<V>: Middle<V> {}");
        var method = Method(analysis, "echo");
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver,
            [analysis.Types.GetPrimitive("int")], TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        var projectedOwner = Assert.Single(result.Projection.Candidates).ConstructedOwner;
        var thisExpression = method.Descendants().OfType<AstAtom>().Single(atom => atom.Spelling == "this");
        Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
        Assert.Equal(projectedOwner, result.Body.ConstructedOwner);
        Assert.Equal(projectedOwner, result.Body.GetExpression(thisExpression)!.Type);
        Assert.NotEqual(receiver, result.Body.GetExpression(thisExpression)!.Type);
    }

    [Fact]
    public void NullableAndStructuralReceiversHaveNoNominalOwnerProjection()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func echo(value: T): T = value }\n"
            + "class Leaf<T>: Root<T> {}");
        var method = Method(analysis, "echo");
        var leaf = Constructed(analysis, "Leaf", Nominal(analysis, "String"));
        var receivers = new[]
        {
            analysis.Types.GetNullable(leaf),
            analysis.Types.GetArray(Nominal(analysis, "String")),
            analysis.Types.GetTuple([Nominal(analysis, "String"), analysis.Types.GetPrimitive("int")]),
            analysis.Types.GetPrimitive("int"),
        };

        foreach (var receiver in receivers)
        {
            var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
                TestContext.Current.CancellationToken);

            Assert.Equal(OwnerProjectionStatus.Missing, result.Projection.Status);
            Assert.Empty(result.Projection.Candidates);
            Assert.Null(result.Projection.Reason);
            Assert.Null(result.Body);
        }
    }

    [Fact]
    public void RejectsOpenMethodArgumentsEvenWhenThereWouldBeNoOwnerRoute()
    {
        var analysis = Analyze("module app\n"
            + "interface Root<T> { func <U> echo(value: U): U = value }\n"
            + "class Unrelated {}\n"
            + "func <V> inspect(value: V): V = value");
        var method = Method(analysis, "echo");
        var unrelated = Constructed(analysis, "Unrelated");
        var inspect = analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstFunction>().Single(function => function.Name == "inspect");
        var openMethodArgument = analysis.Sources.Types.GetType(inspect.Parameters.Single().Type!);

        var error = Assert.Throws<ArgumentException>(() => analysis.GetGenericMemberBodyForReceiver(
            method, unrelated, [openMethodArgument], TestContext.Current.CancellationToken));

        Assert.Contains("closed type arguments", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session(source,
            Core + "\nclass IOException: Exception {}");
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static AstFunction Method(BodyAnalysis analysis, string name) => analysis.Sources.Declarations.Files
        .SelectMany(file => file.Nodes).OfType<AstTypeDeclaration>().SelectMany(owner => owner.Members)
        .OfType<AstFunction>().Single(method => method.Name == name);

    private static AstTypeDeclaration TypeDeclaration(BodyAnalysis analysis, string name) =>
        analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>().Single(type => type.Name == name);

    private static ResolvedType Nominal(BodyAnalysis analysis, string name) => analysis.Types.Nominal(
        analysis.Sources.Declarations.GetDeclaredSymbol(TypeDeclaration(analysis, name))!, []);

    private static ResolvedType Constructed(BodyAnalysis analysis, string name, params ResolvedType[] arguments) =>
        analysis.Types.Nominal(analysis.Sources.Declarations.GetDeclaredSymbol(TypeDeclaration(analysis, name))!,
            arguments.ToImmutableArray());
}
