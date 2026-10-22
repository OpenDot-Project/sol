// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AstDeclarationTests
{
    [Fact]
    public void GroupedParametersExpandWithSharedOriginAndIndependentNames()
    {
        var ast = AstTestSources.Parse("func add(a, b: int, ref x, y: long, out text: String): int = a + b");
        var function = Assert.Single(ast.Root.Declarations.OfType<AstFunction>());
        Assert.Equal(new[] { "a", "b", "x", "y", "text" }, function.Parameters.Select(p => p.Name));
        Assert.Equal(new[] { "int", "int", "long", "long", "String" },
            function.Parameters.Select(p => p.Type!.Shape.CanonicalName));
        Assert.Same(function.Parameters[0].GroupOrigin.Element, function.Parameters[1].GroupOrigin.Element);
        Assert.NotSame(function.Parameters[0].NameOrigin.Element, function.Parameters[1].NameOrigin.Element);
        Assert.Equal(AstPassingMode.Ref, function.Parameters[2].PassingMode);
        Assert.Equal(AstPassingMode.Ref, function.Parameters[3].PassingMode);
        Assert.Equal(AstPassingMode.Out, function.Parameters[4].PassingMode);
    }

    [Fact]
    public void ParameterDefaultAndVarargAreNotEvaluatedDuringNormalization()
    {
        var ast = AstTestSources.Parse("func f(value: int = next(), vararg rest: Object) {}");
        var function = Assert.Single(ast.Root.Declarations.OfType<AstFunction>());
        Assert.IsType<AstCall>(function.Parameters[0].DefaultValue);
        Assert.Equal(AstPassingMode.Vararg, function.Parameters[1].PassingMode);
        Assert.Null(function.Parameters[1].DefaultValue);
        Assert.Equal(AstBodyKind.Block, function.Body.Kind);
    }

    [Fact]
    public void ExpressionAndBlockBodiesHaveExplicitTailCandidates()
    {
        var ast = AstTestSources.Parse("func expression(a: int) = a + 1\nfunc block(a: int): int { a + 1 }\nfunc empty() {}\nfunc signature(): int");
        var functions = ast.Root.Declarations.OfType<AstFunction>().ToArray();
        Assert.Equal(AstBodyKind.Expression, functions[0].Body.Kind);
        Assert.IsType<AstOperation>(functions[0].Body.TailExpression);
        Assert.Equal(AstBodyKind.Block, functions[1].Body.Kind);
        Assert.IsType<AstOperation>(functions[1].Body.TailExpression);
        Assert.Null(functions[2].Body.TailExpression);
        Assert.Equal(AstBodyKind.Missing, functions[3].Body.Kind);
    }

    [Fact]
    public void AssignmentTailDoesNotPretendToBeAFunctionResult()
    {
        var ast = AstTestSources.Parse("func f() { var x = 0; x = 1 }");
        var function = Assert.Single(ast.Root.Declarations.OfType<AstFunction>());
        Assert.Null(function.Body.TailExpression);
        var body = Assert.IsType<AstBlock>(function.Body.Node);
        Assert.IsType<AstAssignment>(body.Statements[^1]);
    }

    [Fact]
    public void ConstructorPropertiesAndDelegationKeepTheirDeclarationOrigin()
    {
        var ast = AstTestSources.Parse("class Box<T>(val value: T, var count: int) { constructor(value: T): this(value, 1) {} }");
        var type = Assert.Single(ast.Root.Declarations.OfType<AstTypeDeclaration>());
        Assert.Equal(1, type.Arity);
        Assert.True(type.HasInitialization);
        var primary = Assert.IsType<AstConstructor>(type.PrimaryConstructor);
        Assert.True(primary.Parameters[0].IsProperty);
        Assert.False(primary.Parameters[0].IsMutable);
        Assert.True(primary.Parameters[1].IsMutable);
        var secondary = Assert.Single(type.Members.OfType<AstConstructor>());
        Assert.Equal("this", secondary.Delegation!.Children[0].Origin.Text);
        Assert.NotNull(secondary.Body);
    }

    [Fact]
    public void AccessorExpressionIsNotAPropertyInitializer()
    {
        var ast = AstTestSources.Parse("class Person { val fullName: String { get = this.name } }");
        var type = Assert.Single(ast.Root.Declarations.OfType<AstTypeDeclaration>());
        var property = Assert.Single(type.Members.OfType<AstProperty>());
        Assert.Null(property.Initializer);
        Assert.Null(property.Delegate);
        var getter = Assert.Single(property.Accessors);
        Assert.Equal("get", getter.Name);
        Assert.IsType<AstMember>(getter.Body.TailExpression);
    }

    [Fact]
    public void AutoAccessorsAndStoredInitializerRemainSeparate()
    {
        var ast = AstTestSources.Parse("class Counter { var count: int { get; private set; } = 0 }");
        var type = Assert.Single(ast.Root.Declarations.OfType<AstTypeDeclaration>());
        var property = Assert.Single(type.Members.OfType<AstProperty>());
        Assert.True(property.IsMutable);
        Assert.Equal("0", Assert.IsType<AstAtom>(property.Initializer).Spelling);
        var accessors = property.Accessors.ToArray();
        Assert.Equal(new[] { "get", "set" }, accessors.Select(a => a.Name));
        Assert.All(accessors, accessor => Assert.Equal(AstBodyKind.Missing, accessor.Body.Kind));
        Assert.Contains("private", accessors[1].Modifiers);
    }

    [Fact]
    public void DelegateExpressionAndIndexerAreStructured()
    {
        var ast = AstTestSources.Parse("class Store { val value: int by Delegate(); this[index: int]: int { get = index } }");
        var type = Assert.Single(ast.Root.Declarations.OfType<AstTypeDeclaration>());
        var property = Assert.Single(type.Members.OfType<AstProperty>());
        Assert.IsType<AstCall>(property.Delegate);
        Assert.Null(property.Initializer);
        var indexer = Assert.Single(type.Members.OfType<AstIndexer>());
        Assert.Equal("index", Assert.Single(indexer.Parameters).Name);
        Assert.Equal("int", indexer.ValueType!.Shape.CanonicalName);
    }

    [Fact]
    public void CheckedEffectsAndReferenceReturnsStayOnTheSignature()
    {
        var ast = AstTestSources.Parse("func locate(ref value: int): ref readonly int throws IOException { return ref value }");
        var function = Assert.Single(ast.Root.Declarations.OfType<AstFunction>());
        Assert.True(function.ReturnsByReference);
        Assert.Equal("int", function.ReturnType!.Shape.CanonicalName);
        Assert.Equal("IOException", Assert.Single(function.ThrownTypes).Shape.CanonicalName);
        var body = Assert.IsType<AstBlock>(function.Body.Node);
        Assert.True(Assert.IsType<AstReturn>(Assert.Single(body.Statements)).IsByReference);
    }

    [Fact]
    public void PartialAndCompanionFlagsDescribeSpellingWithoutMerging()
    {
        var ast = AstTestSources.Parse("partial class C { companion object { func create() = C() } }\npartial class C {}");
        var types = ast.Root.Declarations.OfType<AstTypeDeclaration>().ToArray();
        Assert.Equal(2, types.Length);
        Assert.All(types, type => Assert.True(type.IsPartial));
        Assert.All(types, type => Assert.False(type.IsCompanion));
        var companion = Assert.Single(types[0].Members.OfType<AstTypeDeclaration>());
        Assert.True(companion.IsCompanion);
        Assert.Null(companion.Name);
    }

    [Fact]
    public void EnumPayloadsAndExternFunctionsAreNotOrdinaryProperties()
    {
        var ast = AstTestSources.Parse("enum Result<T> { Empty, Value(value: T), }\nextern \"c\" { func sum(a, b: int): int; }");
        var type = Assert.Single(ast.Root.Declarations.OfType<AstTypeDeclaration>());
        var variants = type.Members.OfType<AstEnumVariant>().ToArray();
        Assert.Equal(new[] { "Empty", "Value" }, variants.Select(v => v.Name));
        Assert.Empty(variants[0].Parameters);
        Assert.Equal("T", Assert.Single(variants[1].Parameters).Type!.Shape.CanonicalName);
        var external = Assert.Single(ast.Root.Declarations.OfType<AstExtern>());
        Assert.Equal("sum", Assert.Single(external.Functions).Name);
    }

    [Fact]
    public void DestructuringDoesNotInventAnOrdinarySingleBindingName()
    {
        var ast = AstTestSources.Parse("func f() { val (x, _) = pair }");
        var binding = Assert.Single(ast.Root.Descendants().OfType<AstProperty>());
        Assert.Equal(new[] { "x", "_" }, binding.BindingNames);
        Assert.Null(binding.Name);
    }
}
