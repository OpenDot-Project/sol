// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenDot.Sol.Cli;

public sealed class CommandLineException(string message) : Exception(message);

public enum CompilerCommand { Compile, Help, Version, Print, Explain }
public enum CompilerBackend { Aot, Orc }
public enum ArtifactType { Bin, Cdylib, Staticlib, Solib }
public enum EmitKind { Cst, Link, Ast, Fir, LlvmIr }
public enum SolrtLinkage { Dynamic, Static }

public sealed record CompilerOptions(CompilerCommand Command, ImmutableArray<string> Sources,
    EmitKind Emit, CompilerBackend Backend, ArtifactType Artifact, string? Output,
    string OutputDirectory, bool JsonErrors, int ErrorLimit, string? Query, bool Verbose, string Color)
{
    public SolrtLinkage RuntimeLinkage { get; init; } = SolrtLinkage.Dynamic;

    public static CompilerOptions Parse(IEnumerable<string> arguments)
    {
        var args = Expand(arguments);
        var sources = ImmutableArray.CreateBuilder<string>();
        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        CompilerCommand command = CompilerCommand.Compile;
        string? query = null;
        bool verbose = false;
        bool literal = false;
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            if (literal) { sources.Add(arg); continue; }
            if (arg == "--") { literal = true; continue; }
            string option = arg;
            string? value = null;
            int equal = arg.IndexOf('=');
            if (equal >= 0) { option = arg[..equal]; value = arg[(equal + 1)..]; }
            if (option is "-h" or "--help" or "-V" or "--version" or "--verbose")
            {
                if (value is not null) { throw new CommandLineException($"{option} does not accept a value."); }
                if (option == "--verbose") { verbose = true; continue; }
                SetCommand(option is "-h" or "--help" ? CompilerCommand.Help : CompilerCommand.Version);
                continue;
            }
            if (option is "--print" or "--explain")
            {
                string text = Value();
                SetCommand(option == "--print" ? CompilerCommand.Print : CompilerCommand.Explain);
                if (query is not null && query != text) { throw new CommandLineException($"Conflicting {option} values."); }
                query = text;
                continue;
            }
            if (option == "--static-solrt-lib")
            {
                if (value is not null) { throw new CommandLineException("--static-solrt-lib does not accept a value."); }
                SetScalar(option, "true");
                continue;
            }
            if (option is "--emit" or "--artifact-type" or "--edition" or "--target" or "--error-format"
                or "--error-limit" or "--out-dir" or "-o" or "--color")
            {
                SetScalar(option, Value());
                continue;
            }
            if (option == "-C" || option.StartsWith("-C", StringComparison.Ordinal))
            {
                string setting = option.Length > 2 ? arg[2..] : Value();
                int separator = setting.IndexOf('=');
                if (separator <= 0) { throw new CommandLineException("Expected -C KEY=VALUE."); }
                string key = setting[..separator];
                if (key != "llvm-compiler") { throw new CommandLineException($"Codegen option '{key}' is not available in this build."); }
                SetScalar("llvm-compiler", setting[(separator + 1)..]);
                continue;
            }
            if (arg.StartsWith('-')) { throw new CommandLineException($"Unknown option '{arg}'."); }
            if (!arg.EndsWith(".sol", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandLineException($"Expected a .sol source path, not a subcommand: '{arg}'.");
            }
            sources.Add(arg);
            continue;

            string Value()
            {
                if (value is not null)
                {
                    if (value.Length == 0) { throw new CommandLineException($"{option} requires a nonempty value."); }
                    return value;
                }
                if (++index >= args.Count || args[index].Length == 0 || args[index].StartsWith('-'))
                {
                    throw new CommandLineException($"{option} requires a value.");
                }
                return args[index];
            }
        }

        if (command == CompilerCommand.Compile && sources.Count == 0)
        {
            throw new CommandLineException("No source files were provided. Use --help for usage.");
        }
        if (command != CompilerCommand.Compile && (sources.Count != 0 || scalars.Count != 0))
        {
            throw new CommandLineException("An informational command cannot be combined with compilation inputs or options.");
        }
        string emit = Get("--emit", "link");
        string backend = Get("llvm-compiler", "aot");
        string artifact = Get("--artifact-type", "bin");
        string errorFormat = Get("--error-format", "human");
        if (Get("--edition", "2026") != "2026") { throw new CommandLineException("Only edition 2026 is supported."); }
        if (Get("--target", "x86_64-pc-windows-msvc") != "x86_64-pc-windows-msvc")
        {
            throw new CommandLineException("Only x86_64-pc-windows-msvc is supported.");
        }
        if (Get("--color", "auto") is not ("auto" or "always" or "never")) { throw new CommandLineException("Invalid --color value."); }
        if (errorFormat is not ("human" or "json")) { throw new CommandLineException("Invalid --error-format value."); }
        if (!int.TryParse(Get("--error-limit", "100"), NumberStyles.None, CultureInfo.InvariantCulture, out int limit) || limit < 1)
        {
            throw new CommandLineException("--error-limit requires a positive integer.");
        }
        if (scalars.ContainsKey("-o") && sources.Count != 1 && emit is "cst" or "ast") { throw new CommandLineException("-o requires exactly one source for CST/AST output."); }
        return new(command, sources.ToImmutable(), emit switch
        {
            "cst" => EmitKind.Cst,
            "ast" => EmitKind.Ast,
            "fir" => EmitKind.Fir,
            "llvm-ir" => EmitKind.LlvmIr,
            "link" => EmitKind.Link,
            _ => throw new CommandLineException($"Emission '{emit}' is not available in this build."),
        }, backend switch
        {
            "aot" => CompilerBackend.Aot,
            "orc" => CompilerBackend.Orc,
            _ => throw new CommandLineException("-C llvm-compiler must be aot or orc."),
        }, artifact switch
        {
            "bin" => ArtifactType.Bin,
            "cdylib" => ArtifactType.Cdylib,
            "staticlib" => ArtifactType.Staticlib,
            "solib" => ArtifactType.Solib,
            _ => throw new CommandLineException("--artifact-type must be bin, cdylib, staticlib, or solib."),
        }, scalars.GetValueOrDefault("-o"), Get("--out-dir", Directory.GetCurrentDirectory()),
            errorFormat == "json", limit, query, verbose, Get("--color", "auto"))
        {
            RuntimeLinkage = scalars.ContainsKey("--static-solrt-lib") ? SolrtLinkage.Static : SolrtLinkage.Dynamic,
        };

        void SetCommand(CompilerCommand next)
        {
            if (command != CompilerCommand.Compile && command != next)
            {
                throw new CommandLineException("Informational commands cannot be combined.");
            }
            command = next;
        }
        void SetScalar(string key, string value)
        {
            if (scalars.TryGetValue(key, out string? previous) && previous != value)
            {
                throw new CommandLineException($"Conflicting values for {key}.");
            }
            scalars[key] = value;
        }
        string Get(string key, string fallback) => scalars.GetValueOrDefault(key, fallback);
    }

    private static List<string> Expand(IEnumerable<string> arguments)
    {
        var result = new List<string>();
        bool literal = false;
        foreach (string argument in arguments)
        {
            if (argument == "--") { literal = true; result.Add(argument); }
            else if (literal) { result.Add(argument); }
            else if (argument.StartsWith("@@", StringComparison.Ordinal)) { result.Add(argument[1..]); }
            else if (argument.StartsWith('@'))
            {
                string text = File.ReadAllText(argument[1..], new UTF8Encoding(false, true));
                foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                {
                    if (line.Length == 0) { continue; }
                    if (line.StartsWith('@')) { throw new CommandLineException("Nested response files are not supported."); }
                    result.Add(line);
                }
            }
            else { result.Add(argument); }
        }
        return result;
    }
}
