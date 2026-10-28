// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using System.Text.Json;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirJsonFunctionValueTests
{
    private const string Source = "import std.stdio\n"
        + "func <T> identity(value: T): T = value\n"
        + "func update(ref value: int, out result: int): () throws IOException { value = 1; result = 2 }\n"
        + "func relay(@escaping callback: (int) -> int throws IOException): (int) -> int throws IOException = callback\n"
        + "object Main { func main(args: []String): () throws IOException {\n"
        + "val echo: (String) -> String = identity; echo(\"sol\")\n"
        + "val forward: ((int) -> int throws IOException) -> (int) -> int throws IOException = relay\n"
        + "val callback: (ref int, out int) -> () throws IOException = update\n"
        + "val zero: () -> int = { 42 }\n"
        + "val anonymous: (() -> int) -> int = func(@escaping nested: () -> int): int = 0\n"
        + "var current = 0; var result: int; callback(ref current, out result)\n"
        + "} }";

    [Fact]
    public void FunctionReferencesAndIndirectCallsKeepTheirClosedJsonIdentities()
    {
        var verified = Compile();
        var program = verified.Program;
        string first = FirJson.Serialize(verified, TestContext.Current.CancellationToken);
        string second = FirJson.Serialize(verified, TestContext.Current.CancellationToken);
        Assert.Equal(first, second);

        using var json = JsonDocument.Parse(first);
        var root = json.RootElement;
        Assert.Equal(7, root.GetProperty("schema").GetInt32());
        Assert.True(root.GetProperty("verified").GetBoolean());

        foreach (var function in program.Functions)
        {
            var serializedFunction = Assert.Single(root.GetProperty("functions").EnumerateArray(), item =>
                item.GetProperty("id").GetInt32() == function.Id);
            var serializedSlots = serializedFunction.GetProperty("slots").EnumerateArray().ToArray();
            Assert.Equal(function.Slots.Length, serializedSlots.Length);
            foreach (var slot in function.Slots)
            {
                var serializedSlot = Assert.Single(serializedSlots, item =>
                    item.GetProperty("id").GetInt32() == slot.Id);
                Assert.Equal(slot.IsParameter, serializedSlot.GetProperty("isParameter").GetBoolean());
                Assert.Equal(slot.IsEscaping, serializedSlot.GetProperty("isEscaping").GetBoolean());
            }
        }

        var relay = program.Functions.Single(function => function.Name == "relay");
        Assert.True(Assert.Single(relay.Parameters).IsEscaping);
        var relayJson = Assert.Single(root.GetProperty("functions").EnumerateArray(), item =>
            item.GetProperty("id").GetInt32() == relay.Id);
        Assert.True(Assert.Single(relayJson.GetProperty("slots").EnumerateArray(), slot =>
            slot.GetProperty("isParameter").GetBoolean()).GetProperty("isEscaping").GetBoolean());

        var references = program.Functions.Where(function => function.Body is not null)
            .SelectMany(function => function.Body!.Statements).OfType<FirDeclare>()
            .Select(declaration => declaration.Initializer).OfType<FirFunctionReference>().ToArray();
        var serializedReferences = Nodes(root, "FirFunctionReference").ToArray();
        Assert.Equal(references.Length, serializedReferences.Length);
        foreach (var reference in references)
        {
            var node = Assert.Single(serializedReferences, item =>
                item.GetProperty("value").GetProperty("target").GetInt32() == reference.Target);
            Assert.Equal(reference.Type.Id, node.GetProperty("type").GetInt32());
            Assert.Equal(reference.Origin.Span.Start, node.GetProperty("start").GetInt32());
            Assert.Equal(reference.Origin.Span.Length, node.GetProperty("length").GetInt32());

            var target = Assert.Single(root.GetProperty("functions").EnumerateArray(), item =>
                item.GetProperty("id").GetInt32() == reference.Target);
            var targetArguments = target.GetProperty("specializationArguments").EnumerateArray()
                .Select(item => item.GetInt32()).ToArray();
            Assert.Equal(program.Functions[reference.Target].SpecializationArguments.Select(type => type.Id),
                targetArguments);
            var type = Type(root, reference.Type.Id);
            Assert.Equal("Function", type.GetProperty("kind").GetString());
            Assert.Equal(reference.Type.Arguments.Select(argument => argument.Id),
                type.GetProperty("arguments").EnumerateArray().Select(item => item.GetInt32()));
        }

        var calls = program.Functions.Where(function => function.Body is not null)
            .SelectMany(function => function.Body!.Statements).OfType<FirEvaluate>()
            .Select(evaluation => evaluation.Value).OfType<FirIndirectCall>().ToArray();
        var serializedCalls = Nodes(root, "FirIndirectCall").ToArray();
        Assert.Equal(calls.Length, serializedCalls.Length);
        foreach (var call in calls)
        {
            var node = Assert.Single(serializedCalls, item =>
                item.GetProperty("start").GetInt32() == call.Origin.Span.Start);
            Assert.Equal(call.Type.Id, node.GetProperty("type").GetInt32());
            var arguments = node.GetProperty("value").GetProperty("arguments").EnumerateArray().ToArray();
            Assert.Equal(call.Arguments.Length, arguments.Length);
            for (int index = 0; index < arguments.Length; index++)
            {
                Assert.Equal(call.Arguments[index].Parameter, arguments[index].GetProperty("parameter").GetInt32());
                Assert.Equal(call.Arguments[index].Mode.ToString(), arguments[index].GetProperty("mode").GetString());
            }
        }

        var updateReference = references.Single(reference =>
            program.Functions[reference.Target].Name == "update");
        var updateType = Type(root, updateReference.Type.Id);
        Assert.Equal(["Ref", "Out"], updateType.GetProperty("parameterModes").EnumerateArray()
            .Select(item => item.GetString()).ToArray());
        var effectIds = updateType.GetProperty("effects").EnumerateArray().Select(item => item.GetInt32()).ToArray();
        Assert.Single(effectIds);
        Assert.Equal("std.stdio.IOException", Type(root, effectIds[0]).GetProperty("name").GetString());

        var relayReference = references.Single(reference =>
            program.Functions[reference.Target].Name == "relay");
        var relayType = Type(root, relayReference.Type.Id);
        var nestedFunctions = relayType.GetProperty("arguments").EnumerateArray()
            .Select(argument => Type(root, argument.GetInt32())).ToArray();
        Assert.Equal(2, nestedFunctions.Length);
        Assert.All(nestedFunctions, nested => Assert.Equal("Function", nested.GetProperty("kind").GetString()));
        Assert.All(nestedFunctions, nested => Assert.Equal("std.stdio.IOException",
            Type(root, Assert.Single(nested.GetProperty("effects").EnumerateArray()).GetInt32())
                .GetProperty("name").GetString()));

        var entryFlow = Assert.Single(root.GetProperty("flow").EnumerateArray(), item =>
            item.GetProperty("functionId").GetInt32() == program.Entry);
        var flowCall = Assert.Single(entryFlow.GetProperty("blocks").EnumerateArray()
            .SelectMany(block => block.GetProperty("calls").EnumerateArray()), call =>
            call.GetProperty("target").ValueKind == JsonValueKind.Null
                && call.GetProperty("start").GetInt32() == calls.Single(value =>
                    value.Function.Type.Effects.Any()).Origin.Span.Start);
        Assert.Equal(effectIds, flowCall.GetProperty("effects").EnumerateArray()
            .Select(item => item.GetInt32()).ToArray());
    }

    [Fact]
    public void InspectionOutputIsValidJsonAndTruncatedDocumentsAreRejectedByAReader()
    {
        string output = FirJson.Serialize(Compile(), TestContext.Current.CancellationToken);
        using (JsonDocument.Parse(output)) { }
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(output[..^1]));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse("{\"schema\":7"));
    }

    private static VerifiedFirProgram Compile()
    {
        var result = FirCompilation.Create([AstTestSources.Parse(Source)], TestContext.Current.CancellationToken);
        Assert.True(result.Program is not null,
            string.Join('\n', result.Diagnostics.Select(diagnostic => diagnostic.Code + "@"
                + diagnostic.Span.Start + " " + diagnostic.Message)));
        return result.Program;
    }

    private static JsonElement Type(JsonElement root, int id) =>
        Assert.Single(root.GetProperty("types").EnumerateArray(), item => item.GetProperty("id").GetInt32() == id);

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
