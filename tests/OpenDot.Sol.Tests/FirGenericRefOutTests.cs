// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirGenericRefOutTests
{
    [Fact]
    public void ClosedGenericInstancesPreserveRefAndOutAliases()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("""
            import std.stdio
            func <T> fill(out destination: T, source: T) { destination = source }
            func <T> replace(ref current: T, next: T) { current = next }
            object Main {
                func main(args: []String) throws IOException {
                    var value: int
                    fill<int>(out value, 41)
                    replace<int>(ref value, 42)
                    println(value)
                }
            }
            """)], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        Assert.Empty(compilation.Diagnostics);
        Assert.NotNull(compilation.Program);
        var program = compilation.Program.Program;
        var fill = Assert.Single(program.Functions,
            function => function.Name == "fill" && !function.SpecializationArguments.IsEmpty);
        var replace = Assert.Single(program.Functions,
            function => function.Name == "replace" && !function.SpecializationArguments.IsEmpty);
        Assert.Equal(AstPassingMode.Out, fill.Parameters[0].Mode);
        Assert.Equal("int", fill.Parameters[0].Type.Name);
        Assert.Equal(AstPassingMode.Ref, replace.Parameters[0].Mode);
        Assert.Equal("int", replace.Parameters[0].Type.Name);

        var main = Assert.Single(program.Functions, function => function.Name == "main");
        var calls = main.Body!.Statements.OfType<FirEvaluate>().Select(evaluation => evaluation.Value)
            .OfType<FirCall>().Where(call => program.Functions[call.Target].Name is "fill" or "replace").ToArray();
        Assert.Equal(2, calls.Length);
        var fillCall = Assert.Single(calls, call => program.Functions[call.Target].Name == "fill");
        var replaceCall = Assert.Single(calls, call => program.Functions[call.Target].Name == "replace");
        Assert.Equal(AstPassingMode.Out, fillCall.Arguments[0].Mode);
        Assert.Equal(AstPassingMode.Ref, replaceCall.Arguments[0].Mode);
        Assert.IsType<FirRead>(fillCall.Arguments[0].Value);
        Assert.IsType<FirRead>(replaceCall.Arguments[0].Value);
        Assert.NotEmpty(LoweringTrace.Inspect(compilation.Program,
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void GenericOutParameterCannotBeReadBeforeAssignment()
    {
        var source = AstTestSources.Parse("""
            func <T> invalid(out destination: T): T = destination
            object Main { func main(args: []String) { } }
            """);
        var analysis = OpenDot.Sol.Analysis.BodyAnalysis.Create(
            OpenDot.Sol.Analysis.SourceAnalysis.Create(
                OpenDot.Sol.Analysis.AnalysisSession.Create([source],
                    cancellationToken: TestContext.Current.CancellationToken), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2240");
        var compilation = FirCompilation.Create([source], TestContext.Current.CancellationToken);
        Assert.Null(compilation.Program);
    }

    [Fact]
    public void GenericRefAndOutCallsRetainDistinctParameterModesInJson()
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse("""
            func <T> copy(out destination: T, ref source: T) { destination = source }
            object Main { func main(args: []String) { var value = 3; copy<int>(out value, ref value) } }
            """)], TestContext.Current.CancellationToken);

        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics
            .Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
        Assert.NotNull(compilation.Program);
        var program = compilation.Program.Program;
        var target = Assert.Single(program.Functions,
            function => function.Name == "copy" && !function.SpecializationArguments.IsEmpty);
        Assert.Equal(new[] { AstPassingMode.Out, AstPassingMode.Ref },
            target.Parameters.Select(parameter => parameter.Mode).ToArray());
        var call = Assert.Single(program.Functions.Single(function => function.Name == "main")
            .Body!.Statements.OfType<FirEvaluate>().Select(evaluation => evaluation.Value).OfType<FirCall>());
        Assert.Equal(new[] { AstPassingMode.Out, AstPassingMode.Ref },
            call.Arguments.Select(argument => argument.Mode).ToArray());
        string json = FirJson.Serialize(compilation.Program, TestContext.Current.CancellationToken);
        Assert.Contains("\"mode\": \"Out\"", json);
        Assert.Contains("\"mode\": \"Ref\"", json);
    }
}
