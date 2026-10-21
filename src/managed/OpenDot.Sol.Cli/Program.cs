// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using OpenDot.Sol.Diagnostics;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Driver;
using OpenDot.Sol.Text;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Lowering;

namespace OpenDot.Sol.Cli;

internal static class Program
{
    private static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];

    public static int Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try { return Run(CompilerOptions.Parse(args), cancellation.Token); }
        catch (CommandLineException error) { Console.Error.WriteLine($"error: {error.Message}"); return 2; }
        catch (OperationCanceledException) { return 130; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Text.DecoderFallbackException)
        {
            Console.Error.WriteLine($"error SOL0002: {error.Message}");
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"internal compiler error: {error.GetType().Name}: {error.Message}");
            return 70;
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static int Run(CompilerOptions options, CancellationToken cancellation)
    {
        switch (options.Command)
        {
            case CompilerCommand.Help:
                Console.WriteLine($"""
                    Solc {Version} — Sol frontend for Windows
                    Usage: solc <SOURCE.sol>... [OPTIONS]

                    -h, --help                Show help
                    -V, --version             Show the product version
                    --verbose                 Show build capabilities with --version
                    --print KEY               edition-list or target-list
                    --explain CODE            Explain a frontend diagnostic
                    --emit=cst|ast|fir|llvm-ir|jit Source inspection or native ORC execution
                    -o PATH                   Output for one source
                    --out-dir DIR             Output directory; defaults to the current directory
                    --edition=2026            Source edition
                    --target TRIPLE           x86_64-pc-windows-msvc
                    --artifact-type TYPE      bin|cdylib|staticlib|solib
                    --static-solrt-lib        Select static SolRT linkage (default: dynamic)
                    -C llvm-compiler=aot|orc   Backend selection
                    --error-format human|json Diagnostic output on stderr
                    --error-limit N           Maximum diagnostics per source; default 100
                    --color auto|always|never  Diagnostic color policy

                    @FILE: UTF-8 response file, one argument per line. No subcommands.
                    Native AOT/isolated ORC support the verified subset; LLVM backend required.
                    CST/AST output does not imply type checking or executable code.
                    """);
                return 0;
            case CompilerCommand.Version:
                Console.WriteLine($"solc {Version}");
                if (options.Verbose) { Console.WriteLine("edition: 2026\ntarget: x86_64-pc-windows-msvc\nfrontend: CST, PSI, AST, verified FIR subset\ncodegen: native AOT and process-isolated ORC; scalar C libraries and closed solib subset; backend component required\nLLVM target version: 23.1.3"); }
                return 0;
            case CompilerCommand.Print:
                Console.WriteLine(options.Query switch
                {
                    "edition-list" => "2026",
                    "target-list" => "x86_64-pc-windows-msvc",
                    _ => throw new CommandLineException("--print supports edition-list or target-list in this build."),
                });
                return 0;
            case CompilerCommand.Explain:
                if (options.Query is not null && DiagnosticDescriptions.TryGetValue(options.Query, out string? description))
                {
                    Console.WriteLine($"{options.Query}: {description}");
                    return 0;
                }
                throw new CommandLineException("Unknown diagnostic code.");
        }
        if (options.Emit == EmitKind.Link && options.Backend != CompilerBackend.Aot)
        {
            WriteError("SOL0001", "ORC executes bin output with --emit=jit; linked artifacts use the AOT backend.", options);
            return 1;
        }

        var sources = new List<SourceText>();
        var outputs = new List<(string Path, byte[] Bytes)>();
        var inputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in options.Sources)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(source);
            if (!path.EndsWith(".sol", StringComparison.OrdinalIgnoreCase)) { throw new CommandLineException("Source files must use the .sol extension."); }
            if (!inputPaths.Add(path)) { throw new CommandLineException($"Duplicate source path: {source}"); }
            sources.Add(SourceText.FromBytes(File.ReadAllBytes(path), path));
        }
        var compilation = FrontendCompilation.Parse(sources, options.ErrorLimit, cancellation);
        foreach (Diagnostic diagnostic in compilation.Diagnostics) { WriteDiagnostic(diagnostic, options); }
        if (compilation.HasErrors) { return 1; }
        if (options.Emit is EmitKind.Link or EmitKind.Fir or EmitKind.LlvmIr or EmitKind.Jit)
        {
            var fir = FirCompilation.Create(compilation.CreateAst(cancellation), cancellation, library: options.Artifact != ArtifactType.Bin);
            foreach (var diagnostic in fir.Diagnostics) { WriteDiagnostic(diagnostic, options); }
            if (fir.Program is null) { return 1; }
            if (options.Emit == EmitKind.Jit)
            {
                foreach (var source in sources)
                { if (!File.ReadAllBytes(source.Path).AsSpan().SequenceEqual(source.Bytes)) { throw new IOException("Source changed during compilation."); } }
                return OrcExecutor.Execute(fir.Program, options.ProgramArguments, options.RuntimeLinkage == SolrtLinkage.Static, cancellationToken: cancellation);
            }
            string extension = options.Emit == EmitKind.Fir ? ".fir.json" : options.Emit == EmitKind.LlvmIr ? ".ll" : options.Artifact switch
            { ArtifactType.Bin => ".exe", ArtifactType.Cdylib => ".dll", ArtifactType.Staticlib => ".lib", ArtifactType.Solib => ".solib", _ => throw new InvalidOperationException() };
            string path = Path.GetFullPath(options.Output ?? Path.Combine(options.OutputDirectory, Path.GetFileNameWithoutExtension(sources[0].Path) + extension));
            if (options.Emit == EmitKind.Link && options.Artifact == ArtifactType.Cdylib && !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            { throw new CommandLineException("A cdylib output must use the .dll extension."); }
            if (inputPaths.Contains(path)) { throw new CommandLineException("An output cannot overwrite a source file."); }
            if (options.Emit == EmitKind.Fir) { outputs.Add((path, System.Text.Encoding.UTF8.GetBytes(FirJson.Serialize(fir.Program, cancellation)))); }
            else if (options.Emit == EmitKind.LlvmIr) { outputs.Add((path, System.Text.Encoding.UTF8.GetBytes(LlvmEmitter.Emit(fir.Program, options.RuntimeLinkage == SolrtLinkage.Static, cancellation)))); }
            else
            {
                var artifact = NativeCompiler.Compile(fir.Program, options.RuntimeLinkage == SolrtLinkage.Static, cancellationToken: cancellation,
                    kind: options.Artifact switch { ArtifactType.Bin => NativeArtifactKind.Bin, ArtifactType.Cdylib => NativeArtifactKind.Cdylib,
                        ArtifactType.Staticlib => NativeArtifactKind.Staticlib, ArtifactType.Solib => NativeArtifactKind.Solib, _ => throw new InvalidOperationException() },
                    artifactName: Path.GetFileName(path));
                outputs.Add((path, artifact.Executable));
                if (artifact.RuntimeDll is not null) { outputs.Add((Path.Combine(Path.GetDirectoryName(path)!, "SolRT.dll"), artifact.RuntimeDll)); }
                if (artifact.ImportLibrary is not null) { outputs.Add((Path.ChangeExtension(path, ".lib"), artifact.ImportLibrary)); }
                if (artifact.RuntimeLibrary is not null) { outputs.Add((Path.Combine(Path.GetDirectoryName(path)!, "SolRT.lib"), artifact.RuntimeLibrary)); }
            }
            foreach (var source in sources)
            { if (!File.ReadAllBytes(source.Path).AsSpan().SequenceEqual(source.Bytes)) { throw new IOException("Source changed during compilation."); } }
            var nativeOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var output in outputs)
            { if (!nativeOutputs.Add(output.Path) || inputPaths.Contains(output.Path)) { throw new CommandLineException("Native output paths collide with another output or source."); } }
            OutputTransaction.Write(outputs, cancellation);
            return 0;
        }
        var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in compilation.Trees)
        {
            string path = Path.GetFullPath(options.Output ?? Path.Combine(options.OutputDirectory,
                Path.GetFileNameWithoutExtension(tree.Source.Path) + (options.Emit == EmitKind.Ast ? ".ast.json" : ".cst.json")));
            if (inputPaths.Contains(path)) { throw new CommandLineException("An output cannot overwrite a source file."); }
            if (!outputPaths.Add(path)) { throw new CommandLineException("Frontend output paths collide. Use distinct source basenames."); }
            byte[] bytes = options.Emit == EmitKind.Ast
                ? System.Text.Encoding.UTF8.GetBytes(AstJson.Serialize(AstSnapshot.Create(PsiSnapshot.Create(tree, cancellation), cancellation), cancellation))
                : CstWriter.Serialize(tree);
            outputs.Add((path, bytes));
        }
        // Preflight the entire source set before creating or replacing any output.
        foreach (var source in sources)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!File.ReadAllBytes(source.Path).AsSpan().SequenceEqual(source.Bytes))
            {
                throw new IOException($"Source changed during compilation: {source.Path}");
            }
        }
        OutputTransaction.Write(outputs, cancellation);
        return 0;
    }

    private static void WriteDiagnostic(Diagnostic diagnostic, CompilerOptions options)
    {
        var position = diagnostic.Source.GetPosition(diagnostic.Span.Start);
        if (options.JsonErrors)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                schema_version = 1,
                origin = "compiler",
                code = diagnostic.Code,
                severity = diagnostic.Severity.ToString().ToLowerInvariant(),
                message = diagnostic.Message,
                path = diagnostic.Source.Path,
                byte_start = diagnostic.Span.Start,
                byte_length = diagnostic.Span.Length,
                line = position.Line + 1,
                column = position.ScalarColumn + 1,
                utf16_column = position.Utf16Column,
            }));
        }
        else { Console.Error.WriteLine($"{diagnostic.Source.Path}({position.Line + 1},{position.ScalarColumn + 1}): {ErrorLabel(options)} {diagnostic.Code}: {diagnostic.Message}"); }
    }

    private static void WriteError(string code, string message, CompilerOptions options)
    {
        if (options.JsonErrors) { Console.Error.WriteLine(JsonSerializer.Serialize(new { schema_version = 1, origin = "compiler", code, severity = "error", message })); }
        else { Console.Error.WriteLine($"{ErrorLabel(options)} {code}: {message}"); }
    }

    private static string ErrorLabel(CompilerOptions options) =>
        options.Color == "always" || (options.Color == "auto" && !Console.IsErrorRedirected
            && Environment.GetEnvironmentVariable("NO_COLOR") is null)
        ? "\u001b[31merror\u001b[0m" : "error";

    private static readonly Dictionary<string, string> DiagnosticDescriptions = new(StringComparer.Ordinal)
    {
        ["SOL0001"] = "The requested compiler stage is not implemented; no artifact was produced.",
        ["SOL0002"] = "A source, response file, or output could not be accessed.",
        ["SOL1001"] = "Sol source must contain well-formed UTF-8, including inside comments and literals.",
        ["SOL1002"] = "An unexpected character occurred outside a literal or comment. Identifiers are ASCII.",
        ["SOL1004"] = "NUL is not permitted outside string or rune literals.",
        ["SOL1005"] = "A block comment must end with */; block comments do not nest.",
        ["SOL1006"] = "A positional lambda parameter uses canonical decimal notation: $0, $1, and so on.",
        ["SOL1007"] = "Numeric digits must match the radix; underscores must separate digits; suffixes are not supported.",
        ["SOL1008"] = "A quoted literal must close on the same physical line.",
        ["SOL1009"] = "A rune literal contains exactly one Unicode scalar.",
        ["SOL1010"] = "An escape must use a supported spelling and a valid Unicode scalar value.",
        ["SOL1100"] = "Every source starts with module followed by a logical dotted name, independent of the file path.",
        ["SOL1101"] = "A required syntactic element is missing or not implemented by this frontend build.",
        ["SOL1102"] = "This syntax production is not implemented by the initial frontend; it is not silently accepted.",
        ["SOL1103"] = "A declaration or control-flow body brace must share its header's logical line; else must share the preceding closing brace's line.",
        ["SOL1104"] = "The parser nesting limit was reached; simplify the source nesting.",
    };
}
