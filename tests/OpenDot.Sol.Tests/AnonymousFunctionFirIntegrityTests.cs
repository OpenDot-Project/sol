// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class AnonymousFunctionFirIntegrityTests
{
    [Fact]
    public void VerifierRejectsSameTypedAnonymousBodyMutation()
    {
        var program = Compile("object Main { func main(args: []String) { "
            + "val callback = func(value: int): int = value + 1; val answer = callback(41) } }");
        var target = Assert.Single(program.Functions, function => function.AnonymousFunctionSource is not null);
        var targetBody = Assert.IsType<FirBlock>(target.Body);
        var returned = Assert.IsType<FirReturn>(Assert.Single(targetBody.Statements));
        var binary = Assert.IsType<FirBinary>(returned.Value);
        var changed = target with
        {
            Body = targetBody with
            {
                Statements = targetBody.Statements.SetItem(0,
                    returned with { Value = binary with { Operation = "-" } }),
            },
        };

        var result = FirVerifier.Verify(Replace(program, changed), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("binary operator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsAnonymousFunctionReferenceRetargetedToSiblingSource()
    {
        var program = Compile("object Main { func main(args: []String) { "
            + "val first = func(value: int): int = value + 1; "
            + "val second = func(value: int): int = value + 2; "
            + "val left = first(1); val right = second(1) } }");
        var targets = program.Functions.Where(function => function.AnonymousFunctionSource is not null)
            .OrderBy(function => function.AnonymousFunctionSource!.Origin.Span.Start).ToArray();
        Assert.Equal(2, targets.Length);
        var main = Assert.Single(program.Functions, function => function.Name == "main");
        var declarations = main.Body!.Statements.OfType<FirDeclare>().ToArray();
        var firstReference = Assert.IsType<FirFunctionReference>(declarations[0].Initializer);
        var secondReference = Assert.IsType<FirFunctionReference>(declarations[1].Initializer);
        Assert.NotEqual(firstReference.Target, secondReference.Target);
        var forgedMain = ReplaceInitializer(main, firstReference,
            firstReference with { Target = secondReference.Target });

        var result = FirVerifier.Verify(Replace(program, forgedMain), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("source identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsDuplicateSyntheticFunctionForOneAnonymousSource()
    {
        var program = Compile("object Main { func main(args: []String) { "
            + "val callback = func(value: int): int = value + 1; val answer = callback(41) } }");
        var target = Assert.Single(program.Functions, function => function.AnonymousFunctionSource is not null);
        var duplicate = target with { Id = program.Functions.Length };

        var result = FirVerifier.Verify(program with { Functions = program.Functions.Add(duplicate) },
            TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("exactly one synthetic FIR function", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsAnonymousSyntheticFunctionWhenItsSourceMarkerIsRemoved()
    {
        var program = Compile("object Main { func main(args: []String) { "
            + "val callback = func(value: int): int = value + 1; val answer = callback(41) } }");
        var target = Assert.Single(program.Functions, function => function.AnonymousFunctionSource is not null);
        var forged = target with { AnonymousFunctionSource = null };

        var result = FirVerifier.Verify(Replace(program, forged), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402");
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void FunctionValueLiftingHonorsTheSourceNestingBudget(int depth, bool supported)
    {
        var syntax = SyntaxTree.Parse(SourceText.FromString("module test\n" + NestedAnonymousFunctionSource(depth)),
            maxDepth: 256, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(syntax.HasErrors, string.Join("\n", syntax.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var source = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        AstVerifier.Verify(source, TestContext.Current.CancellationToken);

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Equal(supported, compilation.Program is not null);
        if (supported)
        {
            Assert.Empty(compilation.Diagnostics);
            Assert.Equal(depth, compilation.Program!.Program.Functions.Count(
                function => function.AnonymousFunctionSource is not null));
        }
        else
        {
            Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
            Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2402");
        }
    }

    private static string NestedAnonymousFunctionSource(int depth)
    {
        string expression = "1";
        string returnType = "int";
        for (int level = 0; level < depth; level++)
        {
            expression = $"func(): {returnType} = {expression}";
            returnType = $"() -> {returnType}";
        }
        return $"object Main {{ func main(args: []String) {{ val value = {expression} }} }}";
    }

    private static FirProgram Compile(string source)
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(source)], TestContext.Current.CancellationToken);
        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        return compilation.Program.Program;
    }

    private static FirProgram Replace(FirProgram program, FirFunction replacement)
        => program with { Functions = program.Functions.SetItem(replacement.Id, replacement) };

    private static FirFunction ReplaceInitializer(FirFunction function,
        FirFunctionReference before, FirFunctionReference after)
    {
        var statements = function.Body!.Statements.ToBuilder();
        int index = -1;
        for (int candidate = 0; candidate < statements.Count; candidate++)
        {
            if (statements[candidate] is FirDeclare { Initializer: { } initializer }
                && ReferenceEquals(initializer, before))
            { index = candidate; break; }
        }
        Assert.True(index >= 0);
        var declaration = (FirDeclare)statements[index];
        statements[index] = declaration with { Initializer = after };
        return function with { Body = function.Body with { Statements = statements.ToImmutable() } };
    }
}
