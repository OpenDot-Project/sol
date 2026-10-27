// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using System.Text.Json;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class LambdaFunctionValueSerializationTests
{
    private const string Source = "object Main { func main(args: []String): () {\n"
        + "val named: (int) -> int = { value -> value + 1 }\n"
        + "val positional: (int) -> int = { $0 + 2 }\n"
        + "val callbacks: []((int) -> int) = [named, positional]\n"
        + "val answer = callbacks[0](40)\n} }";

    [Fact]
    public void LambdaFunctionsAndReferencesUseTheExistingDeterministicInspectionSchema()
    {
        var firstProgram = Compile();
        var secondProgram = Compile();
        string first = FirJson.Serialize(firstProgram, TestContext.Current.CancellationToken);
        string second = FirJson.Serialize(secondProgram, TestContext.Current.CancellationToken);
        Assert.Equal(first, second);

        using var json = JsonDocument.Parse(first);
        var root = json.RootElement;
        Assert.Equal(6, root.GetProperty("schema").GetInt32());
        Assert.True(root.GetProperty("verified").GetBoolean());

        var lambdaFunctions = firstProgram.Program.Functions
            .Where(function => function.LambdaSource is not null).ToArray();
        Assert.Equal(2, lambdaFunctions.Length);
        var serializedFunctions = root.GetProperty("functions").EnumerateArray().ToArray();
        Assert.All(lambdaFunctions, function =>
        {
            var serialized = Assert.Single(serializedFunctions,
                candidate => candidate.GetProperty("id").GetInt32() == function.Id);
            Assert.Equal("<lambda>", serialized.GetProperty("name").GetString());
            Assert.Equal(function.ReturnType.Id, serialized.GetProperty("returnType").GetInt32());
            Assert.Empty(serialized.GetProperty("specializationArguments").EnumerateArray());
            Assert.Equal(function.Parameters.Select(parameter => parameter.Id),
                serialized.GetProperty("parameters").EnumerateArray().Select(parameter => parameter.GetInt32()));
            Assert.Equal("FirBlock", serialized.GetProperty("body").GetProperty("kind").GetString());
        });

        var lambdaTargets = lambdaFunctions.Select(function => function.Id).ToHashSet();
        var references = Nodes(root, "FirFunctionReference")
            .Where(reference => lambdaTargets.Contains(reference.GetProperty("value")
                .GetProperty("target").GetInt32())).ToArray();
        Assert.Equal(lambdaFunctions.Length, references.Length);
        foreach (var reference in references)
        {
            int targetId = reference.GetProperty("value").GetProperty("target").GetInt32();
            var target = Assert.Single(serializedFunctions,
                candidate => candidate.GetProperty("id").GetInt32() == targetId);
            Assert.Equal("<lambda>", target.GetProperty("name").GetString());
            Assert.Equal("Function", Type(root, reference.GetProperty("type").GetInt32())
                .GetProperty("kind").GetString());
        }

        // FIR JSON is an inspection view. It carries synthetic function rows and references,
        // but it does not serialize the verifier's in-memory AstLambda source marker or a closure
        // environment/source reconstruction record.
        foreach (var function in serializedFunctions.Where(candidate => lambdaFunctions.Any(lambda =>
            lambda.Id == candidate.GetProperty("id").GetInt32())))
        {
            Assert.False(function.TryGetProperty("lambdaSource", out _));
            Assert.False(function.TryGetProperty("sourceText", out _));
            Assert.False(function.TryGetProperty("captures", out _));
            Assert.False(function.TryGetProperty("environment", out _));
        }
    }

    private static VerifiedFirProgram Compile()
    {
        var result = FirCompilation.Create([AstTestSources.Parse(Source)], TestContext.Current.CancellationToken);
        Assert.True(result.Program is not null, string.Join('\n', result.Diagnostics.Select(diagnostic =>
            diagnostic.Code + "@" + diagnostic.Span.Start + " " + diagnostic.Message)));
        return result.Program;
    }

    private static JsonElement Type(JsonElement root, int id) => Assert.Single(
        root.GetProperty("types").EnumerateArray(), candidate => candidate.GetProperty("id").GetInt32() == id);

    private static System.Collections.Generic.IEnumerable<JsonElement> Nodes(JsonElement current, string kind)
    {
        if (current.ValueKind == JsonValueKind.Object)
        {
            if (current.TryGetProperty("kind", out var nodeKind)
                && nodeKind.ValueKind == JsonValueKind.String && nodeKind.GetString() == kind)
            { yield return current; }
            foreach (var property in current.EnumerateObject())
            {
                foreach (var node in Nodes(property.Value, kind)) { yield return node; }
            }
        }
        else if (current.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in current.EnumerateArray())
            {
                foreach (var node in Nodes(item, kind)) { yield return node; }
            }
        }
    }
}
