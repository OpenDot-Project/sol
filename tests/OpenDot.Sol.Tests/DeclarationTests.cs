// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Driver;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class DeclarationTests
{
    [Theory]
    [InlineData("class Empty")]
    [InlineData("open class Widget(val id: ulong) { open func render(): String = \"widget\" }")]
    [InlineData("class Button(id: ulong, var caption: String): Widget(id), Named { override func name(): String = this.caption }")]
    [InlineData("class Session private constructor(val name: String) { companion object { func create(name: String): Session = Session(name) } }")]
    [InlineData("class C { constructor(x: int): this(x, 0) {}\nconstructor(x, y: int): super() {}\ninit { prepare() } }")]
    [InlineData("interface Named { func name(): String\nfunc other() }")]
    [InlineData("sealed interface Shape: Named { func area(): double }")]
    [InlineData("readonly struct Point { val x: int\nconstructor(x: int) { this.x = x } }")]
    [InlineData("partial ref struct Span<T> { private val owner: []T?\nval length: int }")]
    [InlineData("partial class C {}\npartial struct S {}\npartial object O {}")]
    [InlineData("class C { class Nested<T> {}\ncompanion object Factory {} }")]
    [InlineData("object Reader: Source by actual { func read(): int = 1 }")]
    [InlineData("class Tagged(private val actual: Logger): Logger by actual { override func log(text: String) {} }")]
    [InlineData("class C { var name: String by Delegate(\"initial\") }")]
    [InlineData("class C { var value: int { get; private set; } = 0 }")]
    [InlineData("class C { val name: String { get = this.fullName } }")]
    [InlineData("class C { var value: int { get { field }\nset = field = value\ninit = initialize(value) } = 0 }")]
    [InlineData("class C { val text: String { get throws IOException = read() } }")]
    [InlineData("class C { ref val value: int { get { return ref location } } }")]
    [InlineData("ref val p: int")]
    [InlineData("class C { this[index: int]: String { get = values[index]\nset = values[index] = value } }")]
    [InlineData("ref struct S { ref readonly this[index: int]: int { get { return ref values[index] } } }")]
    [InlineData("struct V { companion object { operator func +(left, right: V): V = left\noperator func checked +(left, right: V): V = right } }")]
    [InlineData("class C { companion object { operator func true(value: C): bool = true\noperator func false(value: C): bool = false\noperator func implicit(value: C): int = 0 } }")]
    [InlineData("class D { operator func getValue(owner: Object?, property: SolProperty<int>): int = 1 }")]
    [InlineData("enum Command { Stop, Move(x: int, y: int), Rename(value: String), }")]
    [InlineData("enum Result<T> { Value(value: T), Empty, }")]
    [InlineData("annotation class Route(val path: String)")]
    [InlineData("@Route(\"/\")\nclass Page(@A val name: String) { @test func test() {} }")]
    [InlineData("class C { @get:A val value: int { get = 1 } }")]
    [InlineData("@type:A class C { @constructor:B constructor() {} }")]
    [InlineData("func <T> identity(value: T): T = value")]
    [InlineData("func checked(readonly, other: int): int = readonly + other")]
    [InlineData("func <T: Comparable<T>> maximum(left, right: T): T where T: Named throws Failure { left }")]
    [InlineData("ref struct View<T> where T: allows ref struct { val value: T }")]
    [InlineData("class Box<T: Hashable, U> where U: Ordering, U: Named {}")]
    [InlineData("val nested: HashMap<String, Vector<Box<int>>>")]
    [InlineData("val nested: Box<Box<int>>=value")]
    [InlineData("val nested: Box<int>=value")]
    [InlineData("var nested: Box<Box<int>>\nval after = 1")]
    [InlineData("var callback: ((int) -> int throws Failure)?\nval after = 1")]
    [InlineData("var elements: []String?\nvar grouped: [](String?)\nvar array: ([]String)?\nvar both: ([]String?)?\nvar repeated: String??")]
    [InlineData("func f(): ref readonly int { return ref location }")]
    [InlineData("val result = identity<Box<int>>(value)\nval zero = default<Box<Box<int>>>()")]
    [InlineData("class C: I { override func f() { super<I>.f() } }")]
    [InlineData("extern \"c\" { @link(\"kernel32\") func close(handle: ulong): int; }")]
    public void DeclarationGrammarAcceptsSupportedForms(string text)
    {
        var tree = Parse(text);
        Assert.Empty(tree.Diagnostics);
        ValidateTree(tree);
    }

    [Fact]
    public void FileAnnotationsPrecedeAnExplicitPathIndependentModule()
    {
        var tree = SyntaxTree.Parse(SourceText.FromString("@file:Allow(\"style\")\nmodule unrelated.name\nclass C {}", "elsewhere.sol"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(tree.Diagnostics);
        Assert.Equal("unrelated.name", tree.ModuleName);
        Assert.Equal(NodeKind.FileAnnotation, tree.Root.Children[0].Kind);
        Assert.Equal(NodeKind.ModuleDeclaration, tree.Root.Children[1].Kind);
        ValidateTree(tree);
    }

    [Theory]
    [InlineData("class")]
    [InlineData("constructor() {}")]
    [InlineData("init {}")]
    [InlineData("this[index: int]: int { get = value }")]
    [InlineData("struct S")]
    [InlineData("interface I")]
    [InlineData("object")]
    [InlineData("enum Empty {}")]
    [InlineData("enum E { A B }")]
    [InlineData("class Box<> {}")]
    [InlineData("class Box<T: > {}")]
    [InlineData("class C where T allows ref struct {}")]
    [InlineData("class C where T: allows ref struct\n{}")]
    [InlineData("class C: {}")]
    [InlineData("class C public (x: int) {}")]
    [InlineData("class C { constructor(): other() {} }")]
    [InlineData("class C { init work() }")]
    [InlineData("class C { val p: int {} }")]
    [InlineData("class C { val p: int { unknown; } }")]
    [InlineData("class C { val p: int { get = field = 1 } }")]
    [InlineData("class C { var p: int { get; } by value }")]
    [InlineData("class C { this[index: int]: int }")]
    [InlineData("val x: Box<> = value")]
    [InlineData("val x: Box<int = value")]
    [InlineData("func <T f(value: T) {}")]
    [InlineData("annotation A")]
    [InlineData("extern \"cpp\" { func f(); }")]
    [InlineData("extern \"c\" { class C {} }")]
    [InlineData("extern \"c\" { func f() {} }")]
    public void BrokenDeclarationsReportBoundedErrors(string text)
    {
        var tree = Parse(text, 12);
        Assert.True(tree.HasErrors);
        Assert.InRange(tree.Diagnostics.Length, 1, 12);
        ValidateTree(tree);
    }

    [Fact]
    public void GenericClosingSplitsOnlyTheParserView()
    {
        var tree = Parse("val value: Box<Box<int>>=source\nval shifted = left >> right");
        Assert.Empty(tree.Diagnostics);
        Assert.Contains(tree.Tokens, t => t.Kind == SyntaxKind.GreaterGreaterEqualsToken);
        Assert.Contains(tree.Tokens, t => t.Kind == SyntaxKind.GreaterGreaterToken);
        var types = Descendants(tree.Root).Where(n => n.Kind == NodeKind.Type).ToArray();
        Assert.Contains(types, type => tree.Source.GetText(type.Span) == "Box<int>");
        Assert.Contains(types, type => tree.Source.GetText(type.Span) == "Box<Box<int>>");
        ValidateTree(tree);
    }

    [Fact]
    public void PropertyAccessorAndDelegateStructureRemainDistinct()
    {
        var tree = Parse("class C { var name: String by Delegate()\nval full: String { get = this.fullName } }");
        Assert.Empty(tree.Diagnostics);
        var property = Descendants(tree.Root).Single(n => n.Kind == NodeKind.PropertyDeclaration);
        Assert.Equal(NodeKind.AccessorList, property.Children[^1].Kind);
        Assert.Single(Descendants(tree.Root), n => n.Kind == NodeKind.Delegation);
        Assert.Equal("get", tree.Source.GetText(Descendants(property).Single(n => n.Kind == NodeKind.AccessorDeclaration).Children[0].Span));
    }

    [Fact]
    public void DeepTypeNestingAndNominalNestingRespectBudgets()
    {
        foreach (string text in new[]
        {
            "val value: " + string.Concat(Enumerable.Repeat("Box<", 400)) + "int" + new string('>', 400),
            string.Concat(Enumerable.Repeat("class C { ", 400)) + new string('}', 400),
        })
        {
            var tree = Parse(text, 12);
            Assert.Contains(tree.Diagnostics, d => d.Code == "SOL1104");
            ValidateTree(tree);
        }
    }

    [Fact]
    public void DeclarationTokenDeletionMutationsPreserveSourceAndTerminate()
    {
        const string text = "class Box<T: Named> where T: Hashable { @A var value: Box<Box<int>> { get = source\nset = field = value }\n"
            + "constructor(): this(0) {}\nfunc <U> convert(value: U): T throws Failure = source }\nenum E { A, B(x: int), }";
        var source = SourceText.FromString(text);
        foreach (var token in Lexer.Lex(source, cancellationToken: TestContext.Current.CancellationToken).Tokens.Where(t => t.Span.Length > 0))
        {
            var tree = Parse(text.Remove(token.Span.Start, token.Span.Length), 12);
            Assert.InRange(tree.Diagnostics.Length, 0, 12);
            ValidateTree(tree);
        }
    }

    private static SyntaxTree Parse(string text, int limit = 100) => SyntaxTree.Parse(
        SourceText.FromString("module declarations\n" + text), limit, cancellationToken: TestContext.Current.CancellationToken);

    private static IEnumerable<SyntaxNode> Descendants(SyntaxNode root)
    {
        var stack = new Stack<SyntaxNode>();
        stack.Push(root);
        while (stack.TryPop(out var node))
        {
            yield return node;
            foreach (var child in node.Children) { stack.Push(child); }
        }
    }

    private static void ValidateTree(SyntaxTree tree)
    {
        foreach (var node in Descendants(tree.Root))
        {
            tree.Source.Validate(node.Span);
            Assert.All(node.Children, child => Assert.True(child.Span.Start >= node.Span.Start && child.Span.End <= node.Span.End));
        }
        int cursor = 0;
        foreach (var token in tree.Tokens)
        {
            Assert.Equal(cursor, token.FullSpan.Start);
            cursor = token.Span.End;
        }
        Assert.Equal(tree.Source.Length, cursor);
        CstWriter.Serialize(tree);
        PsiSnapshot.Create(tree, TestContext.Current.CancellationToken);
    }
}
