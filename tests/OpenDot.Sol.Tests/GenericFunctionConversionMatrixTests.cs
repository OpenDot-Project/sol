// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericFunctionConversionMatrixTests
{
    private const string Core = "module std.core\n"
        + "open class Object {}\n"
        + "open class Animal: Object {}\n"
        + "class Dog: Animal {}\n"
        + "class Text: Object {}\n"
        + "class String: Object {}\n"
        + "open class Throwable: Object {}\n"
        + "open class Exception: Throwable {}\n"
        + "open class IOException: Exception {}\n"
        + "class PermissionFailure: Exception {}";

    private static BodyAnalysis Analyze(string source)
    {
        var sources = SourceAnalysis.Create(AstTestSources.Session([source, Core]),
            TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors,
            string.Join('\n', sources.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static ExpressionBinding Selected(BodyAnalysis analysis, string name) =>
        Assert.Single(analysis.Expressions, binding => binding.FunctionValueTarget?.Name == name);

    [Theory]
    [InlineData("ref", "ref", "Text", true)]
    [InlineData("ref", "ref", "Object", false)]
    [InlineData("out", "out", "Text", true)]
    [InlineData("out", "out", "Object", false)]
    public void GenericReferenceInferenceCombinesExactStorageWithValueContravariance(
        string declarationMode, string expectedMode, string valueType, bool accepted)
    {
        string declarationName = declarationMode == "ref" ? "replace" : "initialize";
        string body = declarationMode == "ref"
            ? "current = replacement"
            : "destination = replacement";
        string firstName = declarationMode == "ref" ? "current" : "destination";
        string function = $"func <T> {declarationName}({declarationMode} {firstName}: T, replacement: T): () {{ {body} }}\n";
        string source = "module app\n" + function
            + $"func use(): () {{ val callback: ({expectedMode} Text, {valueType}) -> () = {declarationName} }}";

        var analysis = Analyze(source);

        if (!accepted)
        {
            Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
            Assert.DoesNotContain(analysis.Expressions,
                binding => binding.FunctionValueTarget?.Name == declarationName);
            return;
        }

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.Equal("std.core.Text", Assert.Single(Selected(analysis, declarationName)
            .FunctionValueTypeArguments).Name);
    }

    [Fact]
    public void RefAndOutFunctionValueModesDoNotConvertIntoEachOther()
    {
        var analysis = Analyze("module app\n"
            + "func <T> initialize(out destination: T, replacement: T): () { destination = replacement }\n"
            + "func use(): () { val callback: (ref Text, Text) -> () = initialize }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2206");
        Assert.DoesNotContain(analysis.Expressions,
            binding => binding.FunctionValueTarget?.Name == "initialize");
    }

    [Fact]
    public void OpenEffectInferenceAllowsSeveralNarrowSourceEffectsUnderOneBroadBudget()
    {
        var analysis = Analyze("module app\n"
            + "func <E: Throwable> read(): String throws E, IOException = \"text\"\n"
            + "func use(): () { val callback: () -> String throws Exception = read }");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Expressions,
            binding => binding.FunctionValueTarget?.Name == "read");
    }

    [Fact]
    public void FixedEffectCandidateRemainsDeferredWhenTheOpenEffectCannotBeClosed()
    {
        var analysis = Analyze("module app\n"
            + "func <E: Throwable> read(): String throws E, PermissionFailure = \"text\"\n"
            + "func use(): () { val callback: () -> String throws IOException = read }");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        Assert.False(analysis.IsComplete);
        Assert.DoesNotContain(analysis.Expressions, binding => binding.FunctionValueTarget?.Name == "read");
    }

    [Fact]
    public void InferredGenericAndNongenericResultsParticipateInOverloadRanking()
    {
        var analysis = Analyze("module app\n"
            + "func <T> choose(value: T) = value\n"
            + "func choose(value: Object) = value\n"
            + "func use(): () { val callback: (Text) -> Object = choose }");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var reference = Selected(analysis, "choose");
        Assert.Empty(reference.FunctionValueTypeArguments);
        Assert.Equal("std.core.Object", analysis.Sources.Types.GetType(reference.FunctionValueTarget!
            .Parameters.Single().Type!).Name);
        Assert.Equal("std.core.Object", analysis.GetBody(reference.FunctionValueTarget!)!.ReturnType!.Name);
    }

    [Fact]
    public void GenericResultOnlyReferenceInferenceClosesFromTheExpectedReturnType()
    {
        var analysis = Analyze("module app\n"
            + "func <T> create(): T { for {} }\n"
            + "func use(): () { val callback: () -> Object = create }");

        Assert.False(analysis.HasErrors,
            string.Join('\n', analysis.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var reference = Selected(analysis, "create");
        Assert.Equal("std.core.Object", Assert.Single(reference.FunctionValueTypeArguments).Name);
        Assert.Equal("std.core.Object", reference.Type!.Arguments[^1].Name);
    }
}
