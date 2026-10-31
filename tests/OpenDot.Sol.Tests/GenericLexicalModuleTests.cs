// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalModuleTests
{
    private const string Owner = "module model\nclass Root<T> { func run(value: T): T {\n"
        + "func <U> local(item: U): T = value\nlocal<int>(1)\n}\n}";

    [Fact]
    public void ImportedOwnerLocalBodyKeepsTheDefiningModuleAndSource()
    {
        var session = AstTestSources.Session(Owner,
            "module app\nimport model\nfunc use(owner: Root<int>, value: int): int = owner.run(value)",
            GenericMemberProofTestSource.Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(item => item.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Same(local.Binding.Target.Origin.Source, local.SpecializedBody!.Declaration.Origin.Source);
        Assert.Equal("model", root.Binding.Receiver!.Declaration!.ModuleName);
        Assert.NotSame(call.Call.Origin.Source, local.Binding.Target.Origin.Source);
        Assert.Same(sources, local.Binding.LexicalContext!.Sources);
    }

    [Fact]
    public void ImportAliasDoesNotReplaceTheCanonicalEnclosingOwner()
    {
        var session = AstTestSources.Session(Owner,
            "module app\nimport model : Alias = Root\n"
            + "func use(owner: Alias<int>, value: int): int = owner.run(value)",
            GenericMemberProofTestSource.Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(item => item.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal("Root", root.Binding.Receiver!.Declaration!.Name);
        Assert.Equal("int", Assert.Single(local.Binding.LexicalContext!.EnclosingArguments).Argument.Name);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void PartialOwnerAcrossFilesUsesOneClosedLexicalParameter()
    {
        var session = AstTestSources.Session("module model\npartial class Root<T> {}",
            "module model\npartial class Root<T> { func run(value: T): T {\n"
            + "func <U> local(item: U): T = value\nlocal<int>(1)\n}\n}",
            "module app\nimport model\nfunc use(owner: Root<int>, value: int): int = owner.run(value)",
            GenericMemberProofTestSource.Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(item => item.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var owner = root.Binding.Receiver!;
        Assert.Equal(2, owner.Declaration!.Parts.Length);
        var local = GenericLexicalTestSource.Local(root);
        var pair = Assert.Single(local.Binding.LexicalContext!.EnclosingArguments);
        Assert.Same(Assert.Single(sources.Types.GetTypeParameters(owner.Declaration.Parts[0])), pair.Parameter);
        Assert.Same(Assert.Single(owner.Arguments), pair.Argument);
    }

    [Fact]
    public void SameSpelledOwnersInDifferentModulesKeepSeparateLexicalDeclarations()
    {
        var session = AstTestSources.Session(Owner,
            Owner.Replace("module model", "module another", System.StringComparison.Ordinal),
            "module app\nimport left = model\nimport right = another\n"
            + "func first(owner: left.Root<int>, value: int): int = owner.run(value)\n"
            + "func second(owner: right.Root<int>, value: int): int = owner.run(value)",
            GenericMemberProofTestSource.Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(item => item.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var calls = analysis.Calls.Where(call => call.Target.Name == "run" && call.Receiver is { } owner
            && owner.Arguments.All(GenericMemberProofTestSource.IsClosed)).ToArray();
        Assert.Equal(2, calls.Length);
        var roots = calls.Select(call => analysis.GetGenericMemberCallProof(call.Call)!).ToArray();
        Assert.All(roots, GenericLexicalTestSource.Bound);
        var first = GenericLexicalTestSource.Local(roots[0]);
        var second = GenericLexicalTestSource.Local(roots[1]);
        Assert.NotSame(first.Binding.Target, second.Binding.Target);
        Assert.NotSame(first.SpecializedBody, second.SpecializedBody);
        Assert.NotSame(first.Binding.LexicalContext!.EnclosingArguments[0].Parameter,
            second.Binding.LexicalContext!.EnclosingArguments[0].Parameter);
    }
}
