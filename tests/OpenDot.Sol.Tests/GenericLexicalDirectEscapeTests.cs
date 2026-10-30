// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalDirectEscapeTests
{
    [Theory]
    [InlineData("local<int>(1)")]
    [InlineData("local(1)")]
    public void ExplicitGenericInvocationIsADirectLocalUse(string invocation)
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(item: U): T = value\n" + invocation);
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var escape = local.SpecializedBody!.Sources.Lexical.GetLocalFunctionEscape((AstFunction)local.Binding.Target);
        Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.All(escape.Uses, use => Assert.Equal(LocalFunctionEscapeUseKind.DirectInvocation, use.Kind));
        Assert.NotEmpty(escape.Uses);
    }

    [Fact]
    public void PassingTheSameGenericFunctionAsACallbackStillMayEscape()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(item: U): U = item\n"
            + "accept(local)\nlocal<int>(1)\nvalue", "func accept(@escaping callback: (int) -> int) {}");
        var local = GenericLexicalTestSource.Local(root);
        var escape = local.SpecializedBody!.Sources.Lexical.GetLocalFunctionEscape((AstFunction)local.Binding.Target);
        Assert.NotEqual(LocalFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.CallArgument);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.DirectInvocation);
    }

    [Fact]
    public void FunctionValueTailReturnCannotBeHiddenByAnotherDirectInvocation()
    {
        var analysis = GenericMemberProofTestSource.Analyze("func outer(): (int) -> int {\n"
            + "func <U> local(item: U): U = item\nlocal<int>(1)\nlocal\n}");
        var function = GenericMemberProofTestSource.Function(analysis, "local");
        var escape = analysis.Sources.Lexical.GetLocalFunctionEscape(function);
        Assert.NotEqual(LocalFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.ReturnValue);
        Assert.Contains(escape.Uses, use => use.Kind == LocalFunctionEscapeUseKind.DirectInvocation);
    }

    [Fact]
    public void RecursiveExplicitGenericInvocationRetainsItsLocalIdentity()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U> local(item: U): T = local<U>(item)\nlocal<int>(1)");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var escape = local.SpecializedBody!.Sources.Lexical.GetLocalFunctionEscape((AstFunction)local.Binding.Target);
        Assert.Equal(LocalFunctionEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Equal(2, escape.Uses.Length);
        Assert.True(Assert.Single(local.Dependencies).IsRecursiveReference);
    }
}
