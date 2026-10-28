// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class EscapingParameterContractTests
{
    [Fact]
    public void NonescapingParameterCanBeInvokedAndReceiveAProvenLocalBorrow()
    {
        var analysis = Analyze("module app\nfunc invoke(callback: () -> int): int = callback()\n"
            + "func use(ref value: int): int { val local = { -> value }; invoke(local) }");
        var lambda = Assert.Single(analysis.Sources.Lexical.LambdaEscapes);

        Assert.False(analysis.HasErrors, Describe(analysis));
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, lambda.Disposition);
        Assert.Contains(lambda.Uses, use => use.Kind == LambdaEscapeUseKind.NonescapingCallArgument);
    }

    [Fact]
    public void EscapingParameterRejectsBorrowedLambdaAndAnonymousFunctionArguments()
    {
        var lambda = Analyze("module app\nfunc retain(@escaping callback: () -> int): () -> int = callback\n"
            + "func use(ref value: int): () -> int = retain({ -> value })");
        var anonymous = Analyze("module app\nfunc retain(@escaping callback: () -> int): () -> int = callback\n"
            + "func use(ref value: int): () -> int = retain(func(): int { value })");

        Assert.Contains(lambda.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
        Assert.Contains(anonymous.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
        Assert.Equal(LambdaEscapeDisposition.MayEscape,
            lambda.Sources.Lexical.GetLambdaEscape(Assert.Single(lambda.Sources.Lexical.LambdaEscapes).Lambda).Disposition);
    }

    [Fact]
    public void NonescapingParameterMayFlowThroughImmutableLocalAliases()
    {
        var analysis = Analyze("module app\nfunc invoke(callback: () -> int): int = callback()\n"
            + "func relay(callback: () -> int): int { val first = callback; val second = first; invoke(second) }");

        Assert.False(analysis.HasErrors, Describe(analysis));
    }

    [Fact]
    public void MutableAliasMayBeReturnedAfterASequentialOverwriteWithAnIndependentClosure()
    {
        var analysis = Analyze("module app\nfunc replace(callback: () -> int): () -> int { "
            + "var slot = callback; slot = { -> 42 }; return slot }");

        Assert.False(analysis.HasErrors, Describe(analysis));
    }

    [Fact]
    public void ConditionalOverwriteDoesNotHideThePathThatStillReturnsTheParameter()
    {
        var analysis = Analyze("module app\nfunc maybe(flag: bool, callback: () -> int): () -> int { "
            + "var slot = callback; if flag { slot = { -> 42 } }; return slot }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2264");
    }

    [Fact]
    public void NonescapingParameterCannotBeReturned()
    {
        var analysis = Analyze("module app\nfunc invalid(callback: () -> int): () -> int { return callback }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2264");
    }

    [Fact]
    public void NonescapingParameterMayNotBeCapturedByAnEscapingClosure()
    {
        var analysis = Analyze("module app\nfunc invalid(callback: () -> int): () -> int { "
            + "return { -> callback() } }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2264");
    }

    [Fact]
    public void NonescapingParameterMayBeCapturedByAProvenLocalClosure()
    {
        var analysis = Analyze("module app\nfunc invoke(callback: () -> int): int { "
            + "val wrapper = { -> callback() }; wrapper() }");

        Assert.False(analysis.HasErrors, Describe(analysis));
    }

    [Theory]
    [InlineData("if callback == null { return 0 }; callback!!()")]
    [InlineData("if callback == null { return 0 }; val required = callback!!; required()")]
    public void NullableNonescapingParameterCanBeAssertedAndInvokedWithoutPublication(string body)
    {
        var analysis = Analyze("module app\nfunc invoke(callback: (() -> int)?): int { " + body + " }");

        Assert.False(analysis.HasErrors, Describe(analysis));
    }

    [Fact]
    public void NonescapingParameterCannotBeForwardedByAProvenLocalClosureToEscapingSlot()
    {
        var analysis = Analyze("module app\nfunc retain(@escaping callback: () -> int): int = callback()\n"
            + "func invalid(callback: () -> int): int { val wrapper = { -> retain(callback) }; wrapper() }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2264");
    }

    [Fact]
    public void UnusedLocalAliasDoesNotHideCaptureByAnEscapingClosure()
    {
        var analysis = Analyze("module app\nfunc invalid(callback: () -> int): () -> int { "
            + "return { -> val retained = callback; 1 } }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2264");
    }

    [Fact]
    public void NonescapingParameterMayForwardOnlyToAnotherNonescapingSlot()
    {
        var safe = Analyze("module app\nfunc invoke(callback: () -> int): int = callback()\n"
            + "func relay(callback: () -> int): int = invoke(callback)");
        var escaping = Analyze("module app\nfunc retain(@escaping callback: () -> int): () -> int = callback\n"
            + "func relay(callback: () -> int): () -> int = retain(callback)");

        Assert.False(safe.HasErrors, Describe(safe));
        Assert.Contains(escaping.Diagnostics, diagnostic => diagnostic.Code == "SOL2264");
    }

    [Fact]
    public void NonescapingParameterCannotBeForwardedToAnIndirectUnknownConsumer()
    {
        var analysis = Analyze("module app\nfunc relay(callback: () -> int, "
            + "consume: (() -> int) -> int): int = consume(callback)");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2264");
    }

    [Theory]
    [InlineData("func invalid(@escaping value: int): int = value")]
    [InlineData("func invalid(@escaping @escaping callback: () -> int): int = callback()")]
    [InlineData("func invalid(@escaping(reason = \"x\") callback: () -> int): int = callback()")]
    [InlineData("func invalid(@escaping ref callback: () -> int): int = callback()")]
    public void InvalidEscapingAnnotationUseIsRejected(string source)
    {
        var analysis = Analyze("module app\n" + source);

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2263");
    }

    [Fact]
    public void NullableFunctionParameterCanBeMarkedEscaping()
    {
        var analysis = Analyze("module app\nfunc retain(@escaping callback: (() -> int)?): (() -> int)? = callback");

        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2263");
    }

    [Fact]
    public void ConstructorFunctionPropertiesRequireAnExplicitEscapingContract()
    {
        var annotated = Analyze("module app\nclass Holder(@escaping val callback: () -> int) {} ");
        var unannotated = Analyze("module app\nclass Holder(val callback: () -> int) {} ");

        Assert.DoesNotContain(annotated.Diagnostics, diagnostic => diagnostic.Code == "SOL2263");
        Assert.Contains(unannotated.Diagnostics, diagnostic => diagnostic.Code == "SOL2264");
    }

    [Fact]
    public void OverridesMustPreserveTheCallableParameterLifetimeContract()
    {
        var broadens = Analyze("module app\nopen class Base { open func invoke(callback: () -> int): int = callback() }\n"
            + "class Derived: Base { override func invoke(@escaping callback: () -> int): int = callback() }");
        var narrows = Analyze("module app\nopen class Base { open func retain(@escaping callback: () -> int): () -> int = callback }\n"
            + "class Derived: Base { override func retain(callback: () -> int): () -> int = callback }");

        Assert.Contains(broadens.Diagnostics, diagnostic => diagnostic.Code == "SOL2265");
        Assert.Contains(narrows.Diagnostics, diagnostic => diagnostic.Code == "SOL2265");
    }

    [Fact]
    public void NamedAndTrailingMemberArgumentsUseTheResolvedParameterContract()
    {
        var named = Analyze("module app\nclass Runner() { "
            + "func invoke(callback: () -> int): int = callback() }\n"
            + "func use(ref value: int): int { val runner: Runner = Runner(); "
            + "runner.invoke(callback = { -> value }) }");
        var trailing = Analyze("module app\nclass Runner() { "
            + "func invoke(callback: () -> int): int = callback() }\n"
            + "func use(ref value: int): int { val runner: Runner = Runner(); "
            + "runner.invoke { -> value } }");
        var escaping = Analyze("module app\nclass Runner() { "
            + "func retain(@escaping callback: () -> int): () -> int = callback }\n"
            + "func use(ref value: int): () -> int { val runner: Runner = Runner(); "
            + "runner.retain(callback = { -> value }) }");

        Assert.False(named.HasErrors, Describe(named));
        Assert.False(trailing.HasErrors, Describe(trailing));
        Assert.Contains(escaping.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session(source);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static string Describe(BodyAnalysis analysis) => string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
        diagnostic.Code + " " + diagnostic.Message));
}
