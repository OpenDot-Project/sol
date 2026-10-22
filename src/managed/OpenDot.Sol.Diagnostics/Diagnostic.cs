// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using OpenDot.Sol.Text;

namespace OpenDot.Sol.Diagnostics;

public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>A diagnostic bound to the snapshot that produced its byte span.</summary>
public sealed record Diagnostic
{
    public Diagnostic(string code, string message, SourceText source, TextSpan span,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(source);
        source.Validate(span);
        Code = code;
        Message = message;
        Source = source;
        Span = span;
        Severity = severity;
    }

    public string Code { get; }
    public string Message { get; }
    public SourceText Source { get; }
    public TextSpan Span { get; }
    public DiagnosticSeverity Severity { get; }
}
