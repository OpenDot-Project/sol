// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class TypeBindingTests
{
    private static TypeAnalysis Bind(params string[] sources)
    {
        return TypeAnalysis.Create(AstTestSources.Session(sources), TestContext.Current.CancellationToken);
    }

    private static AstType PropertyType(TypeAnalysis analysis, string module, string name)
    {
        return analysis.Declarations.Modules[module].Files.SelectMany(f => f.Root.Declarations)
            .OfType<AstProperty>().Single(property => property.Name == name).Type!;
    }

    [Theory]
    [InlineData("sbyte")]
    [InlineData("short")]
    [InlineData("int")]
    [InlineData("long")]
    [InlineData("byte")]
    [InlineData("uint")]
    [InlineData("ulong")]
    [InlineData("half")]
    [InlineData("single")]
    [InlineData("double")]
    [InlineData("quad")]
    [InlineData("bool")]
    public void PrimitiveNamesResolveWithoutInventingCoreClasses(string name)
    {
        var analysis = Bind("module app\nvar value: " + name);
        Assert.False(analysis.HasErrors);
        var type = analysis.GetType(PropertyType(analysis, "app", "value"));
        Assert.Equal(ResolvedTypeKind.Primitive, type.Kind);
        Assert.Equal(name, type.Name);
        Assert.Null(type.Declaration);
    }

    [Fact]
    public void ImportAliasesAndQualificationsResolveToTheSameNominalIdentity()
    {
        var analysis = Bind(
            "module app\nimport lib = library\nimport library : Renamed = Item\nvar a: Renamed\nvar b: lib.Item\nvar c: library.Item",
            "module library\nclass Item {}");
        Assert.False(analysis.HasErrors);
        var a = analysis.GetType(PropertyType(analysis, "app", "a"));
        Assert.Same(a, analysis.GetType(PropertyType(analysis, "app", "b")));
        Assert.Same(a, analysis.GetType(PropertyType(analysis, "app", "c")));
        Assert.Equal("library.Item", a.Name);
    }

    [Fact]
    public void NullableArrayElementAndNullableArrayHaveDifferentIdentity()
    {
        var analysis = Bind("module app\nclass String {}\nvar a: []String?\nvar b: [](String?)\nvar c: ([]String)?");
        Assert.False(analysis.HasErrors);
        var a = analysis.GetType(PropertyType(analysis, "app", "a"));
        Assert.Same(a, analysis.GetType(PropertyType(analysis, "app", "b")));
        var c = analysis.GetType(PropertyType(analysis, "app", "c"));
        Assert.NotSame(a, c);
        Assert.Equal(ResolvedTypeKind.Array, a.Kind);
        Assert.True(Assert.Single(a.Arguments).IsNullable);
        Assert.Equal(ResolvedTypeKind.Nullable, c.Kind);
    }

    [Fact]
    public void ClosedGenericTypesShareDefinitionsButNotDifferentArguments()
    {
        var analysis = Bind("module app\nclass Box<T> {}\nvar a: Box<int>\nvar b: Box<int>\nvar c: Box<byte>");
        Assert.False(analysis.HasErrors);
        var a = analysis.GetType(PropertyType(analysis, "app", "a"));
        var b = analysis.GetType(PropertyType(analysis, "app", "b"));
        var c = analysis.GetType(PropertyType(analysis, "app", "c"));
        Assert.Same(a, b);
        Assert.NotSame(a, c);
        Assert.Same(a.Declaration, c.Declaration);
    }

    [Fact]
    public void GenericAritySelectsTheCorrectNominalDefinition()
    {
        var analysis = Bind("module app\nclass Box {}\nclass Box<T> {}\nvar a: Box\nvar b: Box<int>");
        Assert.False(analysis.HasErrors);
        Assert.Equal(0, analysis.GetType(PropertyType(analysis, "app", "a")).Declaration!.Arity);
        Assert.Equal(1, analysis.GetType(PropertyType(analysis, "app", "b")).Declaration!.Arity);
    }

    [Fact]
    public void PartialTypeParametersShareIdentityWhileMethodParametersAreDistinct()
    {
        var analysis = Bind("module app\npartial class Box<T> { val first: T }",
            "module app\npartial class Box<T> { val second: T; func <U> f(value: U): T = first }");
        Assert.False(analysis.HasErrors);
        var references = analysis.Declarations.Files.SelectMany(f => f.Nodes).OfType<AstType>().Where(t => t.Origin.Text == "T").ToArray();
        Assert.True(references.Length >= 3);
        var type = analysis.GetType(references[0]);
        Assert.Equal(ResolvedTypeKind.Parameter, type.Kind);
        Assert.All(references, reference => Assert.Same(type, analysis.GetType(reference)));
        var method = analysis.Declarations.Files.SelectMany(f => f.Nodes).OfType<AstType>().First(t => t.Origin.Text == "U");
        Assert.NotSame(type, analysis.GetType(method));
    }

    [Fact]
    public void CompanionDoesNotCaptureClassTypeParameters()
    {
        var analysis = Bind("module app\nclass Box<T> { companion object { func f(value: T) {} } }");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2112");
    }

    [Theory]
    [InlineData("var value: .Item")]
    [InlineData("class C { constructor(): {} }")]
    public void MalformedNamesDoNotReachStrictLookupApis(string declaration)
    {
        var declarations = AstTestSources.Session("module app\n" + declaration);
        var analysis = SourceAnalysis.Create(declarations, TestContext.Current.CancellationToken);
        Assert.True(analysis.HasErrors);
    }

    [Theory]
    [InlineData("class int {}")]
    [InlineData("func byte() {}")]
    [InlineData("val bool = true")]
    [InlineData("func f(int:byte) {}")]
    [InlineData("class C<int> {}")]
    public void PrimitiveTypeNamesCannotBeRedeclared(string declaration)
    {
        var analysis = Bind("module app\n" + declaration);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2116");
    }

    [Theory]
    [InlineData("[]Borrow")]
    [InlineData("[](Borrow?)")]
    [InlineData("[](int,Borrow)")]
    [InlineData("Box<Borrow>")]
    public void HeapArraysAndUngatedGenericArgumentsRejectRefLikeTypes(string spelling)
    {
        var analysis = Bind("module app\nref struct Borrow {}\nclass Box<T> {}\nvar value: " + spelling);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2115");
    }

    [Fact]
    public void ExplicitRefLikeGateAndInlineContainersPreserveRefLikeIdentity()
    {
        var analysis = Bind("module app\nref struct Borrow {}\nref struct Box<T> where T: allows ref struct {}\nvar a: Box<Borrow>\nvar b: [2]Borrow\nvar c: Borrow?");
        Assert.False(analysis.HasErrors);
        foreach (string name in new[] { "a", "b", "c" })
        {
            Assert.True(analysis.GetType(PropertyType(analysis, "app", name)).IsRefLike);
        }
    }

    [Fact]
    public void NestedNominalTypesResolveThroughTheirOwner()
    {
        var analysis = Bind("module app\nclass Outer { class Inner {} val own: Inner }\nvar other: Outer.Inner");
        Assert.False(analysis.HasErrors);
        var properties = analysis.Declarations.Files.SelectMany(f => f.Nodes).OfType<AstProperty>().ToArray();
        Assert.Same(analysis.GetType(properties[0].Type!), analysis.GetType(properties[1].Type!));
    }

    [Theory]
    [InlineData("Missing", "SOL2112")]
    [InlineData("int<byte>", "SOL2112")]
    [InlineData("Box<int, byte>", "SOL2112")]
    [InlineData("[2147483648]int", "SOL2111")]
    [InlineData("ubyte", "SOL2112")]
    public void InvalidReferencesAreErrorTypesWithSourceDiagnostics(string spelling, string code)
    {
        var analysis = Bind("module app\nclass Box<T> {}\nvar bad: " + spelling);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.True(analysis.GetType(PropertyType(analysis, "app", "bad")).IsError);
    }

    [Fact]
    public void FixedArrayRadicesResolveToTheSameLengthIdentity()
    {
        var analysis = Bind("module app\nvar a: [16]int\nvar b: [0x10]int\nvar c: [0b10000]int\nvar d: [0o20]int");
        Assert.False(analysis.HasErrors);
        var type = analysis.GetType(PropertyType(analysis, "app", "a"));
        foreach (string name in new[] { "b", "c", "d" })
        {
            Assert.Same(type, analysis.GetType(PropertyType(analysis, "app", name)));
        }
    }

    [Fact]
    public void FunctionModesAndEffectsParticipateInIdentity()
    {
        var analysis = Bind("module app\nclass E {}\nvar a: (int)->() throws E\nvar b: (ref int)->() throws E\nvar c: (int)->() throws E,E");
        Assert.False(analysis.HasErrors);
        var a = analysis.GetType(PropertyType(analysis, "app", "a"));
        Assert.NotSame(a, analysis.GetType(PropertyType(analysis, "app", "b")));
        Assert.Same(a, analysis.GetType(PropertyType(analysis, "app", "c")));
        Assert.Single(a.Effects);
    }

    [Fact]
    public void ImportedNominalAmbiguityIsNotChosenBySourceOrder()
    {
        var analysis = Bind("module app\nimport left\nimport right\nvar value: Item",
            "module left\nclass Item {}", "module right\nclass Item {}");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2002");
    }

    [Fact]
    public void NestedMissingTypeIsDiagnosedAtItsOwnOriginWithoutCascadingParents()
    {
        var analysis = Bind("module app\nclass Box<T> {}\nvar value: []Box<Missing>");
        var diagnostic = Assert.Single(analysis.Diagnostics, item => item.Code == "SOL2112");
        Assert.Equal("Missing", diagnostic.Source.GetText(diagnostic.Span));
        Assert.True(analysis.GetType(PropertyType(analysis, "app", "value")).IsError);
    }

    [Fact]
    public void AliasedParameterTypesCannotHideADuplicateSignature()
    {
        var analysis = Bind("module app\nimport library : Alias = Item\nfunc f(a: Alias) {}\nfunc f(b: library.Item) {}",
            "module library\nclass Item {}");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2003");
    }

    [Theory]
    [InlineData("class C: int {}")]
    [InlineData("class Base {}\nclass C: Base {}")]
    [InlineData("class Base {}\ninterface C: Base {}")]
    [InlineData("open class Base {}\nstruct C: Base {}")]
    [InlineData("interface I {}\nclass C: I? {}")]
    [InlineData("open class A {}\nopen class B {}\nclass C: A, B {}")]
    [InlineData("open class A {}\nclass C: A by instance {}")]
    public void InvalidBaseKindsOrFinalClassesAreDiagnosed(string declarations)
    {
        var analysis = Bind("module app\n" + declarations);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2113");
    }

    [Fact]
    public void InterfaceAndOpenClassBasesAreAccepted()
    {
        var analysis = Bind("module app\ninterface I {}\nopen class Base {}\nclass C: Base, I {}\nstruct S: I {}");
        Assert.False(analysis.HasErrors);
    }

    [Fact]
    public void InheritanceCyclesAreRejectedEvenAcrossSourceModules()
    {
        var analysis = Bind("module a\nimport b\nopen class A: B {}", "module b\nimport a\nopen class B: A {}");
        Assert.Equal(2, analysis.Diagnostics.Count(d => d.Code == "SOL2114"));
    }

    [Fact]
    public void CrossAnalysisTypeIdentitiesCannotBeReused()
    {
        var first = Bind("module app\nvar a: int");
        var second = Bind("module app\nvar a: int");
        Assert.Throws<ArgumentException>(() => first.ValidateOwnership(second.GetType(PropertyType(second, "app", "a"))));
        Assert.Throws<ArgumentException>(() => first.GetType(PropertyType(second, "app", "a")));
        Assert.Throws<OperationCanceledException>(() => TypeAnalysis.Create(first.Declarations, new CancellationToken(true)));
    }

    [Fact]
    public async Task FrozenTypeTablesSupportConcurrentQueries()
    {
        var analysis = Bind("module app\nvar a: []int?");
        var reference = PropertyType(analysis, "app", "a");
        var expected = analysis.GetType(reference);
        var values = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => analysis.GetType(reference),
            TestContext.Current.CancellationToken)));
        Assert.All(values, value => Assert.Same(expected, value));
    }
}
