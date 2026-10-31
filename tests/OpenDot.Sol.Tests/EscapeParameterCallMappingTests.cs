// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class EscapeParameterCallMappingTests
{
    [Fact]
    public void NamedCallbackAfterAnOmittedDefaultUsesItsFormalContract()
    {
        var analysis = Analyze("module app\n"
            + "func invoke(prefix: int = 0, callback: () -> int): int = callback()\n"
            + "func use(ref value: int): int { invoke(callback = { -> value }) }");
        var escape = Assert.Single(analysis.Sources.Lexical.LambdaEscapes);

        Assert.False(analysis.HasErrors, Describe(analysis));
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.NonescapingCallArgument);
    }

    [Fact]
    public void TrailingLambdaAfterAnOmittedDefaultUsesTheFinalFormal()
    {
        var analysis = Analyze("module app\n"
            + "func invoke(prefix: int = 0, callback: () -> int): int = callback()\n"
            + "func use(ref value: int): int { invoke { -> value } }");
        var escape = Assert.Single(analysis.Sources.Lexical.LambdaEscapes);

        Assert.False(analysis.HasErrors, Describe(analysis));
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.NonescapingCallArgument);
    }

    [Fact]
    public void TrailingLambdaAfterVarargUsesTheFinalCallbackFormal()
    {
        var analysis = Analyze("module app\n"
            + "func invoke(prefix: int = 0, vararg values: int, callback: () -> int): int = callback()\n"
            + "func use(ref value: int): int { invoke { -> value } }");
        var escape = Assert.Single(analysis.Sources.Lexical.LambdaEscapes);

        Assert.False(analysis.HasErrors, Describe(analysis));
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.NonescapingCallArgument);
    }

    [Fact]
    public void OutOfOrderNamedArgumentsMapTheClosureToItsNamedFormal()
    {
        var analysis = Analyze("module app\n"
            + "func invoke(prefix: int = 0, callback: () -> int, enabled: bool = true): int = callback()\n"
            + "func use(ref value: int): int { invoke(enabled = false, callback = { -> value }) }");
        var escape = Assert.Single(analysis.Sources.Lexical.LambdaEscapes);

        Assert.False(analysis.HasErrors, Describe(analysis));
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.Contains(escape.Uses, use => use.Kind == LambdaEscapeUseKind.NonescapingCallArgument);
    }

    [Fact]
    public void NamedCallbackAfterAnOmittedDefaultPreservesEscapingContract()
    {
        var analysis = Analyze("module app\n"
            + "func retain(prefix: int = 0, @escaping callback: () -> int): () -> int = callback\n"
            + "func use(ref value: int): () -> int { retain(callback = { -> value }) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }

    [Fact]
    public void TypedOverloadWithNonescapingCallbackIsNotRejectedByOtherOverloads()
    {
        var analysis = Analyze("module app\n"
            + "func invoke(kind: int, callback: () -> int): int = callback()\n"
            + "func invoke(kind: bool, @escaping callback: () -> int): int = callback()\n"
            + "func use(ref value: int): int { val kind: int = 1; invoke(kind, { -> value }) }");
        var escape = Assert.Single(analysis.Sources.Lexical.LambdaEscapes);

        Assert.False(analysis.HasErrors, Describe(analysis));
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
    }

    [Fact]
    public void TypedOverloadWithEscapingCallbackStillRejectsBorrowedCapture()
    {
        var analysis = Analyze("module app\n"
            + "func invoke(kind: int, callback: () -> int): int = callback()\n"
            + "func invoke(kind: bool, @escaping callback: () -> int): int = callback()\n"
            + "func use(ref value: int): () -> int { val kind: bool = true; invoke(kind, { -> value }) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }

    [Fact]
    public void ExactNominalOverloadWinsOverEscapingReferenceUpcastOverload()
    {
        var analysis = Analyze("module app\n"
            + "open class Device {}\nclass Mobile: Device {}\n"
            + "func invoke(device: Mobile, callback: () -> int): int = callback()\n"
            + "func invoke(device: Device, @escaping callback: () -> int): int = callback()\n"
            + "func use(ref value: int, device: Mobile): int { invoke(device, { -> value }) }");
        var escape = Assert.Single(analysis.Sources.Lexical.LambdaEscapes);
        var bindings = new Dictionary<AstAtom, NameBinding>(ReferenceEqualityComparer.Instance);
        foreach (var binding in analysis.Sources.Lexical.Bindings)
        { bindings.Add(binding.Reference, binding); }
        var call = Assert.Single(analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstCall>(), candidate => candidate.Arguments.Length == 2);
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
        Assert.False(analysis.HasErrors, Describe(analysis));
        Assert.Equal(CallArgumentEscapeContract.NonEscaping, EscapeParameterContracts.Resolve(call, 1,
            bindings, analysis.Sources.Declarations, analysis.Sources.Types));
    }

    [Fact]
    public void ExactEscapingNominalOverloadStillRejectsBorrowedCapture()
    {
        var analysis = Analyze("module app\n"
            + "open class Device {}\nclass Mobile: Device {}\n"
            + "func invoke(device: Mobile, @escaping callback: () -> int): int = callback()\n"
            + "func invoke(device: Device, callback: () -> int): int = callback()\n"
            + "func use(ref value: int, device: Mobile): int { invoke(device, { -> value }) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }

    [Fact]
    public void IntegerLiteralExcludesBooleanCallbackOverloadWithoutChangingLiteralTyping()
    {
        const string source = "module app\n"
            + "func invoke(kind: int, callback: () -> int): int = callback()\n"
            + "func invoke(kind: bool, @escaping callback: () -> int): int = callback()\n"
            + "func use(ref value: int): int { invoke(1, { -> value }) }";
        var session = AstTestSources.Session(source);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        var bindings = new Dictionary<AstAtom, NameBinding>(ReferenceEqualityComparer.Instance);
        foreach (var binding in sources.Lexical.Bindings) { bindings.Add(binding.Reference, binding); }
        var call = Assert.Single(sources.Declarations.Files.SelectMany(file => file.Nodes).OfType<AstCall>(),
            candidate => candidate.Arguments.Length == 2);
        var target = Assert.IsType<AstAtom>(call.Target);
        Assert.Equal(2, bindings[target].Lookup.Declarations.Candidates.Length);
        var numeric = Assert.IsType<AstAtom>(call.Arguments[0].Value);
        Assert.Equal(OpenDot.Sol.Cst.SyntaxKind.IntegerLiteralToken, numeric.Origin.Element.Tokens.First().Kind);
        var boolType = sources.Types.Types.First(type => type.Kind == ResolvedTypeKind.Primitive && type.Name == "bool");
        Assert.False(CallBindingBuilder.NumericLiteralFits(numeric.Spelling, boolType));
        Assert.Equal(CallArgumentEscapeContract.NonEscaping,
            EscapeParameterContracts.Resolve(call, 1, bindings, sources.Declarations, sources.Types));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var escape = Assert.Single(analysis.Sources.Lexical.LambdaEscapes);

        Assert.False(analysis.HasErrors, Describe(analysis));
        Assert.Equal(LambdaEscapeDisposition.ProvenNonEscaping, escape.Disposition);
    }

    [Fact]
    public void BooleanLiteralRetainsEscapingBooleanOverloadContract()
    {
        var analysis = Analyze("module app\n"
            + "func invoke(kind: int, callback: () -> int): int = callback()\n"
            + "func invoke(kind: bool, @escaping callback: () -> int): int = callback()\n"
            + "func use(ref value: int): () -> int { invoke(true, { -> value }) }");

        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2262");
    }

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session(source);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(diagnostic =>
            diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static string Describe(BodyAnalysis analysis) => string.Join('\n', analysis.Diagnostics.Select(diagnostic =>
        diagnostic.Code + " " + diagnostic.Message));
}
