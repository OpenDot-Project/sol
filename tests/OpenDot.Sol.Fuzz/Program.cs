// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace OpenDot.Sol.Fuzz;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "self-test") { CorpusSelfTests.Run(); return 0; }
            if (args.Length == 3 && args[0] == "verify") { Verify(new Corpus(args[1]), args[2]); return 0; }
            if (args.Length == 3 && args[0] == "verify-all") { VerifyAll(args[1], args[2]); return 0; }
            if (args.Length is 5 or 6 && args[0] == "pack")
            {
                Pack(new Corpus(args[1]), int.Parse(args[2], CultureInfo.InvariantCulture), args[3], args[4], args.Length == 6 ? args[5] : null);
                return 0;
            }
            if (args.Length is 5 or 6 && args[0] == "replay")
            {
                Replay(new Corpus(args[1]), int.Parse(args[2], CultureInfo.InvariantCulture), args[3], args[4],
                    args.Length == 6 ? args[5] : null);
                return 0;
            }
            Console.Error.WriteLine("Usage: self-test | verify/verify-all <corpus> <report> | replay <corpus> <shard> <report> <journal> [case-id] | pack <corpus> <shard> <pack> <report> [case-id]");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Verify(Corpus corpus, string report)
    {
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var counts = corpus.Families.ToDictionary(f => f, _ => 0, StringComparer.Ordinal);
        int total = 0;
        for (int i = 0; i < corpus.Shards.Count; i++)
        {
            foreach (var item in corpus.ReadShard(i))
            {
                Corpus.Require(sources.Add(item.Sha256), "Duplicate source bytes in corpus.");
                counts[item.Family]++;
                total++;
            }
        }
        Corpus.Require(total == 20000 && counts.Values.All(n => n == 1000), "Corpus target distribution mismatch.");
        WriteReport(report, new { schema_version = 1, target = corpus.Target, corpus_sha256 = corpus.ManifestSha256,
            count = total, unique_sources = sources.Count, families = counts, status = "verified" });
        Console.WriteLine($"Verified {total} unique {corpus.Target} inputs across {corpus.Shards.Count} shards.");
    }

    private static void VerifyAll(string root, string report)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in new[] { "frontend", "semantic", "runtime", "backend", "gc" })
        {
            var corpus = new Corpus(Path.Combine(root, name));
            for (int shard = 0; shard < corpus.Shards.Count; shard++)
            {
                foreach (var item in corpus.ReadShard(shard))
                {
                    Corpus.Require(hashes.Add(item.Sha256), "Duplicate input bytes across fuzz targets.");
                }
            }
        }
        Corpus.Require(hashes.Count == 100000, "Expected 100000 distinct corpus inputs.");
        WriteReport(report, new { schema_version = 1, count = hashes.Count, targets = 5, status = "verified" });
        Console.WriteLine("Verified 100000 unique inputs across five targets.");
    }

    private static void Pack(Corpus corpus, int shard, string path, string report, string? caseId)
    {
        byte code = CorpusTargets.NativeCode(corpus.Target);
        var items = corpus.ReadShard(shard);
        if (caseId is not null) { items = items.Where(c => c.Id == caseId).ToList(); Corpus.Require(items.Count == 1, "Native case is absent from shard."); }
        using (var writer = new BinaryWriter(File.Create(path), Encoding.UTF8, false))
        {
        writer.Write(Encoding.ASCII.GetBytes("SFZP0001"));
        writer.Write(items.Count);
        writer.Write(int.Parse(items[0].Id.AsSpan(5), CultureInfo.InvariantCulture));
        foreach (var item in items)
        {
            Corpus.Require(item.Bytes.Length == 64 && item.Bytes.AsSpan(0, 4).SequenceEqual("SFZ1"u8) &&
                item.Bytes[4] == code && item.Bytes[5] == shard / 2 && item.Bytes[6] == 1 && item.Bytes[7] == 0,
                "Invalid native trace envelope.");
            writer.Write(item.Bytes);
            writer.Write(Convert.FromHexString(item.Sha256));
        }
        writer.Flush();
        }
        WriteReport(report, new { schema_version = 1, corpus_sha256 = corpus.ManifestSha256, shard, count = items.Count,
            pack_sha256 = Corpus.Hash(File.ReadAllBytes(path)), status = "packed" });
    }

    private static void Replay(Corpus corpus, int shard, string report, string journal, string? caseId)
    {
        var cases = corpus.ReadShard(shard);
        if (caseId is not null)
        {
            cases = cases.Where(c => c.Id == caseId).ToList();
            Corpus.Require(cases.Count == 1, "Requested case is absent from shard.");
        }
        using var progress = new StreamWriter(journal, false, new UTF8Encoding(false)) { AutoFlush = true };
        var timer = Stopwatch.StartNew();
        int rejected = 0;
        foreach (var item in cases)
        {
            // Flush before entering the parser so hard timeout/crash triage retains the active input.
            progress.WriteLine($"{item.Id} {item.Sha256}");
            try
            {
                bool diagnosed = corpus.Target switch
                {
                    "frontend-utf8-v1" => FrontendOracle.Check(item),
                    "semantic-modules-v1" => SemanticOracle.Check(item),
                    _ => throw new InvalidDataException("Native traces must run in the isolated native worker."),
                };
                if (diagnosed) { rejected++; }
            }
            catch (Exception error) { throw new InvalidDataException($"{corpus.Target} invariant failed for {item.Id} ({item.Family}, {item.Sha256}).", error); }
        }
        WriteReport(report, new { schema_version = 1, corpus_sha256 = corpus.ManifestSha256, shard,
            count = cases.Count, diagnosed = rejected, elapsed_ms = timer.ElapsedMilliseconds, status = "passed" });
        Console.WriteLine($"Shard {shard:D2}: {cases.Count} inputs passed, {rejected} produced diagnostics.");
    }

    private static void WriteReport(string path, object report) =>
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
}
