// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace OpenDot.Sol.Ast;

/// <summary>A flat, versioned inspection format. It is not executable FIR or a binary compatibility contract.</summary>
public static class AstJson
{
    public const int SchemaVersion = 1;

    public static string Serialize(AstSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        Write(snapshot, stream, cancellationToken);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static void Write(AstSnapshot snapshot, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        using var writer = new Utf8JsonWriter(destination, new() { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schema", SchemaVersion);
        writer.WriteString("module", snapshot.Root.ModuleName);
        writer.WriteBoolean("hasSyntaxErrors", snapshot.HasSyntaxErrors);
        writer.WriteNumber("root", snapshot.Root.Id);
        writer.WriteStartArray("nodes");
        foreach (var node in snapshot.Nodes.OrderBy(n => n.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteNumber("id", node.Id);
            writer.WriteString("kind", node.SyntaxKind.ToString());
            writer.WriteNumber("start", node.Origin.Span.Start);
            writer.WriteNumber("length", node.Origin.Span.Length);
            writer.WriteStartArray("children");
            foreach (var child in node.Children) { writer.WriteNumberValue(child.Id); }
            writer.WriteEndArray();
            if (node is AstType type)
            {
                writer.WriteString("type", type.Shape.CanonicalName);
            }
            if (node is AstDeclaration declaration)
            {
                writer.WriteString("name", declaration.Name);
                writer.WriteStartArray("modifiers");
                foreach (var modifier in declaration.Modifiers) { writer.WriteStringValue(modifier); }
                writer.WriteEndArray();
            }
            if (node is AstCallable callable)
            {
                writer.WriteStartArray("parameters");
                foreach (var parameter in callable.Parameters)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", parameter.Name);
                    writer.WriteString("mode", parameter.PassingMode.ToString());
                    writer.WriteString("type", parameter.Type?.Shape.CanonicalName);
                    writer.WriteNumber("nameStart", parameter.NameOrigin.Span.Start);
                    writer.WriteNumber("groupStart", parameter.GroupOrigin.Span.Start);
                    writer.WriteBoolean("property", parameter.IsProperty);
                    writer.WriteBoolean("mutable", parameter.IsMutable);
                    if (parameter.DefaultValue is not null)
                    {
                        writer.WriteNumber("default", parameter.DefaultValue.Id);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (node is AstOperation operation)
            {
                writer.WriteString("operator", operation.Operation);
                writer.WriteBoolean("postfix", operation.IsPostfix);
            }
            if (node is AstAssignment assignment)
            {
                writer.WriteString("operator", assignment.Operation);
            }
            if (node is AstMember member)
            {
                writer.WriteBoolean("safe", member.IsSafe);
                writer.WriteString("member", member.Name);
            }
            if (node is AstLambda lambda)
            {
                writer.WriteString("form", lambda.Form.ToString());
                writer.WriteBoolean("unrepresentablePosition", lambda.HasUnrepresentablePosition);
                writer.WriteStartArray("positions");
                foreach (int position in lambda.Positions) { writer.WriteNumberValue(position); }
                writer.WriteEndArray();
            }
            if (node is AstCall call)
            {
                writer.WriteStartArray("arguments");
                foreach (var argument in call.Arguments)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", argument.Name);
                    writer.WriteString("mode", argument.Mode.ToString());
                    writer.WriteNumber("value", argument.Value.Id);
                    writer.WriteBoolean("trailingLambda", argument.IsTrailingLambda);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (node is AstImport import)
            {
                writer.WriteString("module", import.ModuleName);
                writer.WriteString("alias", import.Alias);
                writer.WriteBoolean("public", import.IsPublic);
                writer.WriteStartArray("items");
                foreach (var item in import.Items)
                {
                    writer.WriteStartObject();
                    writer.WriteString("local", item.LocalName);
                    writer.WriteString("original", item.OriginalName);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
    }
}
