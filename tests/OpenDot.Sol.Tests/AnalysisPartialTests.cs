// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnalysisPartialTests
{
    [Theory]
    [InlineData("class")]
    [InlineData("struct")]
    [InlineData("ref struct")]
    [InlineData("object")]
    public void SupportedPartialTypesMergeByModuleAndName(string kind)
    {
        var session = AstTestSources.Session(
            "module app\npartial " + kind + " C { func first() {} }",
            "module app\npartial " + kind + " C { func second() {} }");
        Assert.False(session.HasErrors);
        var symbol = Assert.Single(session.Modules["app"].Declarations);
        Assert.True(symbol.IsPartial);
        Assert.Equal(2, symbol.Parts.Length);
        Assert.Equal(new[] { "first", "second" }, symbol.Members.Select(m => m.Name));
        Assert.Same(symbol, session.GetDeclaredSymbol(symbol.Parts[0]));
        Assert.Same(symbol, session.GetDeclaredSymbol(symbol.Parts[1]));
    }

    [Fact]
    public void SameTypeNameInDifferentModulesDoesNotMerge()
    {
        var session = AstTestSources.Session("module a\npartial class C {}", "module b\npartial class C {}");
        Assert.False(session.HasErrors);
        var a = Assert.Single(session.Modules["a"].Declarations);
        var b = Assert.Single(session.Modules["b"].Declarations);
        Assert.NotSame(a, b);
        Assert.Single(a.Parts);
        Assert.Single(b.Parts);
    }

    [Fact]
    public void GenericArityIsPartOfNominalIdentity()
    {
        var session = AstTestSources.Session("module a\nclass Box {}\nclass Box<T> {}");
        Assert.False(session.HasErrors);
        Assert.Equal(new[] { 0, 1 }, session.Modules["a"].Declarations.Select(d => d.Arity));
    }

    [Theory]
    [InlineData("class C {}", "class C {}")]
    [InlineData("partial class C {}", "class C {}")]
    [InlineData("partial class C {}", "partial struct C {}")]
    [InlineData("public partial class C {}", "private partial class C {}")]
    [InlineData("open partial class C {}", "partial class C {}")]
    [InlineData("partial class C<T> {}", "partial class C<U> {}")]
    [InlineData("partial class C<T: A> {}", "partial class C<T: B> {}")]
    [InlineData("partial class C<T> where T: A {}", "partial class C<T> where T: B {}")]
    [InlineData("partial class C { init {} }", "partial class C { init {} }")]
    [InlineData("partial class C { val a = 1 }", "partial class C { val b = 2 }")]
    [InlineData("partial class C { val a: int by Delegate() }", "partial class C { init {} }")]
    [InlineData("partial class C() {}", "partial class C() {}")]
    public void IncompatiblePartsAreRejected(string first, string second)
    {
        var session = AstTestSources.Session("module app\n" + first, "module app\n" + second);
        Assert.Contains(session.Diagnostics, diagnostic => diagnostic.Code == "SOL2030");
    }

    [Fact]
    public void OnlyOnePartMayOwnInitialization()
    {
        var session = AstTestSources.Session(
            "module app\npartial class C { val a = 1; init {} }",
            "module app\npartial class C { val b: int; func read() = a }");
        Assert.False(session.HasErrors);
        Assert.Equal(3, Assert.Single(session.Modules["app"].Declarations).Members.Length);
    }

    [Fact]
    public void NestedPartialIdentityIncludesItsContainingType()
    {
        var session = AstTestSources.Session(
            "module app\npartial class Outer { partial class Inner { func a() {} } }",
            "module app\npartial class Outer { partial class Inner { func b() {} } }\nclass Other { class Inner {} }");
        Assert.False(session.HasErrors);
        var outer = Assert.Single(session.Modules["app"].Declarations, d => d.Name == "Outer");
        var inner = Assert.Single(outer.Members);
        Assert.Equal(2, inner.Parts.Length);
        Assert.Equal(2, inner.Members.Length);
        Assert.Contains("Outer`0.Inner", inner.QualifiedName);
    }

    [Fact]
    public void MergedMemberLookupPrecedesModuleLookup()
    {
        var session = AstTestSources.Session("module app\nclass Owner { func item() {} }\nfunc item(x: int) {}");
        var owner = Assert.Single(session.Modules["app"].Declarations, d => d.Name == "Owner");
        var lookup = session.Lookup(session.Files[0].Root, "item", owner);
        Assert.Equal(LookupScope.Member, lookup.Scope);
        Assert.Equal("app.Owner`0.item", Assert.Single(lookup.Candidates).QualifiedName);
    }

    [Theory]
    [InlineData("func f(a: int) {}\nfunc f(b: int) {}")]
    [InlineData("func f(a: int): int = a\nfunc f(b: int): String = \"x\"")]
    [InlineData("func f(a: int) throws A {}\nfunc f(b: int) throws B {}")]
    [InlineData("func f(ref a: int) {}\nfunc f(out b: int) {}")]
    [InlineData("func f(a: int = 1) {}\nfunc f(b: int = 2) {}")]
    [InlineData("func <T> f(a: T) {}\nfunc <U> f(b: U) {}")]
    [InlineData("val item: int\nval item: int")]
    [InlineData("class C { val item: int; func item() {} }")]
    [InlineData("enum C { A, A }")]
    public void DuplicateSignaturesAndNameConflictsAreDiagnosed(string declarations)
    {
        var session = AstTestSources.Session("module app\n" + declarations);
        Assert.Contains(session.Diagnostics, diagnostic => diagnostic.Code == "SOL2003");
    }

    [Fact]
    public void ValueAndReferenceParametersMayFormDifferentOverloads()
    {
        var session = AstTestSources.Session("module app\nfunc f(a: int) {}\nfunc f(ref a: int) {}");
        Assert.False(session.HasErrors);
        Assert.Equal(2, session.Modules["app"].Declarations.Length);
    }

    [Theory]
    [InlineData("public private class C {}", "SOL2004")]
    [InlineData("public public class C {}", "SOL2004")]
    [InlineData("final open class C {}", "SOL2004")]
    [InlineData("partial interface C {}", "SOL2030")]
    [InlineData("class C<T,T> {}", "SOL2005")]
    [InlineData("func f(a,a:int) {}", "SOL2005")]
    [InlineData("func f(a,b:int=1) {}", "SOL2005")]
    [InlineData("func f(vararg a,b:int) {}", "SOL2005")]
    [InlineData("func f(vararg a:int,vararg b:int) {}", "SOL2005")]
    [InlineData("func f(ref a:int=1) {}", "SOL2005")]
    [InlineData("class C(val a,b:int) {}", "SOL2005")]
    [InlineData("class C { val x:int { get; get; } }", "SOL2006")]
    [InlineData("class C { val x:int { get; set; } }", "SOL2006")]
    public void DeclarationShapeValidationDoesNotRequireResolvedTypes(string declarations, string code)
    {
        var session = AstTestSources.Session("module app\n" + declarations);
        Assert.Contains(session.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void ConstructorAndIndexerOverloadsAreSeparateMemberSets()
    {
        var session = AstTestSources.Session("module app\nclass C { constructor() {} constructor(x:int) {} this[x:int]:int { get = x } this[x:String]:int { get = 1 } }");
        Assert.False(session.HasErrors);
        var owner = Assert.Single(session.Modules["app"].Declarations);
        Assert.Equal(2, owner.Members.Count(m => m.Kind == SymbolKind.Constructor));
        Assert.Equal(2, owner.Members.Count(m => m.Kind == SymbolKind.Indexer));
    }
}
