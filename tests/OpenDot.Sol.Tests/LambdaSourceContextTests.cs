// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaSourceContextTests
{
    [Fact]
    public void ExplicitParameterTypeInfersAnUncontextualizedFunctionValue()
    {
        var verified = Compile("object Main { func main(args: []String) {\n"
            + "val increment = { value: int -> value + 1 }\n"
            + "val result = increment(41)\n} }");
        var sourceLambda = Assert.Single(verified.Program.Analysis.Sources.Declarations.Files
            .SelectMany(file => file.Nodes).OfType<AstLambda>());

        var sourceType = Assert.IsType<ResolvedType>(verified.Program.Analysis.GetExpression(sourceLambda)?.Type);
        var synthetic = Assert.Single(verified.Program.Functions,
            function => ReferenceEquals(function.LambdaSource, sourceLambda));

        Assert.Equal(ResolvedTypeKind.Function, sourceType.Kind);
        Assert.Equal("int", sourceType.Arguments[0].Name);
        Assert.Equal("int", sourceType.Arguments[^1].Name);
        Assert.Equal("int", Assert.Single(synthetic.Parameters).Type.Name);
        Assert.Equal("int", synthetic.ReturnType.Name);
    }

    [Fact]
    public void NamedAndTrailingCallArgumentsMapLambdasToTheSelectedCallbackParameter()
    {
        var verified = Compile("func apply(value: int, callback: (int) -> int): int = callback(value)\n"
            + "object Main { func main(args: []String) {\n"
            + "val named = apply(callback = { value -> value + 1 }, value = 41)\n"
            + "val trailing = apply(40) { value -> value + 2 }\n} }");
        var bindings = verified.Program.Analysis.Calls.Where(binding => binding.Call.Arguments
            .Any(argument => argument.Value is AstLambda)).ToArray();

        Assert.Equal(2, bindings.Length);
        Assert.Contains(bindings, binding =>
        {
            var argument = Assert.Single(binding.Call.Arguments, item => item.Value is AstLambda);
            var mapping = Assert.Single(binding.Arguments, item => item.ArgumentIndex >= 0
                && item.ArgumentIndex < binding.Call.Arguments.Length
                && ReferenceEquals(binding.Call.Arguments[item.ArgumentIndex].Value, argument.Value));
            return mapping.ParameterIndex == 1 && !mapping.IsTrailingLambda;
        });
        Assert.Contains(bindings, binding =>
        {
            var argument = Assert.Single(binding.Call.Arguments, item => item.Value is AstLambda);
            var mapping = Assert.Single(binding.Arguments, item => item.ArgumentIndex >= 0
                && item.ArgumentIndex < binding.Call.Arguments.Length
                && ReferenceEquals(binding.Call.Arguments[item.ArgumentIndex].Value, argument.Value));
            return mapping.ParameterIndex == 1 && mapping.IsTrailingLambda;
        });
        Assert.Equal(2, verified.Program.Functions.Count(function => function.LambdaSource is not null));
    }

    [Fact]
    public void FunctionExpressionBodySuppliesTheLambdaResultContext()
    {
        var verified = Compile("func create(): () -> int = { -> 42 }\n"
            + "object Main { func main(args: []String) {\n"
            + "val callback = create()\n"
            + "val answer = callback()\n} }");
        var sourceLambda = Assert.Single(verified.Program.Analysis.Sources.Declarations.Files
            .SelectMany(file => file.Nodes).OfType<AstLambda>());
        var sourceType = verified.Program.Analysis.GetExpression(sourceLambda)?.Type;
        var function = Assert.Single(verified.Program.Functions,
            candidate => ReferenceEquals(candidate.LambdaSource, sourceLambda));
        var creator = Assert.Single(verified.Program.Functions,
            candidate => candidate.Name == "create");
        var reference = Assert.IsType<FirFunctionReference>(Assert.IsType<FirReturn>(
            Assert.Single(creator.Body!.Statements)).Value);

        Assert.Equal(ResolvedTypeKind.Function, sourceType?.Kind);
        Assert.Empty(sourceType!.ParameterModes);
        Assert.Equal("int", sourceType.Arguments[^1].Name);
        Assert.Empty(function.Parameters);
        Assert.Equal(sourceType, reference.Type);
        Assert.Equal(function.Id, reference.Target);
        Assert.Equal("int", function.ReturnType.Name);
    }

    [Fact]
    public void ContextualVarianceThatNeedsAFunctionAbiAdapterIsRejected()
    {
        const string text = "func accept(callback: (String) -> Object): Object = callback(\"sol\")\n"
            + "object Main { func main(args: []String) {\n"
            + "val result = accept(callback = { value: Object -> value })\n} }";
        var source = AstTestSources.Parse(text);
        var lambda = Assert.Single(source.Root.Descendants().OfType<AstLambda>());

        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);

        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400"
            && diagnostic.Span == lambda.Origin.Span);
    }

    private static VerifiedFirProgram Compile(string text)
    {
        var result = FirCompilation.Create([AstTestSources.Parse(text)], TestContext.Current.CancellationToken);
        Assert.True(result.Program is not null, string.Join("\n", result.Diagnostics
            .Select(diagnostic => diagnostic.Code + "[" + diagnostic.Span.Start + "]: " + diagnostic.Message)));
        return Assert.IsType<VerifiedFirProgram>(result.Program);
    }
}
