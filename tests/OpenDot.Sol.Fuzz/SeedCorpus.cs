// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenDot.Sol.Fuzz;

internal static class SeedCorpus
{
    internal static void Create(string corpusPath, string outputPath, int limit)
    {
        Corpus corpus = new(corpusPath);
        Corpus.Require(limit is > 0 and <= 20000, "Seed limit must be between 1 and 20000.");
        string root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "out", "fuzz", "campaigns"));
        string output = Path.GetFullPath(outputPath);
        Corpus.Require(output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "Seed output must remain under out/fuzz/campaigns.");
        Directory.CreateDirectory(root);
        Corpus.Require((File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "Seed output root cannot be a reparse point.");
        Corpus.Require(!Directory.Exists(output) && !File.Exists(output), "Seed output directory already exists.");
        Directory.CreateDirectory(output);

        var inputs = corpus.Families.ToDictionary(name => name, _ => new List<byte[]>(), StringComparer.Ordinal);
        var hashes = corpus.Families.ToDictionary(name => name, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        for (int shard = 0; shard < corpus.Shards.Count; shard++)
        {
            foreach (CorpusCase item in corpus.ReadShard(shard))
            {
                IEnumerable<byte[]> candidates = corpus.Target == "semantic-modules-v1"
                    ? SemanticSources(item.Bytes)
                    : [item.Bytes];
                foreach (byte[] bytes in candidates)
                {
                    string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    if (hashes[item.Family].Add(hash)) { inputs[item.Family].Add(bytes); }
                }
            }
        }

        int perFamily = limit / corpus.Families.Length;
        int remainder = limit % corpus.Families.Length;
        int written = 0;
        for (int familyIndex = 0; familyIndex < corpus.Families.Length; familyIndex++)
        {
            string family = corpus.Families[familyIndex];
            List<byte[]> candidates = inputs[family];
            int take = Math.Min(candidates.Count, perFamily + (familyIndex < remainder ? 1 : 0));
            for (int index = 0; index < take; index++)
            {
                byte[] bytes = candidates[index * candidates.Count / take];
                string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                File.WriteAllBytes(Path.Combine(output, hash), bytes);
                written++;
            }
        }

        Corpus.Require(written > 0 && written <= limit, "Seed selection produced no inputs or exceeded its limit.");
        Console.WriteLine($"Created {written} unique {corpus.Target} seeds across {corpus.Families.Length} families.");
    }

    private static IEnumerable<byte[]> SemanticSources(byte[] input)
    {
        using JsonDocument document = JsonDocument.Parse(input, new() { MaxDepth = 8 });
        JsonElement files = document.RootElement.GetProperty("files");
        foreach (JsonElement file in files.EnumerateArray())
        {
            string source = file.GetProperty("source").GetString() ?? string.Empty;
            yield return Encoding.UTF8.GetBytes(source);
        }
    }
}
