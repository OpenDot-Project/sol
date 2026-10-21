// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;

namespace OpenDot.Sol.Lowering;

/// <summary>A deterministic, digest-checked library container for the closed, nongeneric FIR subset.</summary>
public static class SolLibraryArchive
{
    private const string Target = "x86_64-pc-windows-msvc";
    private const int MaximumBytes = 64 * 1024 * 1024;
    private static readonly Lazy<string> Identity = new(() =>
    {
        Assembly[] assemblies = [typeof(SolLibraryArchive).Assembly, typeof(VerifiedFirProgram).Assembly,
            typeof(AstSnapshot).Assembly, typeof(Analysis.BodyAnalysis).Assembly, typeof(Cst.SyntaxTree).Assembly,
            typeof(Psi.PsiSnapshot).Assembly, typeof(Text.SourceText).Assembly, typeof(Diagnostics.Diagnostic).Assembly];
        string inventory = string.Join("\n", assemblies.OrderBy(a => a.GetName().Name, StringComparer.Ordinal)
            .Select(a => a.GetName().Name + ":" + Digest(File.ReadAllBytes(a.Location))));
        return typeof(SolLibraryArchive).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion
            + ":" + Digest(Encoding.UTF8.GetBytes(inventory));
    });
    public static string CompilerId => Identity.Value;
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static byte[] Create(VerifiedFirProgram program, byte[] llvmBitcode, byte[] objectBytes, bool staticRuntime)
    {
        ArgumentNullException.ThrowIfNull(program); ArgumentNullException.ThrowIfNull(llvmBitcode); ArgumentNullException.ThrowIfNull(objectBytes);
        if (!program.Program.IsLibrary) { throw new ArgumentException("A Sol library cannot contain a program entry.", nameof(program)); }
        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        byte[] fir = Encoding.UTF8.GetBytes(FirJson.Serialize(program));
        string firPath = "fir/" + Digest(fir) + ".bin", objectPath = "objects/" + Target + "/" + Digest(objectBytes) + ".obj";
        entries.Add(firPath, fir); entries.Add(objectPath, objectBytes);
        entries.Add("llvm/module.bc", llvmBitcode);
        var modules = program.Program.Analysis.Sources.Declarations.Files.Where(f => !program.Program.Catalog.Files.Contains(f))
            .GroupBy(f => f.Root.ModuleName, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
        foreach (var module in modules)
        {
            string id = module.Key ?? throw new InvalidDataException("A library module requires a logical identity.");
            if (id.Split('.').Any(part => part.Length == 0 || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')))
            { throw new InvalidDataException("Invalid logical module identity."); }
            entries.Add("modules/" + id + "/metadata.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                module = id,
                exports = program.Program.Functions.Where(f => f.ExportName is not null && module.Any(s => s.Root.Origin.Source == f.Origin.Source))
                    .Select(f => new { name = f.ExportName, native_symbol = "sol_fn_" + f.Id, result = f.ReturnType.Name, parameters = f.Parameters.Select(p => new { p.Name, type = p.Type.Name, mode = p.Mode.ToString() }) })
                    .OrderBy(e => e.name, StringComparer.Ordinal),
            }));
        }
        entries.Add("provenance.json", JsonSerializer.SerializeToUtf8Bytes(new { compiler = CompilerId, llvm = "23.1.3", runtime_abi = 0, runtime_contract = RuntimeContract.Id, fir_encoding = "utf8-json", subset = "closed-nongeneric" }));
        entries.Add("manifest.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 2, compiler = CompilerId, edition = 2026, target = Target, llvm = "23.1.3", runtime_abi = 0, runtime_contract = RuntimeContract.Id,
            runtime_linkage = staticRuntime ? "static" : "dynamic", modules = modules.Select(m => m.Key),
            files = entries.Select(e => new { path = e.Key, size = e.Value.Length, sha256 = Digest(e.Value) }).ToArray(),
        }));
        if (entries.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
        { throw new InvalidDataException("Logical modules collide in the Windows library inventory."); }
        if (entries.Count > 4096 || entries.Values.Sum(bytes => (long)bytes.Length) > MaximumBytes)
        { throw new InvalidDataException("Sol library exceeds the inventory limits."); }
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in entries)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open(); stream.Write(bytes);
            }
        }
        if (output.Length > MaximumBytes) { throw new InvalidDataException("Sol library exceeds the archive size limit."); }
        return output.ToArray();
    }

    /// <summary>Reads data only. Importing never executes archive content or mints verified FIR.</summary>
    public static ImmutableDictionary<string, byte[]> Read(byte[] archive)
    {
        try { return ReadCore(archive); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException or FormatException)
        { throw new InvalidDataException("Malformed Sol library metadata.", error); }
    }

    private static ImmutableDictionary<string, byte[]> ReadCore(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.Length > MaximumBytes) { throw new InvalidDataException("Sol library exceeds the size limit."); }
        using var input = new MemoryStream(archive, writable: false);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var pathNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        if (zip.Entries.Count > 4096) { throw new InvalidDataException("Too many library entries."); }
        foreach (var entry in zip.Entries)
        {
            string path = entry.FullName;
            if (path.Contains('\\') || path.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.Contains(':')) || Path.IsPathRooted(path))
            { throw new InvalidDataException("Unsafe library path."); }
            total = checked(total + entry.Length);
            if (total > MaximumBytes) { throw new InvalidDataException("Expanded library exceeds the size limit."); }
            if (!pathNames.Add(path)) { throw new InvalidDataException("Duplicate or case-colliding library entry."); }
            using var stream = entry.Open();
            byte[] bytes = new byte[checked((int)entry.Length)]; stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) { throw new InvalidDataException("Library entry size mismatch."); }
            files.Add(path, bytes);
        }
        if (!files.TryGetValue("manifest.json", out var manifest)) { throw new InvalidDataException("Library manifest is missing."); }
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        var pending = new Stack<JsonElement>(); pending.Push(root);
        while (pending.TryPop(out var element))
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                { if (!names.Add(property.Name)) { throw new InvalidDataException("Duplicate JSON metadata key."); } pending.Push(property.Value); }
            }
            else if (element.ValueKind == JsonValueKind.Array) { foreach (var child in element.EnumerateArray()) { pending.Push(child); } }
        }
        if (root.GetProperty("schema").GetInt32() != 2 || root.GetProperty("compiler").GetString() != CompilerId
            || root.GetProperty("target").GetString() != Target || root.GetProperty("llvm").GetString() != "23.1.3"
            || root.GetProperty("runtime_abi").GetInt32() != 0 || root.GetProperty("runtime_contract").GetUInt64() != RuntimeContract.Id || root.GetProperty("edition").GetInt32() != 2026)
        { throw new InvalidDataException("Incompatible library schema, compiler, runtime, or target."); }
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manifest.json" };
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            string path = file.GetProperty("path").GetString() ?? throw new InvalidDataException("Missing file identity.");
            if (!declared.Add(path) || !files.TryGetValue(path, out var bytes) || file.GetProperty("size").GetInt64() != bytes.Length
                || file.GetProperty("sha256").GetString() != Digest(bytes)) { throw new InvalidDataException("Library inventory or digest mismatch."); }
        }
        if (declared.Count != files.Count || !files.Keys.Any(k => k.StartsWith("fir/", StringComparison.Ordinal))
            || !files.Keys.Any(k => k.StartsWith("objects/" + Target + "/", StringComparison.Ordinal))
            || !files.ContainsKey("provenance.json") || !files.ContainsKey("llvm/module.bc"))
        { throw new InvalidDataException("Incomplete or undeclared library inventory."); }
        var modules = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in root.GetProperty("modules").EnumerateArray())
        {
            string id = module.GetString() ?? throw new InvalidDataException("Missing module identity.");
            if (!modules.Add(id) || !files.ContainsKey("modules/" + id + "/metadata.json"))
            { throw new InvalidDataException("Missing or duplicate module metadata."); }
        }
        if (modules.Count == 0) { throw new InvalidDataException("A library requires a logical module."); }
        return files.ToImmutableDictionary(StringComparer.Ordinal);
    }
}
