// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class PsiTests
{
    [Fact]
    public void CompleteModelFixtureProjectsIntoTypedSourceViews()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "declarations.sol");
        var source = SourceText.FromBytes(File.ReadAllBytes(path), path);
        var tree = SyntaxTree.Parse(source, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(tree.Diagnostics);
        var snapshot = PsiSnapshot.Create(tree, TestContext.Current.CancellationToken);
        Assert.Equal("fixtures.models", snapshot.Root.ModuleName);
        var users = snapshot.Root.Declarations.OfType<PsiTypeDeclaration>().Where(t => t.Name == "User").ToArray();
        Assert.Equal(2, users.Length);
        Assert.All(users, user => Assert.True(user.IsPartial));
        Assert.Equal("create", users[0].Members.OfType<PsiTypeDeclaration>().Single().Members.OfType<PsiFunction>().Single().Name);
        Assert.Contains(snapshot.Root.Declarations.OfType<PsiTypeDeclaration>(), t => t.IsRefLike && t.Name == "Window");
        Assert.Equal("main", snapshot.Root.Declarations.OfType<PsiTypeDeclaration>().Single(t => t.Name == "Main").Members.Single().Name);
    }

    [Fact]
    public void TypeMembersExposeSourceAnnotationsModifiersAndGenericShape()
    {
        var snapshot = Parse("@A(1)\npartial ref struct Box<T: Named> where T: allows ref struct {\n"
            + "private var value: T\nfunc get(): T = this.value\n}");
        var type = Assert.IsType<PsiTypeDeclaration>(Assert.Single(snapshot.Root.Declarations));
        Assert.Equal("Box", type.Name);
        Assert.True(type.IsPartial);
        Assert.True(type.IsRefLike);
        Assert.Equal("partial", Assert.Single(type.Modifiers));
        Assert.Equal("A", Assert.Single(type.Annotations).Name);
        Assert.Equal("T", Assert.Single(type.TypeParameters).Name);
        Assert.Equal("Named", Assert.Single(type.TypeParameters).Constraint!.Text);
        Assert.Equal(1, type.Arity);
        Assert.Equal(new[] { "value", "get" }, type.Members.Select(m => m.Name));
        Assert.Equal("T", type.Members.OfType<PsiFunction>().Single().ReturnType!.Text);
        Assert.Equal(NodeKind.MemberExpression, type.Members.OfType<PsiFunction>().Single().Body!.Kind);
    }

    [Fact]
    public void PropertyAccessorsAndDelegatesAreReadableWithoutBinding()
    {
        var snapshot = Parse("class C { var name: String by Delegate()\nval full: String { get throws Failure = this.name } }");
        var type = snapshot.Root.Declarations.OfType<PsiTypeDeclaration>().Single();
        var fields = type.Members.OfType<PsiProperty>().ToArray();
        Assert.True(fields[0].IsMutable);
        Assert.Equal("Delegate()", fields[0].DelegateExpression!.Text);
        Assert.Null(fields[0].Initializer);
        Assert.False(fields[1].IsMutable);
        var getter = Assert.Single(fields[1].Accessors);
        Assert.Equal("get", getter.Name);
        Assert.Equal("this.name", getter.Body!.Text);
        Assert.Equal("Failure", Assert.Single(getter.ThrownTypes).Text);
        Assert.Equal("String", fields[1].ValueType!.Text);
    }

    [Fact]
    public void InitializersAndExternSignaturesAreTypedDeclarations()
    {
        var snapshot = Parse("class C { init { prepare() } }\nextern \"c\" { func close(handle: ulong): int; }");
        var type = snapshot.Root.Declarations.OfType<PsiTypeDeclaration>().Single();
        Assert.Equal(NodeKind.Block, Assert.Single(type.Initializers).Body!.Kind);
        var external = snapshot.Root.Declarations.OfType<PsiExtern>().Single();
        Assert.Equal("close", Assert.Single(external.Functions).Name);
        Assert.Null(Assert.Single(external.Functions).Body);
    }

    [Fact]
    public void ConstructorsIndexersAndEnumPayloadsHaveTypedViews()
    {
        var snapshot = Parse("class C(val name: String) { constructor(): this(\"x\") {}\n"
            + "this[index: int]: String { get = this.name } }\nenum E { Empty, Value(value: int), }");
        var types = snapshot.Root.Declarations.OfType<PsiTypeDeclaration>().ToArray();
        var primary = types[0].PrimaryConstructor!;
        Assert.True(Assert.Single(primary.ParameterGroups).DeclaresProperty);
        var constructor = types[0].Members.OfType<PsiConstructor>().Single();
        Assert.Equal("this(\"x\")", constructor.Delegation!.Text);
        Assert.Equal(NodeKind.Block, constructor.Body!.Kind);
        var indexer = types[0].Members.OfType<PsiIndexer>().Single();
        Assert.Equal("index", Assert.Single(Assert.Single(indexer.ParameterGroups).Names).Text);
        Assert.Equal("String", indexer.ValueType!.Text);
        Assert.Equal("get", Assert.Single(indexer.Accessors).Name);
        var variants = types[1].Members.OfType<PsiEnumVariant>().ToArray();
        Assert.Equal(new[] { "Empty", "Value" }, variants.Select(v => v.Name));
        Assert.Equal("int", Assert.Single(variants[1].ParameterGroups).ParameterType!.Text);
    }

    [Fact]
    public void ParameterGroupsKeepSharedTypePassingModeAndDefaults()
    {
        var snapshot = Parse("func f(ref a, b: int, count: int = 3): int throws Failure = count");
        var function = snapshot.Root.Declarations.OfType<PsiFunction>().Single();
        var groups = function.ParameterGroups.ToArray();
        Assert.Equal(new[] { "a", "b" }, groups[0].Names.Select(n => n.Text));
        Assert.Equal(ParameterPassingMode.Ref, groups[0].PassingMode);
        Assert.Equal("int", groups[0].ParameterType!.Text);
        Assert.Equal("3", groups[1].DefaultValue!.Text);
        Assert.Equal("Failure", Assert.Single(function.ThrownTypes).Text);
    }

    [Fact]
    public void WorkspaceGroupsModulesByDeclarationRatherThanSourcePath()
    {
        var first = SyntaxTree.Parse(SourceText.FromString("module shared.identity\npartial class C {}", "unrelated/one.sol"),
            cancellationToken: TestContext.Current.CancellationToken);
        var second = SyntaxTree.Parse(SourceText.FromString("module shared.identity\npartial class C {}", "different/two.sol"),
            cancellationToken: TestContext.Current.CancellationToken);
        var workspace = PsiWorkspace.Create([first, second], TestContext.Current.CancellationToken);
        Assert.Equal(2, workspace.Modules["shared.identity"].Length);
        Assert.All(workspace.Modules["shared.identity"], file => Assert.True(file.Declarations.Single().IsPartial));
        Assert.False(workspace.Modules.ContainsKey("Shared.Identity"));
    }

    [Fact]
    public void ForeignSnapshotNodesAreRejectedAndOriginalViewsRemainValid()
    {
        var first = Parse("class C {}");
        var second = Parse("class C {}");
        Assert.NotSame(first.Root, second.Root);
        Assert.Same(first.Root, first.GetElement(first.Syntax.Root));
        Assert.Throws<ArgumentException>(() => second.GetElement(first.Syntax.Root));
        Assert.Equal("C", first.Root.Declarations.Single().Name);
    }

    [Fact]
    public void ParentIdentityAndByteLookupReferToTheSameWrappers()
    {
        var snapshot = Parse("class C { val word = \"é\" }");
        foreach (var element in snapshot.Root.Descendants())
        {
            Assert.Same(snapshot, element.Snapshot);
            Assert.Same(element, snapshot.GetElement(element.Syntax));
            Assert.Contains(element, element.Parent!.Children);
            Assert.Contains(snapshot.Root, element.Ancestors());
        }
        var name = snapshot.Root.Descendants().OfType<PsiName>().First(n => n.Text == "word");
        Assert.Same(name, snapshot.FindElement(name.Span.Start));
        Assert.Same(snapshot.Root, snapshot.FindElement(snapshot.Source.Length));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.FindElement(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.FindElement(snapshot.Source.Length + 1));
    }

    [Fact]
    public void ArraySuffixBindsToElementAndOuterNullabilityRequiresGrouping()
    {
        var snapshot = Parse("var elements: []String?\nvar grouped: [](String?)\nvar array: ([]String)?\nvar both: ([]String?)?\nvar nested: [][]String?\nvar fixedElements: [4]String?");
        var types = snapshot.Root.Declarations.OfType<PsiProperty>().Select(p => p.ValueType!).ToArray();
        Assert.Equal("[]String?", types[0].Text);
        Assert.Equal("String?", Assert.Single(types[0].Children.OfType<PsiTypeReference>()).Text);
        Assert.Equal("(String?)", Assert.Single(types[1].Children.OfType<PsiTypeReference>()).Text);
        Assert.Equal("([]String)?", types[2].Text);
        Assert.Equal("[]String", Assert.Single(types[2].Children.OfType<PsiTypeReference>()).Text);
        Assert.Equal("([]String?)?", types[3].Text);
        Assert.Equal("[]String?", Assert.Single(types[3].Children.OfType<PsiTypeReference>()).Text);
        var nestedElement = Assert.Single(types[4].Children.OfType<PsiTypeReference>());
        Assert.Equal("String?", Assert.Single(nestedElement.Children.OfType<PsiTypeReference>()).Text);
        Assert.Equal("String?", Assert.Single(types[5].Children.OfType<PsiTypeReference>()).Text);
    }

    [Fact]
    public void TokensCanStraddleAContextuallySplitGenericCloser()
    {
        var snapshot = Parse("val value: Box<Box<int>>=other");
        var inner = snapshot.Root.Descendants().OfType<PsiTypeReference>().Single(t => t.Text == "Box<int>");
        Assert.Contains(inner.Tokens, token => token.Kind == SyntaxKind.GreaterGreaterEqualsToken && token.Span.End > inner.Span.End);
        Assert.Equal("int", Assert.Single(inner.TypeArguments).Text);
    }

    [Fact]
    public void FileAnnotationsAndImportAliasesAreSourceViews()
    {
        var tree = SyntaxTree.Parse(SourceText.FromString("@file:Allow(\"style\")\nmodule app\nimport fmt = std.fmt\n"),
            cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = PsiSnapshot.Create(tree, TestContext.Current.CancellationToken);
        Assert.Equal("file", Assert.Single(snapshot.Root.FileAnnotations).Target);
        Assert.Equal("Allow", Assert.Single(snapshot.Root.FileAnnotations).Name);
        var import = Assert.Single(snapshot.Root.Imports);
        Assert.Equal("fmt", import.Alias);
        Assert.Equal("std.fmt", import.ModuleName);
    }

    [Fact]
    public void RecoveryTreesRemainInspectableAndCancellationIsObserved()
    {
        var tree = SyntaxTree.Parse(SourceText.FromString("module app\nclass C { val p: }"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(tree.HasErrors);
        var snapshot = PsiSnapshot.Create(tree, TestContext.Current.CancellationToken);
        Assert.Equal("C", snapshot.Root.Declarations.Single().Name);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PsiSnapshot.Create(tree, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => PsiWorkspace.Create([], cancellation.Token));
    }

    [Fact]
    public void MalformedByteSnapshotsRemainInspectableWithoutRewritingTheSource()
    {
        var random = new Random(20261020);
        for (int length = 0; length < 256; length += 3)
        {
            byte[] bytes = new byte[length];
            random.NextBytes(bytes);
            var source = SourceText.FromBytes(bytes);
            var tree = SyntaxTree.Parse(source, 12, cancellationToken: TestContext.Current.CancellationToken);
            var snapshot = PsiSnapshot.Create(tree, TestContext.Current.CancellationToken);
            Assert.Equal(bytes, snapshot.Source.Bytes.ToArray());
            Assert.All(snapshot.Root.Descendants(), element => Assert.Same(element, snapshot.GetElement(element.Syntax)));
        }
    }

    [Fact]
    public void ConstructionRejectsDagNodesAndInvalidRootRanges()
    {
        var source = SourceText.FromString("");
        var leaf = new SyntaxNode(NodeKind.Name, new(0, 0), []);
        var root = new SyntaxNode(NodeKind.CompilationUnit, new(0, 0), [leaf, leaf]);
        var tree = new SyntaxTree(source, root, [], [], null);
        Assert.Throws<ArgumentException>(() => PsiSnapshot.Create(tree, TestContext.Current.CancellationToken));
        var nonempty = SourceText.FromString("x");
        Assert.Throws<ArgumentException>(() => PsiSnapshot.Create(new(nonempty, root, [], [], null), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DeepExternalTreesAreBuiltIterativelyAndViewsSupportConcurrentReads()
    {
        var node = new SyntaxNode(NodeKind.Name, new(0, 0), []);
        for (int index = 0; index < 5000; index++) { node = new(NodeKind.Type, new(0, 0), [node]); }
        var root = new SyntaxNode(NodeKind.CompilationUnit, new(0, 0), [node]);
        var snapshot = PsiSnapshot.Create(new(SourceText.FromString(""), root, [], [], null), TestContext.Current.CancellationToken);
        Assert.Equal(5001, snapshot.Root.Descendants().Count());
        Parallel.For(0, 32, new ParallelOptions { CancellationToken = TestContext.Current.CancellationToken },
            _ => Assert.Same(snapshot.Root, snapshot.GetElement(root)));
    }

    private static PsiSnapshot Parse(string text)
    {
        var tree = SyntaxTree.Parse(SourceText.FromString("module psi.example\n" + text),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(tree.Diagnostics);
        return PsiSnapshot.Create(tree, TestContext.Current.CancellationToken);
    }
}
