// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AstTypeTests
{
    [Theory]
    [InlineData("int", "int", AstTypeKind.Named)]
    [InlineData("sbyte", "sbyte", AstTypeKind.Named)]
    [InlineData("byte", "byte", AstTypeKind.Named)]
    [InlineData("std.core.String", "std.core.String", AstTypeKind.Named)]
    [InlineData("!", "!", AstTypeKind.Bottom)]
    [InlineData("!?", "(!)?", AstTypeKind.Nullable)]
    [InlineData("String?", "(String)?", AstTypeKind.Nullable)]
    [InlineData("String??", "(String)?", AstTypeKind.Nullable)]
    [InlineData("(String)", "String", AstTypeKind.Named)]
    [InlineData("((String))", "String", AstTypeKind.Named)]
    [InlineData("(String?)?", "(String)?", AstTypeKind.Nullable)]
    [InlineData("[]String", "[]String", AstTypeKind.Array)]
    [InlineData("[]String?", "[](String)?", AstTypeKind.Array)]
    [InlineData("[](String?)", "[](String)?", AstTypeKind.Array)]
    [InlineData("([]String)?", "([]String)?", AstTypeKind.Nullable)]
    [InlineData("([]String?)?", "([](String)?)?", AstTypeKind.Nullable)]
    [InlineData("[][]String?", "[][](String)?", AstTypeKind.Array)]
    [InlineData("[]([]String)?", "[]([]String)?", AstTypeKind.Array)]
    [InlineData("[4]String?", "[4](String)?", AstTypeKind.FixedArray)]
    [InlineData("([4]String)?", "([4]String)?", AstTypeKind.Nullable)]
    [InlineData("[1_024]int", "[1024]int", AstTypeKind.FixedArray)]
    [InlineData("()", "()", AstTypeKind.Tuple)]
    [InlineData("(int,)", "(int,)", AstTypeKind.Tuple)]
    [InlineData("(int,String)", "(int,String)", AstTypeKind.Tuple)]
    [InlineData("(int, String?)?", "((int,(String)?))?", AstTypeKind.Nullable)]
    [InlineData("Box<int>", "Box<int>", AstTypeKind.Named)]
    [InlineData("Box<Box<int>>", "Box<Box<int>>", AstTypeKind.Named)]
    [InlineData("Box<Box<int>>?", "(Box<Box<int>>)?", AstTypeKind.Nullable)]
    [InlineData("Map<String,[]int>", "Map<String,[]int>", AstTypeKind.Named)]
    [InlineData("()->int", "()->int", AstTypeKind.Function)]
    [InlineData("(int)->String", "(int)->String", AstTypeKind.Function)]
    [InlineData("(ref int,out String)->()", "(ref int,out String)->()", AstTypeKind.Function)]
    [InlineData("(int)->!", "(int)->!", AstTypeKind.Function)]
    [InlineData("((int)->String)?", "((int)->String)?", AstTypeKind.Nullable)]
    [InlineData("(int)->String throws IOException", "(int)->String throws IOException", AstTypeKind.Function)]
    [InlineData("(int)->String throws A,B", "(int)->String throws A,B", AstTypeKind.Function)]
    public void TypesNormalizeWithoutResolvingNames(string spelling, string expected, AstTypeKind kind)
    {
        var ast = AstTestSources.Parse("var value: " + spelling);
        var property = Assert.Single(ast.Root.Declarations.OfType<AstProperty>());
        Assert.NotNull(property.Type);
        Assert.Equal(kind, property.Type.Shape.Kind);
        Assert.Equal(expected, property.Type.Shape.CanonicalName);
        Assert.Equal(spelling, property.Type.Origin.Text);
    }

    [Fact]
    public void NullableArrayElementAndNullableArrayAreDifferentShapes()
    {
        var ast = AstTestSources.Parse("var elements: []String?\nvar array: ([]String)?");
        var types = ast.Root.Declarations.OfType<AstProperty>().Select(p => p.Type!.Shape).ToArray();
        Assert.Equal(AstTypeKind.Array, types[0].Kind);
        Assert.Equal(AstTypeKind.Nullable, Assert.Single(types[0].Arguments).Kind);
        Assert.Equal(AstTypeKind.Nullable, types[1].Kind);
        Assert.Equal(AstTypeKind.Array, Assert.Single(types[1].Arguments).Kind);
    }

    [Fact]
    public void GroupingDoesNotChangeNormalizedTypeIdentity()
    {
        var ast = AstTestSources.Parse("var a: []String?\nvar b: [](String?)\nvar c: []((String?))");
        var names = ast.Root.Declarations.OfType<AstProperty>().Select(p => p.Type!.Shape.CanonicalName).ToArray();
        Assert.Equal(names[0], names[1]);
        Assert.Equal(names[1], names[2]);
    }

    [Fact]
    public void FunctionPassingModesAndEffectsRemainExplicit()
    {
        var ast = AstTestSources.Parse("var callback: (ref int, out String, byte)->long throws E1, E2");
        var shape = Assert.Single(ast.Root.Declarations.OfType<AstProperty>()).Type!.Shape;
        Assert.Equal(new[] { AstPassingMode.Ref, AstPassingMode.Out, AstPassingMode.Value }, shape.ParameterModes);
        Assert.Equal(new[] { "E1", "E2" }, shape.Effects.Select(e => e.CanonicalName));
        Assert.Equal("long", shape.Arguments[^1].CanonicalName);
    }

    [Fact]
    public void NullableFactoryIsIdempotentAndDoesNotInventASecondAbsenceLayer()
    {
        var named = TypeShape.Named("String");
        var nullable = TypeShape.Nullable(named);
        Assert.Same(nullable, TypeShape.Nullable(nullable));
        Assert.Equal("(String)?", nullable.CanonicalName);
        Assert.Throws<ArgumentNullException>(() => TypeShape.Nullable(null!));
        Assert.Throws<ArgumentException>(() => TypeShape.Named(" "));
        Assert.Throws<ArgumentNullException>(() => TypeShape.Named("Box", null!));
    }
}
