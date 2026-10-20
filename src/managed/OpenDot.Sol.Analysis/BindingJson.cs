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

/// <summary>Flat inspection of session-local types and lexical bindings; not an executable serialization.</summary>
public static class BindingJson
{
    public static string Serialize(SourceAnalysis analysis, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schema", 1);
        writer.WriteBoolean("hasErrors", analysis.HasErrors);
        var files = new Dictionary<AstNode, int>(ReferenceEqualityComparer.Instance);
        writer.WriteStartArray("files");
        for (int index = 0; index < analysis.Declarations.Files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = analysis.Declarations.Files[index];
            foreach (var node in file.Nodes) { files.Add(node, index); }
            writer.WriteStartObject();
            writer.WriteNumber("id", index);
            writer.WriteString("module", file.Root.ModuleName);
            writer.WriteNumber("length", file.Psi.Source.Length);
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
            writer.WriteStartArray("arguments");
            foreach (var argument in type.Arguments) { writer.WriteNumberValue(argument.Id); }
            writer.WriteEndArray();
            writer.WriteStartArray("modes");
            foreach (var mode in type.ParameterModes) { writer.WriteStringValue(mode.ToString()); }
            writer.WriteEndArray();
            writer.WriteStartArray("effects");
            foreach (var effect in type.Effects) { writer.WriteNumberValue(effect.Id); }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("scopes");
        foreach (var scope in analysis.Lexical.Scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteNumber("id", scope.Id);
            writer.WriteNumber("file", files[scope.Owner]);
            if (scope.ParentId is int parent) { writer.WriteNumber("parent", parent); }
            writer.WriteString("module", scope.Owner.Origin.Element.Snapshot.Root.ModuleName);
            writer.WriteNumber("start", scope.Owner.Origin.Span.Start);
            writer.WriteNumber("length", scope.Owner.Origin.Span.Length);
            writer.WriteStartArray("locals");
            foreach (var local in scope.Symbols)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", local.Id);
                writer.WriteString("name", local.Name);
                writer.WriteString("kind", local.Kind.ToString());
                writer.WriteNumber("visibleFrom", local.VisibleFrom);
                writer.WriteBoolean("mutable", local.IsMutable);
                if (local.WrittenType is not null)
                {
                    writer.WriteNumber("type", analysis.Types.GetType(local.WrittenType).Id);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("references");
        foreach (var binding in analysis.Lexical.Bindings.OrderBy(b => b.Reference.Origin.Source.Path, StringComparer.Ordinal)
            .ThenBy(b => b.Reference.Origin.Span.Start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteString("module", binding.Reference.Origin.Element.Snapshot.Root.ModuleName);
            writer.WriteNumber("file", files[binding.Reference]);
            writer.WriteNumber("start", binding.Reference.Origin.Span.Start);
            writer.WriteString("name", binding.Reference.Spelling);
            writer.WriteBoolean("capture", binding.CapturesOuterLocal);
            writer.WriteString("declarationLookup", binding.Lookup.Declarations.Kind.ToString());
            writer.WriteStartArray("locals");
            foreach (var local in binding.Lookup.Locals) { writer.WriteNumberValue(local.Id); }
            writer.WriteEndArray();
            writer.WriteStartArray("declarations");
            foreach (var declaration in binding.Lookup.Declarations.Candidates) { writer.WriteNumberValue(declaration.Id); }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
