// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Driver;

public static class CstWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        MaxDepth = 1024,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] Serialize(SyntaxTree tree) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema_version = 1,
        source_path = tree.Source.Path,
        module = tree.ModuleName,
        root = tree.Root,
        tokens = tree.Tokens.Select(token => new
        {
            kind = token.Kind,
            span = token.Span,
            text = tree.Source.GetText(token.Span),
            leading_trivia = token.LeadingTrivia.Select(trivia => new
            {
                kind = trivia.Kind,
                span = trivia.Span,
                text = tree.Source.GetText(trivia.Span),
            }),
        }),
    }, Options);
}
