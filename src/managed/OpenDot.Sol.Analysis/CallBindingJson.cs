// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

/// <summary>Inspection only. File indexes and local IDs are not a cache format, FIR, or public ABI.</summary>
public static class CallBindingJson
{
    public static string Serialize(CallAnalysis analysis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
        var files = new Dictionary<AstNode, int>(ReferenceEqualityComparer.Instance);
        writer.WriteStartObject();
        writer.WriteNumber("schema", 1);
        writer.WriteBoolean("hasErrors", analysis.HasErrors);
        writer.WriteBoolean("complete", analysis.IsComplete);
        writer.WriteStartArray("files");
        for (int index = 0; index < analysis.Sources.Declarations.Files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = analysis.Sources.Declarations.Files[index];
            foreach (var node in file.Nodes) { files.Add(node, index); }
            writer.WriteStartObject();
            writer.WriteNumber("id", index);
            writer.WriteString("module", file.Root.ModuleName);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("types");
        foreach (var type in analysis.Types.Types)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteNumber("id", type.Id);
            writer.WriteString("kind", type.Kind.ToString());
            writer.WriteString("name", type.Name);
            if (type.Declaration is not null) { writer.WriteNumber("declaration", type.Declaration.Id); }
            if (type.Parameter is { } parameter)
            {
                writer.WriteNumber("ownerFile", files[parameter.Owner]);
                writer.WriteNumber("ownerNode", parameter.Owner.Id);
                writer.WriteNumber("ordinal", parameter.Ordinal);
            }
            WriteIds(writer, "arguments", type.Arguments.Select(t => t.Id));
            WriteIds(writer, "effects", type.Effects.Select(t => t.Id));
            writer.WriteStartArray("modes");
            foreach (var mode in type.ParameterModes) { writer.WriteStringValue(mode.ToString()); }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("calls");
        foreach (var binding in analysis.Bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteNumber("file", files[binding.Call]);
            writer.WriteNumber("node", binding.Call.Id);
            writer.WriteNumber("start", binding.Call.Origin.Span.Start);
            writer.WriteString("status", binding.Status.ToString());
            writer.WriteString("reason", binding.Reason);
            if (binding.Selected is not null) { writer.WriteNumber("selected", binding.Selected.Symbol.Id); }
            writer.WriteStartArray("candidates");
            foreach (var candidate in binding.Candidates)
            {
                writer.WriteStartObject();
                writer.WriteNumber("symbol", candidate.Symbol.Id);
                writer.WriteString("status", candidate.Status.ToString());
                writer.WriteString("reason", candidate.Reason);
                WriteIds(writer, "typeArguments", candidate.TypeArguments.Select(t => t.Id));
                WriteIds(writer, "parameterTypes", candidate.ParameterTypes.Select(t => t.Id));
                WriteIds(writer, "effects", candidate.Effects.Select(t => t.Id));
                WriteIds(writer, "defaults", candidate.DefaultParameters);
                WriteIds(writer, "ranks", candidate.ConversionRanks);
                writer.WriteBoolean("expandsVararg", candidate.ExpandsVararg);
                if (candidate.ReturnType is not null) { writer.WriteNumber("returnType", candidate.ReturnType.Id); }
                writer.WriteStartArray("arguments");
                foreach (var argument in candidate.Arguments)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("source", argument.ArgumentIndex);
                    writer.WriteNumber("parameter", argument.ParameterIndex);
                    writer.WriteBoolean("expanded", argument.IsExpandedVararg);
                    writer.WriteBoolean("spread", argument.IsSpread);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteIds(Utf8JsonWriter writer, string name, IEnumerable<int> values)
    {
        writer.WriteStartArray(name);
        foreach (int value in values) { writer.WriteNumberValue(value); }
        writer.WriteEndArray();
    }
}
