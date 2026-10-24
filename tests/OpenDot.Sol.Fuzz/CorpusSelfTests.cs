// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenDot.Sol.Fuzz;

internal static class CorpusSelfTests
{
    internal static void Run()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("module self\nval value = 1\n");
        var valid = new JsonObject
        {
            ["id"] = "case-00000", ["family"] = "arithmetic", ["expectation"] = "parse",
            ["sha256"] = Corpus.Hash(bytes), ["source_base64"] = Convert.ToBase64String(bytes),
        };
        var mutations = new Dictionary<string, Action<JsonObject>>
        {
            ["missing identity"] = v => v.Remove("id"),
            ["wrong identity type"] = v => v["id"] = 1,
            ["out-of-range identity"] = v => v["id"] = "case-20000",
            ["identity traversal"] = v => v["id"] = "../case-00000",
            ["unknown family"] = v => v["family"] = "unknown",
            ["unknown expectation"] = v => v["expectation"] = "ignore-errors",
            ["null source"] = v => v["source_base64"] = null,
            ["invalid base64"] = v => v["source_base64"] = "!",
            ["noncanonical base64"] = v => v["source_base64"] = Convert.ToBase64String(bytes) + "\n",
            ["changed source bytes"] = v => v["source_base64"] = Convert.ToBase64String([1, 2, 3]),
            ["wrong digest"] = v => v["sha256"] = new string('0', 64),
            ["wrong digest type"] = v => v["sha256"] = new JsonArray("a", "b"),
            ["unknown property"] = v => v["extra"] = true,
            ["oversized input"] = v =>
            {
                byte[] large = new byte[8193];
                v["source_base64"] = Convert.ToBase64String(large);
                v["sha256"] = Corpus.Hash(large);
            },
        };
        int count = 0;
        foreach (var mutation in mutations)
        {
            var value = (JsonObject)valid.DeepClone();
            mutation.Value(value);
            Reject(mutation.Key, () => Corpus.Decode(value.ToJsonString()));
            count++;
        }
        string duplicate = valid.ToJsonString().Insert(1, "\"id\":\"case-00000\",");
        Reject("duplicate property", () => Corpus.Decode(duplicate));
        Reject("oversized record", () => Corpus.Decode(new string(' ', 16385)));
        Corpus.Require(!FrontendOracle.Check(Corpus.Decode(valid.ToJsonString())), "Valid seed self-test failed.");
        byte[] invalid = [0xFF];
        valid["source_base64"] = Convert.ToBase64String(invalid);
        valid["sha256"] = Corpus.Hash(invalid);
        valid["expectation"] = "reject";
        Corpus.Require(FrontendOracle.Check(Corpus.Decode(valid.ToJsonString())), "Invalid UTF-8 self-test failed.");
        Console.WriteLine($"Corpus validator self-test: {count + 2} rejected corrupt records and valid/invalid frontend positives.");
    }

    private static void Reject(string name, Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or JsonException or FormatException or InvalidOperationException)
        {
            return;
        }
        throw new InvalidDataException($"Corrupt corpus record was accepted: {name}");
    }
}
