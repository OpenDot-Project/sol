// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace OpenDot.Sol.Analysis;

/// <summary>Deterministic declaration-analysis inspection; does not serialize executable state.</summary>
public static class AnalysisJson
{
    public static string Serialize(AnalysisSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("distribution", session.Options.DistributionName);
            writer.WriteBoolean("hasErrors", session.HasErrors);
            writer.WriteStartArray("modules");
            foreach (var module in session.Modules.Values.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteString("name", module.Name);
                writer.WriteNumber("fileCount", module.Files.Length);
                writer.WriteStartArray("declarations");
                foreach (var symbol in module.Declarations) { writer.WriteNumberValue(symbol.Id); }
                writer.WriteEndArray();
                writer.WriteStartArray("dependencies");
                foreach (var dependency in session.Graph.Edges[module.Name]) { writer.WriteStringValue(dependency); }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("symbols");
            var pending = new Stack<DeclarationSymbol>(session.Modules.Values.OrderByDescending(m => m.Name, StringComparer.Ordinal)
                .SelectMany(m => m.Declarations.Reverse()));
            while (pending.TryPop(out var symbol))
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteNumber("id", symbol.Id);
                writer.WriteString("name", symbol.Name);
                writer.WriteString("qualified", symbol.QualifiedName);
                writer.WriteString("kind", symbol.Kind.ToString());
                writer.WriteString("signature", symbol.Signature);
                writer.WriteNumber("arity", symbol.Arity);
                writer.WriteNumber("parts", symbol.Parts.Length);
                writer.WriteStartArray("members");
                foreach (var member in symbol.Members) { writer.WriteNumberValue(member.Id); }
                writer.WriteEndArray();
                writer.WriteEndObject();
                foreach (var member in symbol.Members.Reverse()) { pending.Push(member); }
            }
            writer.WriteEndArray();
            writer.WriteStartArray("compilationGroups");
            foreach (var group in session.Graph.CompilationGroups)
            {
                writer.WriteStartArray();
                foreach (var name in group) { writer.WriteStringValue(name); }
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("diagnostics");
            foreach (var diagnostic in session.Diagnostics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteString("code", diagnostic.Code);
                writer.WriteString("message", diagnostic.Message);
                writer.WriteNumber("start", diagnostic.Span.Start);
                writer.WriteNumber("length", diagnostic.Span.Length);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
