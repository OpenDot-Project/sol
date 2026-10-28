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

public sealed class AnonymousFunctionFirSourceContractTests
{
    [Fact]
    public void VerifierRejectsAnonymousParameterTypeChangedAfterBinding()
    {
        var program = Compile("object Main { func main(args: []String) { "
            + "val callback = func(value: int): int = value; val result = callback(41) } }");
        var function = Assert.Single(program.Functions, candidate => candidate.AnonymousFunctionSource is not null);
        var parameter = Assert.Single(function.Parameters);
        var forgedParameter = parameter with { Type = program.Analysis.Types.GetPrimitive("long") };
        var forged = function with
        {
            Parameters = function.Parameters.SetItem(0, forgedParameter),
            Slots = function.Slots.SetItem(parameter.Id, forgedParameter),
        };

        var result = FirVerifier.Verify(Replace(program, forged), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("parameter slot differs from its source contract", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsAnonymousResultTypeChangedAfterBinding()
    {
        var program = Compile("object Main { func main(args: []String) { "
            + "val callback = func(value: int): int = value; val result = callback(41) } }");
        var function = Assert.Single(program.Functions, candidate => candidate.AnonymousFunctionSource is not null);
        var forged = function with { ReturnType = program.Analysis.Types.GetPrimitive("long") };

        var result = FirVerifier.Verify(Replace(program, forged), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("signature differs from source analysis", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsAnonymousCheckedEffectsChangedAfterBinding()
    {
        var program = Compile("import std.stdio\nobject Main { func main(args: []String) { "
            + "val callback = func(value: int): int throws IOException = value } }");
        var function = Assert.Single(program.Functions, candidate => candidate.AnonymousFunctionSource is not null);
        Assert.Single(function.Effects);
        var forged = function with { Effects = [] };

        var result = FirVerifier.Verify(Replace(program, forged), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("signature differs from source analysis", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsIndirectCallRetargetedToSameTypedSiblingLocal()
    {
        var program = Compile("object Main { func main(args: []String) { "
            + "val first = func(value: int): int = value + 1; "
            + "val second = func(value: int): int = value + 2; val result = first(41) } }");
        var main = Assert.Single(program.Functions, function => function.Name == "main");
        var callDeclaration = Assert.Single(main.Body!.Statements.OfType<FirDeclare>(),
            declaration => declaration.Initializer is FirIndirectCall);
        var call = Assert.IsType<FirIndirectCall>(callDeclaration.Initializer);
        var second = Assert.Single(main.Slots, slot => slot.Name == "second");
        var forgedCall = call with
        {
            Function = new FirRead(second.Id, call.Function.Type, call.Function.Origin),
        };
        var forgedMain = ReplaceInitializer(main, call, forgedCall);

        var result = FirVerifier.Verify(Replace(program, forgedMain), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("different source local slot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsLambdaRetargetedToAnonymousFunctionInsideAnonymousFunction()
    {
        var program = Compile("object Main { func main(args: []String) { val outer = "
            + "func(value: int): int { val fromLambda: (int) -> int = { item -> item + 1 }; "
            + "val fromAnonymous: (int) -> int = func(item: int): int = item - 1; "
            + "fromLambda(value) + fromAnonymous(value) }; val answer = outer(41) } }");
        var outer = Assert.Single(program.Functions, function => function.AnonymousFunctionSource is { } source
            && source.Parameters.Any(parameter => parameter.Name == "value"));
        var declarations = outer.Body!.Statements.OfType<FirDeclare>()
            .Where(declaration => declaration.Initializer is FirFunctionReference).ToArray();
        var lambdaReference = Assert.Single(declarations.Select(declaration =>
                Assert.IsType<FirFunctionReference>(declaration.Initializer)),
            reference => program.Functions[reference.Target].LambdaSource is not null);
        var anonymousReference = Assert.Single(declarations.Select(declaration =>
                Assert.IsType<FirFunctionReference>(declaration.Initializer)),
            reference => program.Functions[reference.Target].AnonymousFunctionSource is not null);
        Assert.Equal(lambdaReference.Type, anonymousReference.Type);
        var forged = lambdaReference with { Target = anonymousReference.Target };
        var forgedOuter = ReplaceInitializer(outer, lambdaReference, forged);

        var result = FirVerifier.Verify(Replace(program, forgedOuter), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("lambda reference differs from its source lambda identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifierRejectsAnonymousFunctionRetargetedToLambdaInsideLambda()
    {
        var program = Compile("object Main { func main(args: []String) { "
            + "val outer: (int) -> int = { value -> "
            + "val fromAnonymous: (int) -> int = func(item: int): int = item + 1; "
            + "val fromLambda: (int) -> int = { item -> item - 1 }; "
            + "fromAnonymous(value) + fromLambda(value) }; val answer = outer(41) } }");
        var outer = Assert.Single(program.Functions, function => function.LambdaSource is { } source
            && source.Parameters.Any(parameter => parameter.Children.Any(child => child.Origin.Text == "value")));
        var declarations = outer.Body!.Statements.OfType<FirDeclare>()
            .Where(declaration => declaration.Initializer is FirFunctionReference).ToArray();
        var anonymousReference = Assert.Single(declarations.Select(declaration =>
                Assert.IsType<FirFunctionReference>(declaration.Initializer)),
            reference => program.Functions[reference.Target].AnonymousFunctionSource is not null);
        var lambdaReference = Assert.Single(declarations.Select(declaration =>
                Assert.IsType<FirFunctionReference>(declaration.Initializer)),
            reference => program.Functions[reference.Target].LambdaSource is not null);
        Assert.Equal(anonymousReference.Type, lambdaReference.Type);
        var forged = anonymousReference with { Target = lambdaReference.Target };
        var forgedOuter = ReplaceInitializer(outer, anonymousReference, forged);

        var result = FirVerifier.Verify(Replace(program, forgedOuter), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("anonymous-function reference differs from its source identity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecoveredNestedAnonymousFunctionBodyIsNotAdmittedToFir()
    {
        const string text = "module test\nobject Main { func main(args: []String) {\n"
            + "val callback: () -> int = { -> val broken = func(): int\n"
            + "val after: int = 13\n after } } }\n";
        var syntax = SyntaxTree.Parse(SourceText.FromString(text),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(syntax.HasErrors);
        var source = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        AstVerifier.Verify(source, TestContext.Current.CancellationToken);
        var incomplete = Assert.Single(source.Nodes.OfType<AstAnonymousFunction>());
        Assert.Equal(AstBodyKind.Missing, incomplete.Body.Kind);
        Assert.Contains(source.Nodes.OfType<AstProperty>(), property => property.BindingNames.Contains("after"));

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.NotEmpty(compilation.Diagnostics);
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

    private static FirFunction ReplaceInitializer(FirFunction function, FirExpression before, FirExpression after)
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
