// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenDot.Sol.Fuzz;

internal sealed record CorpusCase(string Id, string Family, string Expectation, string Sha256, byte[] Bytes);
internal sealed record CorpusShard(string File, string Family, string Sha256);

internal sealed class Corpus
{
    internal static readonly string[] FrontendFamilies =
    [
        "arithmetic", "control-flow", "declarations", "generic-types", "lambdas", "arguments",
        "modules", "properties", "numeric-boundaries", "string-escapes", "trivia", "truncation",
        "token-mutation", "delimiter-mutation", "unterminated", "invalid-utf8", "arbitrary-bytes",
        "nesting", "operators", "unicode-newlines",
    ];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal string Root { get; }
    internal string Target { get; }
    internal string[] Families { get; }
    internal string ManifestSha256 { get; }
    internal List<CorpusShard> Shards { get; } = [];

    internal Corpus(string root)
    {
        Root = Path.GetFullPath(root);
        byte[] bytes = ReadFile(Path.Combine(Root, "manifest.json"), 64 * 1024);
        ManifestSha256 = Hash(bytes);
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
        var manifest = document.RootElement;
        Fields(manifest, "schema_version", "target", "count", "max_input_bytes", "generator_sha256", "families", "shards");
        Target = Text(manifest, "target");
        Families = CorpusTargets.Families(Target);
        Require(manifest.GetProperty("schema_version").GetInt32() == 1 && manifest.GetProperty("count").GetInt32() == 20000 &&
            manifest.GetProperty("max_input_bytes").GetInt32() == 8192, "Unsupported corpus manifest.");
        Require(IsHash(Text(manifest, "generator_sha256")), "Invalid generator identity.");
        Require(manifest.GetProperty("families").EnumerateArray().Select(e => e.GetString()).SequenceEqual(Families),
            "Corpus family inventory changed.");
        var shards = manifest.GetProperty("shards").EnumerateArray().ToArray();
        Require(shards.Length == 40, "Expected 40 corpus shards.");
        for (int i = 0; i < shards.Length; i++)
        {
            var shard = shards[i];
            Fields(shard, "file", "count", "family", "sha256");
            string file = Text(shard, "file"), family = Text(shard, "family"), hash = Text(shard, "sha256");
            Require(file == $"shard-{i:D2}.jsonl" && family == Families[i / 2] &&
                shard.GetProperty("count").GetInt32() == 500 && IsHash(hash), "Invalid shard identity.");
            Shards.Add(new(file, family, hash));
        }
        string[] expected = ["manifest.json", .. Shards.Select(s => s.File)];
        var entries = Directory.GetFileSystemEntries(Root).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Require(entries.SequenceEqual(expected.Order(StringComparer.Ordinal)), "Unexpected corpus entries.");
    }

    internal List<CorpusCase> ReadShard(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Shards.Count);
        var shard = Shards[index];
        byte[] bytes = ReadFile(Path.Combine(Root, shard.File), 8 * 1024 * 1024);
        Require(Hash(bytes) == shard.Sha256, "Corpus shard digest mismatch.");
        using var reader = new StringReader(StrictUtf8.GetString(bytes));
        var result = new List<CorpusCase>(500);
        while (reader.ReadLine() is string line)
        {
            Require(result.Count < 500, "Too many corpus records.");
            var item = Decode(line, Target);
            Require(item.Id == $"case-{index * 500 + result.Count:D5}" && item.Family == shard.Family,
                "Corpus case sequence/family mismatch.");
            result.Add(item);
        }
        Require(result.Count == 500, "Incomplete corpus shard.");
        return result;
    }

    internal static CorpusCase Decode(string line, string target = "frontend-utf8-v1")
    {
        Require(line.Length is > 0 and <= 16384, "Corpus record size limit exceeded.");
        using var document = JsonDocument.Parse(line, new() { MaxDepth = 8 });
        var record = document.RootElement;
        Fields(record, "id", "family", "expectation", "sha256", "source_base64");
        string id = Text(record, "id"), family = Text(record, "family"), expectation = Text(record, "expectation");
        Require(id.Length == 10 && id.StartsWith("case-", StringComparison.Ordinal) &&
            int.TryParse(id.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int number) &&
            number < 20000 && id == $"case-{number:D5}", "Invalid case identity.");
        Require(CorpusTargets.Families(target).Contains(family, StringComparer.Ordinal) && expectation is "parse" or "reject" or "any",
            "Invalid corpus target contract.");
        Require(target == "frontend-utf8-v1" || expectation == "any", "Unsupported expectation for this fuzz target.");
        string hash = Text(record, "sha256"), encoded = Text(record, "source_base64");
        byte[] bytes = Convert.FromBase64String(encoded);
        Require(bytes.Length <= 8192 && Convert.ToBase64String(bytes) == encoded && IsHash(hash) && Hash(bytes) == hash,
            "Corpus source bytes/digest mismatch.");
        return new(id, family, expectation, hash, bytes);
    }

    internal static void Fields(JsonElement element, params string[] names)
    {
        Require(element.ValueKind == JsonValueKind.Object, "Expected a JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            Require(names.Contains(property.Name, StringComparer.Ordinal) && seen.Add(property.Name),
                "Unknown or duplicate corpus property.");
        }
        Require(seen.Count == names.Length, "Missing corpus property.");
    }

    internal static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new InvalidDataException("Null corpus string.");
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidDataException(message); }
    }
    private static byte[] ReadFile(string path, int maximum)
    {
        var file = new FileInfo(path);
        Require(file.Exists && file.Length is > 0 && file.Length <= maximum &&
            (file.Attributes & FileAttributes.ReparsePoint) == 0, "Invalid corpus file.");
        return File.ReadAllBytes(path);
    }
}
