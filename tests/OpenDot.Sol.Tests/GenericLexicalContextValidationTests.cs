// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalContextValidationTests
{
    private static (BodyAnalysis Analysis, GenericMemberCallProof Local) Create()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("val captured = value\n"
            + "func <U> local(item: U): T = captured\nlocal<int>(1)");
        GenericLexicalTestSource.Bound(root);
        return (analysis, GenericLexicalTestSource.Local(root));
    }

    private static SpecializedBodyAnalysis Query(BodyAnalysis analysis, GenericMemberCallProof local,
        LexicalBodyContext context, ImmutableArray<ResolvedType>? arguments = null) =>
        analysis.GetLexicalBody((AstFunction)local.Binding.Target, arguments ?? local.Binding.TypeArguments,
            context, TestContext.Current.CancellationToken);

    [Fact]
    public void AContextFromAnotherAnalysisCannotBeReused()
    {
        var (analysis, local) = Create();
        var (_, foreign) = Create();
        Assert.Throws<ArgumentException>(() => Query(analysis, local, foreign.Binding.LexicalContext!));
        Assert.Equal(BodyBindingStatus.Bound, Query(analysis, local, local.Binding.LexicalContext!).Summary.Status);
    }

    [Fact]
    public void AContextCannotBeRetargetedToAnotherFunction()
    {
        var (analysis, local) = Create();
        var outer = GenericMemberProofTestSource.Function(analysis, "run");
        Assert.Throws<ArgumentException>(() => analysis.GetLexicalBody(outer, [], local.Binding.LexicalContext!,
            TestContext.Current.CancellationToken));
        Assert.Equal(BodyBindingStatus.Bound, local.SpecializedBody!.Summary.Status);
    }

    [Fact]
    public void MissingEnclosingTypeArgumentIsRejected()
    {
        var (analysis, local) = Create();
        var context = local.Binding.LexicalContext!;
        Assert.Single(context.EnclosingArguments);
        Assert.Throws<ArgumentException>(() => Query(analysis, local, context with { EnclosingArguments = [] }));
        Assert.Equal(BodyBindingStatus.Bound, Query(analysis, local, context).Summary.Status);
    }

    [Fact]
    public void DefaultEnclosingVectorProducesAnArgumentError()
    {
        var (analysis, local) = Create();
        var context = local.Binding.LexicalContext!;
        Assert.Throws<ArgumentException>(() => Query(analysis, local,
            context with { EnclosingArguments = default }));
        Assert.Equal(BodyBindingStatus.Bound, local.SpecializedBody!.Summary.Status);
    }

    [Fact]
    public void DefaultLocalInventoryProducesAnArgumentError()
    {
        var (analysis, local) = Create();
        var context = local.Binding.LexicalContext!;
        Assert.Throws<ArgumentException>(() => Query(analysis, local, context with { Locals = default }));
        Assert.Same(local.SpecializedBody, Query(analysis, local, context));
    }

    [Fact]
    public void DuplicateEnclosingParameterIdentityIsRejected()
    {
        var (analysis, local) = Create();
        var context = local.Binding.LexicalContext!;
        var argument = Assert.Single(context.EnclosingArguments);
        Assert.Throws<ArgumentException>(() => Query(analysis, local,
            context with { EnclosingArguments = [argument, argument] }));
    }

    [Fact]
    public void OpenEnclosingArgumentCannotPublishAClosedBody()
    {
        var (analysis, local) = Create();
        var context = local.Binding.LexicalContext!;
        var pair = Assert.Single(context.EnclosingArguments);
        Assert.Throws<ArgumentException>(() => Query(analysis, local,
            context with { EnclosingArguments = [pair with { Argument = pair.Parameter }] }));
    }

    [Fact]
    public void ForeignTypeCannotEnterTheEnclosingVector()
    {
        var (analysis, local) = Create();
        var (foreign, _) = Create();
        var context = local.Binding.LexicalContext!;
        var pair = Assert.Single(context.EnclosingArguments);
        Assert.Throws<ArgumentException>(() => Query(analysis, local,
            context with { EnclosingArguments = [pair with { Argument = foreign.Types.GetPrimitive("int") }] }));
    }

    [Fact]
    public void MissingAncestorLocalIsRejected()
    {
        var (analysis, local) = Create();
        var context = local.Binding.LexicalContext!;
        Assert.NotEmpty(context.Locals);
        Assert.Throws<ArgumentException>(() => Query(analysis, local, context with { Locals = [] }));
    }

    [Fact]
    public void DuplicateAncestorLocalIsRejected()
    {
        var (analysis, local) = Create();
        var context = local.Binding.LexicalContext!;
        Assert.Throws<ArgumentException>(() => Query(analysis, local,
            context with { Locals = context.Locals.Add(context.Locals[0]) }));
    }

    [Fact]
    public void LocalInventoryOrderIsValidatedBeforeCacheLookup()
    {
        var (analysis, local) = Create();
        var context = local.Binding.LexicalContext!;
        Assert.True(context.Locals.Length >= 2);
        Assert.Throws<ArgumentException>(() => Query(analysis, local,
            context with { Locals = context.Locals.Reverse().ToImmutableArray() }));
        Assert.Same(local.SpecializedBody, Query(analysis, local, context));
    }

    [Fact]
    public void ForeignLocalCannotBeInsertedEvenWithAReplacedSourceReference()
    {
        var (analysis, local) = Create();
        var (_, foreign) = Create();
        var context = local.Binding.LexicalContext!;
        var foreignLocal = foreign.Binding.LexicalContext!.Locals[0].Local;
        var changed = context with { Locals = context.Locals.SetItem(0,
            context.Locals[0] with { Local = foreignLocal }) };
        Assert.Throws<ArgumentException>(() => Query(analysis, local, changed));
    }

    [Fact]
    public void WrongLocalGenericArityIsRejected()
    {
        var (analysis, local) = Create();
        Assert.Throws<ArgumentException>(() => Query(analysis, local, local.Binding.LexicalContext!, []));
        Assert.Throws<ArgumentException>(() => Query(analysis, local, local.Binding.LexicalContext!,
            [analysis.Types.GetPrimitive("int"), analysis.Types.GetPrimitive("long")]));
    }

    [Fact]
    public void DefaultArgumentVectorIsNotAnEmptyClosedVector()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("func local(): T = value\nlocal()");
        var local = GenericLexicalTestSource.Local(root);
        Assert.Empty(local.Binding.TypeArguments);
        Assert.Throws<ArgumentException>(() => analysis.GetLexicalBody((AstFunction)local.Binding.Target,
            default, local.Binding.LexicalContext!, TestContext.Current.CancellationToken));
        Assert.Equal(BodyBindingStatus.Bound, Query(analysis, local, local.Binding.LexicalContext!).Summary.Status);
    }

    [Fact]
    public void OpenLocalMethodArgumentIsRejected()
    {
        var (analysis, local) = Create();
        var parameter = Assert.Single(analysis.Sources.Types.GetTypeParameters((AstFunction)local.Binding.Target));
        Assert.Throws<ArgumentException>(() => Query(analysis, local, local.Binding.LexicalContext!, [parameter]));
    }

    [Fact]
    public void ForeignLocalMethodArgumentIsRejected()
    {
        var (analysis, local) = Create();
        var (foreign, _) = Create();
        Assert.Throws<ArgumentException>(() => Query(analysis, local, local.Binding.LexicalContext!,
            [foreign.Types.GetPrimitive("int")]));
    }

    [Fact]
    public void ForeignCaptureTypeCannotBeValidatedByItsSpelling()
    {
        var (analysis, local) = Create();
        var (foreign, _) = Create();
        var context = local.Binding.LexicalContext!;
        var changed = context with { Locals = context.Locals.SetItem(0,
            context.Locals[0] with { Type = foreign.Types.GetPrimitive("int") }) };
        Assert.Throws<ArgumentException>(() => Query(analysis, local, changed));
    }

    [Fact]
    public void AnUnassignedLocalCannotCarryASmartCast()
    {
        var (analysis, local) = Create();
        var context = local.Binding.LexicalContext!;
        int index = Array.FindIndex(context.Locals.ToArray(), fact => fact.Local.Name == "captured");
        var fact = context.Locals[index];
        var changed = context with { Locals = context.Locals.SetItem(index,
            fact with { Assigned = false, Narrowed = fact.Type }) };
        Assert.Throws<ArgumentException>(() => Query(analysis, local, changed));
    }

    [Fact]
    public void AMutableCaptureCannotCarryASmartCastIntoTheBody()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("var captured: T = value\n"
            + "func <U> local(item: U): T = captured\nlocal<int>(1)");
        var local = GenericLexicalTestSource.Local(root);
        var context = local.Binding.LexicalContext!;
        int index = Array.FindIndex(context.Locals.ToArray(), fact => fact.Local.Name == "captured");
        var changed = context with { Locals = context.Locals.SetItem(index,
            context.Locals[index] with { Narrowed = context.Locals[index].Type }) };
        Assert.Throws<ArgumentException>(() => Query(analysis, local, changed));
    }
}
