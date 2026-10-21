// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using OpenDot.Sol.Cli;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class RuntimeAndLibraryTests
{
    private static VerifiedFirProgram Program(string body, bool library = false)
    {
        string source = library ? body : "import std.stdio\nobject Main { func main(args: []String) throws IOException { " + body + " } }";
        var compilation = FirCompilation.Create([AstTestSources.Parse(source)], TestContext.Current.CancellationToken, library);
        Assert.True(compilation.Program is not null, string.Join("\n", compilation.Diagnostics.Select(d => d.Message)));
        return compilation.Program;
    }

    [Theory]
    [InlineData("val text = args[0] + \"!\"; Gc.collect(); println(text.length); println(text.substring(0, 1)); println(text.indexOf(\"!\", 0)); println(text.compareOrdinal(text)); println(text.hashCode())")]
    [InlineData("val values = [1, 2, 3]; Gc.collect(); println(values[1], values.length)")]
    [InlineData("val values = [\"a\" + \"b\", args[0]]; Gc.collect(); println(values[0], values[1])")]
    [InlineData("println(args[0].substring(length = 1, start = 0)); println(args[0].indexOf(start = 0, value = \"a\"))")]
    public void RootedArraysAndStringOperationsHaveVerifiedNativeLowering(string body)
    {
        var program = Program(body);
        string ir = LlvmEmitter.Emit(program, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("@sol_gc_push_frame", ir);
        Assert.Contains("@sol_gc_pop_frame", ir);
        Assert.Contains("@sol_gc_store_ref", ir);
        Assert.Contains("@sol_gc_collect", ir);
        Assert.DoesNotContain("$ROOT_COUNT$", ir);
        Assert.DoesNotContain("coreclr", ir);
    }

    [Fact]
    public void LibraryHasNoEntryAndCWrappersHaveExplicitRuntimeTransitions()
    {
        var library = Program("func add(a, b: int): int = a + b", library: true);
        Assert.True(library.Program.IsLibrary);
        Assert.Equal(-1, library.Program.Entry);
        string ir = LlvmEmitter.Emit(library, cancellationToken: TestContext.Current.CancellationToken, cExports: true);
        Assert.DoesNotContain("define i32 @wmain", ir);
        Assert.Contains("define dllexport i32 @add", ir);
        Assert.Contains("call i32 @sol_gc_enter_export()", ir);
        Assert.Contains("call void @sol_gc_leave_export(i32 %transition)", ir);
        Assert.Contains("@sol_library_shutdown", ir);
        Assert.DoesNotContain("define dllexport ptr", ir);
    }

    [Theory]
    [InlineData("func text(value: String): String = value")]
    [InlineData("func choose(value: bool): bool = value")]
    [InlineData("func add(value: int): int = value; func add(value: long): long = value")]
    [InlineData("func sol_rt_check_abi(value: long): long = value")]
    [InlineData("func memset(value: int): int = value")]
    public void CExportsRejectManagedSignaturesAndAmbiguousNames(string source)
    { Assert.Throws<IOException>(() => LlvmEmitter.Emit(Program(source, library: true), cancellationToken: TestContext.Current.CancellationToken, cExports: true)); }

    [Fact]
    public void SolibIsDeterministicInventoriedDataAndDoesNotGrantVerifiedFir()
    {
        var library = Program("func add(a, b: int): int = a + b", library: true);
        string ir = LlvmEmitter.Emit(library, cancellationToken: TestContext.Current.CancellationToken);
        byte[] first = SolLibraryArchive.Create(library, ir, [0x64, 0x86, 0, 0], false);
        Assert.Equal(first, SolLibraryArchive.Create(library, ir, [0x64, 0x86, 0, 0], false));
        var files = SolLibraryArchive.Read(first);
        Assert.Contains("manifest.json", files.Keys);
        Assert.Contains("provenance.json", files.Keys);
        Assert.Contains(files.Keys, key => key.StartsWith("objects/", StringComparison.Ordinal));
        Assert.Contains(files.Keys, key => key.StartsWith("fir/", StringComparison.Ordinal));
        using var bytes = new MemoryStream();
        bytes.Write(first);
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry("llvm/module.ll")!.Delete();
            using var stream = zip.CreateEntry("llvm/module.ll").Open(); stream.Write(Encoding.UTF8.GetBytes("tampered"));
        }
        Assert.Throws<InvalidDataException>(() => SolLibraryArchive.Read(bytes.ToArray()));
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("/absolute")]
    [InlineData("objects\\escape")]
    [InlineData("object:stream")]
    public void LibraryRejectsUnsafePathsBeforeReadingManifest(string path)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true)) { zip.CreateEntry(path); }
        Assert.Throws<InvalidDataException>(() => SolLibraryArchive.Read(bytes.ToArray()));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema\":1,\"schema\":1}")]
    [InlineData("[]")]
    [InlineData("{\"schema\":18446744073709551616}")]
    public void MalformedManifestIsADataError(string manifest)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        { using var stream = zip.CreateEntry("manifest.json").Open(); stream.Write(Encoding.UTF8.GetBytes(manifest)); }
        Assert.Throws<InvalidDataException>(() => SolLibraryArchive.Read(bytes.ToArray()));
    }

    [Fact]
    public void LibraryEntryCaseMustMatchItsInventoryAndReservedNames()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        { using var stream = zip.CreateEntry("MANIFEST.JSON").Open(); stream.Write(Encoding.UTF8.GetBytes("{}")); }
        Assert.Throws<InvalidDataException>(() => SolLibraryArchive.Read(bytes.ToArray()));
        using var duplicate = new MemoryStream();
        using (var zip = new ZipArchive(duplicate, ZipArchiveMode.Create, leaveOpen: true))
        { zip.CreateEntry("fir/a.bin"); zip.CreateEntry("FIR/A.bin"); }
        Assert.Throws<InvalidDataException>(() => SolLibraryArchive.Read(duplicate.ToArray()));
    }

    [Fact]
    public void OrcOwnsOnlyArgumentsAfterSeparatorAndDoesNotProduceLinkedArtifacts()
    {
        var options = CompilerOptions.Parse(["Main.sol", "-Cllvm-compiler=orc", "--", "Alice", "20", "@literal", "--help"]);
        Assert.Equal(EmitKind.Jit, options.Emit);
        Assert.Equal(["Alice", "20", "@literal", "--help"], options.ProgramArguments);
        Assert.Equal("Main.sol", Assert.Single(options.Sources));
        Assert.Throws<CommandLineException>(() => CompilerOptions.Parse(["Main.sol", "--emit=jit"]));
        Assert.Throws<CommandLineException>(() => CompilerOptions.Parse(["Main.sol", "-Cllvm-compiler=orc", "--artifact-type=solib"]));
        Assert.Equal(EmitKind.LlvmIr, CompilerOptions.Parse(["Main.sol", "-Cllvm-compiler=orc", "--emit=llvm-ir"]).Emit);
    }
}
