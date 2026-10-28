// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LocalFunctionCaptureAnalysisTests
{
    private static LexicalAnalysis Bind(string source) => LexicalAnalysis.Create(
        AstTestSources.Session("module app\n" + source), TestContext.Current.CancellationToken);

    private static AstFunction Function(LexicalAnalysis analysis, string name) => Assert.Single(
        analysis.Declarations.Files.SelectMany(file => file.Root.Descendants()).OfType<AstFunction>(),
        function => function.Name == name);

    [Fact]
    public void LocalFunctionCapturesResolvedParameterAndVariableWithOrderedAccesses()
    {
        var analysis = Bind("func outer(seed: int) { var count = 0; "
            + "func nested(): int { count += seed; count } }");
        var outer = Function(analysis, "outer");
        var nested = Function(analysis, "nested");
        var captures = analysis.GetLocalFunctionCaptures(nested).OrderBy(capture => capture.Local.Name).ToArray();

        Assert.Equal(2, captures.Length);
        var count = Assert.Single(captures, capture => capture.Local.Name == "count");
        var seed = Assert.Single(captures, capture => capture.Local.Name == "seed");
        Assert.Same(nested, count.Function);
        Assert.Same(nested, seed.Function);
        Assert.Equal(LocalSymbolKind.Variable, count.Local.Kind);
        Assert.Equal(LocalSymbolKind.Parameter, seed.Local.Kind);
        Assert.Equal(LocalFunctionCaptureAccess.ReadWrite, count.Access);
        Assert.Equal(LocalFunctionCaptureAccess.Read, seed.Access);
        Assert.Equal(2, count.References.Length);
        Assert.Single(seed.References);
        Assert.Equal(count.References.Select(reference => reference.Origin.Span.Start).OrderBy(offset => offset),
            count.References.Select(reference => reference.Origin.Span.Start));
        Assert.All(captures.SelectMany(capture => capture.References), reference =>
            Assert.Same(captures.Single(capture => capture.References.Contains(reference)).Local,
                Assert.Single(analysis.GetBinding(reference).Lookup.Locals)));
        Assert.Empty(analysis.GetLocalFunctionCaptures(outer));
    }

    [Fact]
    public void LocalFunctionParameterShadowingDoesNotCaptureTheOuterParameter()
    {
        var analysis = Bind("func outer(item: int) { func nested(item: int): int = item }");
        var outer = Function(analysis, "outer");
        var nested = Function(analysis, "nested");
        var outerItem = Assert.Single(analysis.Scopes.SelectMany(scope => scope.Symbols),
            symbol => symbol.Name == "item" && symbol.ScopeId == analysis.Scopes.Single(scope => ReferenceEquals(scope.Owner, outer)).Id);
        var nestedItem = Assert.Single(analysis.Scopes.SelectMany(scope => scope.Symbols),
            symbol => symbol.Name == "item" && symbol.ScopeId == analysis.Scopes.Single(scope => ReferenceEquals(scope.Owner, nested)).Id);
        var reference = Assert.Single(analysis.Bindings, binding => binding.Reference.Spelling == "item").Reference;

        Assert.NotSame(outerItem, nestedItem);
        Assert.Same(nestedItem, Assert.Single(analysis.GetBinding(reference).Lookup.Locals));
        Assert.Empty(analysis.GetLocalFunctionCaptures(nested));
        Assert.Empty(analysis.LocalFunctionCaptures);
    }

    [Fact]
    public void SelfRecursiveNameResolvesToTheCapturedLocalFunctionSymbol()
    {
        var analysis = Bind("func outer() { func recursive(count: int): int { "
            + "if count == 0 { return 0 }; recursive(count - 1) }; recursive(2) }");
        var recursive = Function(analysis, "recursive");
        var references = analysis.Bindings.Where(binding => binding.Reference.Spelling == "recursive").ToArray();
        var capture = Assert.Single(analysis.GetLocalFunctionCaptures(recursive));
        var selfReference = Assert.Single(references, binding =>
            recursive.Origin.Span.Start <= binding.Reference.Origin.Span.Start
            && binding.Reference.Origin.Span.End <= recursive.Origin.Span.End).Reference;
        var outerCall = Assert.Single(references, binding => !ReferenceEquals(binding.Reference, selfReference)).Reference;

        Assert.Equal(2, references.Length);
        Assert.All(references, binding => Assert.Equal(LocalSymbolKind.Function,
            Assert.Single(binding.Lookup.Locals).Kind));
        Assert.All(references, binding => Assert.Same(capture.Local, Assert.Single(binding.Lookup.Locals)));
        Assert.Equal(LocalFunctionCaptureAccess.Read, capture.Access);
        Assert.Same(selfReference, Assert.Single(capture.References));
        Assert.DoesNotContain(outerCall, capture.References);
    }

    [Fact]
    public void AGrandparentLocalIsInventoriedForEachCrossedLocalFunction()
    {
        var analysis = Bind("func outer(seed: int) { func middle(): int { "
            + "func inner(): int = seed; inner() }; middle() }");
        var middle = Function(analysis, "middle");
        var inner = Function(analysis, "inner");
        var captures = analysis.LocalFunctionCaptures;

        Assert.Equal(new[] { middle, inner }, captures.Select(capture => capture.Function));
        Assert.Same(captures[0].Local, captures[1].Local);
        Assert.All(captures, capture =>
        {
            Assert.Equal("seed", capture.Local.Name);
            Assert.Equal(LocalFunctionCaptureAccess.Read, capture.Access);
            Assert.Single(capture.References);
        });
        Assert.Same(captures[0].References[0], captures[1].References[0]);
    }

    [Fact]
    public void CapturedRefAndOutParametersKeepTheirAccessModesAndSymbolIdentity()
    {
        var analysis = Bind("func consume(ref value: int) {}\n"
            + "func fill(out result: int) { result = 1 }\n"
            + "func outer(ref value: int, out result: int) { "
            + "func nested() { consume(ref value); fill(out result) } }");
        var nested = Function(analysis, "nested");
        var captures = analysis.GetLocalFunctionCaptures(nested);
        var value = Assert.Single(captures, capture => capture.Local.Name == "value");
        var result = Assert.Single(captures, capture => capture.Local.Name == "result");

        Assert.Equal(AstPassingMode.Ref, value.Local.PassingMode);
        Assert.Equal(AstPassingMode.Out, result.Local.PassingMode);
        Assert.Equal(LocalFunctionCaptureAccess.ReadWrite, value.Access);
        Assert.Equal(LocalFunctionCaptureAccess.Write, result.Access);
        Assert.Single(value.References);
        Assert.Single(result.References);
        Assert.All(captures.SelectMany(capture => capture.References), reference =>
            Assert.Same(captures.Single(capture => capture.References.Contains(reference)).Local,
                Assert.Single(analysis.GetBinding(reference).Lookup.Locals)));
    }

    [Fact]
    public void TopLevelAndOrdinaryTypeMemberFunctionsAreNotLocalFunctionCaptures()
    {
        var analysis = Bind("func top(seed: int): int = seed\n"
            + "class Box(val value: int) { func member(): int = value }");
        var top = Function(analysis, "top");
        var member = Function(analysis, "member");

        Assert.Empty(analysis.LocalFunctionCaptures);
        Assert.Empty(analysis.GetLocalFunctionCaptures(top));
        Assert.Empty(analysis.GetLocalFunctionCaptures(member));
    }

    [Fact]
    public void LocalFunctionCaptureQueryRejectsAFunctionFromAnotherSnapshot()
    {
        var first = Bind("func outer(seed: int) { func nested(): int = seed }");
        var second = Bind("func outer(seed: int) { func nested(): int = seed }");
        var foreign = Function(second, "nested");

        Assert.Throws<System.ArgumentException>(() => first.GetLocalFunctionCaptures(foreign));
        Assert.Throws<System.ArgumentNullException>(() => first.GetLocalFunctionCaptures(null!));
    }
}
