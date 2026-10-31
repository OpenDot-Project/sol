// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalReturnInferenceTests
{
    private const string Choices = "class Widget: Object {}\n"
        + "func choose(value: Object): int = 1\n"
        + "func choose(value: Widget): String = \"closed\"\n";

    [Fact]
    public void LocalOwnTypeArgumentChangesItsInferredResult()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("func <U: Object> local(item: U) = choose(item)\n"
            + "local<Widget>(value)", Choices, result: "String", type: "Widget");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal("std.core.String", local.Binding.ReturnType!.Name);
        Assert.Same(local.Binding.ReturnType, local.SpecializedBody!.Summary.ReturnType);
        Assert.Equal("std.core.String", Assert.Single(local.SpecializedBody.Calls).ReturnType!.Name);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
    }

    [Fact]
    public void ClosedLocalResultParticipatesInEnclosingOverloadSelection()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U: Object> local(item: U) = choose(item)\n"
            + "consume(local<Widget>(value))", Choices
            + "func consume(value: int): int = value\n"
            + "func consume(value: String): String = value\n", result: "String", type: "Widget");
        GenericLexicalTestSource.Bound(root);
        var body = root.SpecializedBody!;
        var consume = Assert.Single(body.Calls, call => call.Target.Name == "consume");
        Assert.Equal("std.core.String", Assert.Single(consume.ParameterTypes).Name);
        Assert.Equal("std.core.String", consume.ReturnType!.Name);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Same(consume.ReturnType, local.Binding.ReturnType);
    }

    [Fact]
    public void CapturedClosedLocalCanDetermineTheInferredReturn()
    {
        var (_, root) = GenericLexicalTestSource.Member("val captured = value\n"
            + "func <U> local(item: U) = captured\nlocal<int>(1)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal("int", local.Binding.ReturnType!.Name);
        Assert.Equal(BodyBindingStatus.Bound, local.SpecializedBody!.Summary.Status);
        Assert.All(GenericLexicalTestSource.Reads(local, "captured"), read =>
            Assert.Same(local.Binding.ReturnType, read.Type));
    }

    [Fact]
    public void AParameterlessLocalCanInferFromTheClosedOwner()
    {
        var (_, root) = GenericLexicalTestSource.Member("func local() = value\nlocal()");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Empty(local.Binding.TypeArguments);
        Assert.Equal("int", local.Binding.ReturnType!.Name);
        Assert.Equal("int", Assert.Single(GenericLexicalTestSource.Reads(local, "value")).Type!.Name);
    }

    [Fact]
    public void AnnotatedRecursionDoesNotNeedCircularReturnInference()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(item: U): T = local<U>(item)\n"
            + "local<int>(1)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal("int", local.Binding.ReturnType!.Name);
        Assert.True(Assert.Single(local.Dependencies).IsRecursiveReference);
    }

    [Fact]
    public void UnannotatedRecursiveInferenceNeverAssumesItsOwnResult()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(item: U) = local<U>(item)\n"
            + "local<T>(value)");
        Assert.NotEqual(BodyBindingStatus.Bound, root.Status);
        Assert.Contains(root.SpecializedBody!.UnverifiedNodes, node =>
            node.Origin.Span.Start >= root.SpecializedBody.Declaration.Origin.Span.Start);
    }

    [Fact]
    public void AnUnassignedCaptureDoesNotPublishAnInferredSuccessfulResult()
    {
        var (_, root) = GenericLexicalTestSource.Member("var captured: T\n"
            + "func <U> local(item: U) = captured\nlocal<int>(1)");
        Assert.NotEqual(BodyBindingStatus.Bound, root.Status);
        Assert.NotEmpty(root.SpecializedBody!.UnverifiedNodes);
        Assert.DoesNotContain(root.Dependencies, proof => proof.Status == BodyBindingStatus.Bound);
    }
}
