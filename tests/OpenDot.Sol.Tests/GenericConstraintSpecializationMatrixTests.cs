// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericConstraintSpecializationMatrixTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String {}\nopen class Throwable {}\n"
        + "open class Exception: Throwable {}\nopen class RuntimeException: Exception {}\n";

    private static TypeAnalysis AnalyzeTypes(string source)
    {
        var result = TypeAnalysis.Create(AstTestSources.Session("module app\n" + source, Core),
            TestContext.Current.CancellationToken);
        Assert.False(result.HasErrors, string.Join('\n', result.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        return result;
    }

    private static BodyAnalysis AnalyzeBody(string source)
    {
        var declarations = AstTestSources.Session("module app\n" + source, Core);
        var sources = SourceAnalysis.Create(declarations, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static AstFunction Function(TypeAnalysis types, string name) => types.Declarations.Files
        .SelectMany(file => file.Nodes).OfType<AstFunction>().Single(function => function.Name == name);

    [Fact]
    public void EveryDirectAndWhereBoundMustHoldForOneGenericArgument()
    {
        var valid = AnalyzeBody("interface A {}\ninterface B {}\nclass Both: A, B {}\n"
            + "func <T: A> requireBoth(value: T): T where T: B = value\n"
            + "func use(value: Both): Both = requireBoth(value)");

        Assert.False(valid.HasErrors, string.Join('\n', valid.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        Assert.Equal("app.Both", Assert.Single(valid.Calls).TypeArguments[0].Name);

        var invalid = AnalyzeBody("interface A {}\ninterface B {}\nclass OnlyA: A {}\n"
            + "func <T: A> requireBoth(value: T): T where T: B = value\n"
            + "func use(value: OnlyA): OnlyA = requireBoth(value)");

        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.Empty(invalid.Calls);
    }

    [Fact]
    public void MethodBoundCanUseAConstructedGenericOwnerBound()
    {
        var analysis = AnalyzeBody("interface Named<T> {}\nclass TextName: Named<String> {}\n"
            + "class Holder<T: Named<String>> { func <U> accept(value: U): U where U: T = value }\n"
            + "func use(holder: Holder<Named<String>>, value: TextName): TextName = holder.accept(value)");

        Assert.False(analysis.HasErrors, string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        var call = Assert.Single(analysis.Calls);
        Assert.Equal("accept", call.Target.Name);
        Assert.Equal("app.TextName", Assert.Single(call.TypeArguments).Name);
        Assert.Equal("std.core.String", Assert.Single(call.Receiver!.Arguments[0].Arguments).Name);
    }

    [Fact]
    public void OwnerAndMethodParametersWithTheSameSpellingKeepDistinctBoundIdentities()
    {
        var analysis = AnalyzeBody("interface OwnerBound {}\ninterface MethodBound {}\n"
            + "class OwnerValue: OwnerBound {}\nclass MethodValue: MethodBound {}\n"
            + "class Cache<T: OwnerBound> { func <T: MethodBound> replace(value: T): T = value; func owner(value: T): T = value }\n"
            + "func use(cache: Cache<OwnerValue>, value: MethodValue): MethodValue = cache.replace<MethodValue>(value)\n"
            + "func bad(cache: Cache<OwnerValue>, value: OwnerValue): OwnerValue = cache.replace<OwnerValue>(value)");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        var selected = Assert.Single(analysis.Calls);
        Assert.Equal("replace", selected.Target.Name);
        Assert.Equal("app.MethodValue", Assert.Single(selected.TypeArguments).Name);
        Assert.Equal("app.OwnerValue", Assert.Single(selected.Receiver!.Arguments).Name);
        Assert.Equal("app.MethodValue", selected.ReturnType!.Name);
    }

    [Fact]
    public void SubstitutionPreservesNestedArrayAndNullableLayers()
    {
        var types = AnalyzeTypes("func stringAnchor(value: String): String = value\n"
            + "func <T> nullableLeaves(value: [][]T?): [][]T? = value\n"
            + "func <T> nullableParameter(value: [][]T): [][]T = value\n"
            + "func <T> nullableContainer(value: ([]T)?): ([]T)? = value");
        var workspace = new TypeSpecialization(types);
        var stringType = types.Types.Single(type => type.Declaration is
            { ModuleName: "std.core", Name: "String", Arity: 0 });
        var intType = workspace.GetPrimitive("int");

        var leafFunction = Function(types, "nullableLeaves");
        var leafParameter = Assert.Single(types.GetTypeParameters(leafFunction));
        var leafShape = types.GetType(leafFunction.Parameters[0].Type!);
        var nullableLeaf = workspace.Substitute(leafShape,
            new System.Collections.Generic.Dictionary<ResolvedType, ResolvedType> { [leafParameter] = stringType },
            TestContext.Current.CancellationToken);
        Assert.Same(workspace.GetArray(workspace.GetArray(workspace.GetNullable(stringType))), nullableLeaf);

        var nestedFunction = Function(types, "nullableParameter");
        var nestedParameter = Assert.Single(types.GetTypeParameters(nestedFunction));
        var nestedShape = types.GetType(nestedFunction.Parameters[0].Type!);
        var nested = workspace.Substitute(nestedShape,
            new System.Collections.Generic.Dictionary<ResolvedType, ResolvedType>
                { [nestedParameter] = workspace.GetNullable(intType) }, TestContext.Current.CancellationToken);
        Assert.Same(workspace.GetArray(workspace.GetArray(workspace.GetNullable(intType))), nested);

        var containerFunction = Function(types, "nullableContainer");
        var containerParameter = Assert.Single(types.GetTypeParameters(containerFunction));
        var containerShape = types.GetType(containerFunction.Parameters[0].Type!);
        var container = workspace.Substitute(containerShape,
            new System.Collections.Generic.Dictionary<ResolvedType, ResolvedType> { [containerParameter] = stringType },
            TestContext.Current.CancellationToken);
        Assert.Same(workspace.GetNullable(workspace.GetArray(stringType)), container);
    }

    [Fact]
    public void InheritedGenericInterfaceProjectionSatisfiesConstructedBound()
    {
        var types = AnalyzeTypes("interface Root<T> {}\ninterface Left<U>: Root<[]U> {}\n"
            + "interface Right<V>: Root<[]V> {}\nclass Joined: Left<String>, Right<String> {}\n"
            + "func <T: Root<[]String>> accept(value: T): T = value\nvar item: Joined");
        var workspace = new TypeSpecialization(types);
        var itemDeclaration = types.Declarations.Files.SelectMany(file => file.Nodes).OfType<AstProperty>()
            .Single(property => property.Name == "item");
        var item = types.GetType(itemDeclaration.Type!);
        var accept = Function(types, "accept");
        var rootStringArray = types.GetType(Assert.Single(accept.TypeParameters).Constraint!);

        Assert.Equal(IntrinsicConversion.Reference,
            workspace.ClassifyConversion(item, rootStringArray, TestContext.Current.CancellationToken));
        Assert.Equal(ConstraintStatus.Satisfied,
            workspace.CheckConstraints(accept, new[] { item }, TestContext.Current.CancellationToken).Status);
        var inheritedRoots = workspace.GetDirectBases(item, TestContext.Current.CancellationToken)
            .SelectMany(parent => workspace.GetDirectBases(parent, TestContext.Current.CancellationToken))
            .Where(type => type.Name == "app.Root").Distinct().ToArray();
        Assert.Single(inheritedRoots);
        Assert.Equal("std.core.String", Assert.Single(inheritedRoots[0].Arguments).Arguments[0].Name);
    }

    [Fact]
    public void EqualRankGenericOverloadsRemainAmbiguousAfterTheirBoundsAreSubstituted()
    {
        var analysis = AnalyzeBody("interface A {}\ninterface B {}\nclass Both: A, B {}\n"
            + "func <T: A> choose(value: T, left: int, right: long): int = 1\n"
            + "func <T: B> choose(value: T, left: long, right: int): int = 2\n"
            + "func use(value: Both): int = choose(value, 1, 1)");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2222");
        Assert.Empty(analysis.Calls);
    }

    [Fact]
    public void BoundFailureAndValidOverloadAmbiguityDiagnosticsAreStableAndSourceOrdered()
    {
        const string source = "interface A {}\ninterface B {}\nclass Both: A, B {}\nclass OnlyA: A {}\n"
            + "func <T: A> requireBoth(value: T): T where T: B = value\n"
            + "func <T: A> choose(value: T, left: int, right: long): int = 1\n"
            + "func <T: B> choose(value: T, left: long, right: int): int = 2\n"
            + "func use(bad: OnlyA, both: Both): () { requireBoth(bad); choose(both, 1, 1) }";
        var first = AnalyzeBody(source);
        var second = AnalyzeBody(source);
        var firstDiagnostics = first.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Source.Path,
            diagnostic.Span.Start, diagnostic.Span.Length, diagnostic.Message)).ToArray();
        var secondDiagnostics = second.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Source.Path,
            diagnostic.Span.Start, diagnostic.Span.Length, diagnostic.Message)).ToArray();

        Assert.Equal(new[] { "SOL2221", "SOL2222" }, firstDiagnostics.Select(diagnostic => diagnostic.Code));
        Assert.Equal(firstDiagnostics, secondDiagnostics);
    }
}
