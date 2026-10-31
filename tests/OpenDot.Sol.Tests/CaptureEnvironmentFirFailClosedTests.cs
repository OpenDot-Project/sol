// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class CaptureEnvironmentFirFailClosedTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void VerifierRejectsCaptureBearingSourceRetargetedAsNoncapturingReference(
        bool anonymousFunction, bool receiverCapture)
    {
        var program = BuildUnverified(CaptureSource);
        var lexical = program.Analysis.Sources.Lexical;
        AstNode captured = FindCallableSource(program, anonymousFunction, receiverCapture, capture: true);
        AstNode plain = FindCallableSource(program, anonymousFunction, receiverCapture, capture: false);
        FirFunction target = FindSyntheticFunction(program, plain, anonymousFunction);
        var (host, capturedDeclaration) = FindSourceDeclaration(program, captured);
        var (plainHost, plainDeclaration) = FindSourceDeclaration(program, plain);
        Assert.NotEqual(host.Id, target.Id);
        Assert.NotEqual(plainHost.Id, target.Id);
        var plainReference = Assert.IsType<FirFunctionReference>(plainDeclaration.Initializer);
        Assert.IsNotType<FirFunctionReference>(capturedDeclaration.Initializer);
        Assert.Equal(plainReference.Type, program.Analysis.GetExpression((AstExpression)captured)!.Type);

        FirFunction forgedTarget = anonymousFunction
            ? target with { AnonymousFunctionSource = (AstAnonymousFunction)captured, LambdaSource = null,
                Origin = captured.Origin }
            : target with { LambdaSource = (AstLambda)captured, AnonymousFunctionSource = null,
                Origin = captured.Origin };
        var redirected = new FirFunctionReference(target.Id, plainReference.Type, captured.Origin);
        var forgedHost = ReplaceInitializer(host, capturedDeclaration, redirected);
        var forgedFunctions = program.Functions.SetItem(target.Id, forgedTarget).SetItem(host.Id, forgedHost);

        var result = FirVerifier.Verify(program with { Functions = forgedFunctions }, TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        string captureMessage = anonymousFunction
            ? "captures local, receiver, or declaration-owned storage"
            : "captures a local, receiver, or declaration-owned member";
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains(captureMessage, System.StringComparison.OrdinalIgnoreCase));
        if (receiverCapture)
        {
            if (anonymousFunction)
            { Assert.NotEmpty(lexical.GetAnonymousFunctionReceiverCaptures((AstAnonymousFunction)captured)); }
            else { Assert.NotEmpty(lexical.GetLambdaReceiverCaptures((AstLambda)captured)); }
        }
        else
        {
            if (anonymousFunction)
            { Assert.NotEmpty(lexical.GetAnonymousFunctionCaptures((AstAnonymousFunction)captured)); }
            else { Assert.NotEmpty(lexical.GetLambdaCaptures((AstLambda)captured)); }
        }
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("this")]
    public void VerifierRejectsFabricatedLocalOrReceiverSlot(string fabricatedName)
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(
            "object Main { func main(args: []String) { val seed = 41; "
            + "val callback = func(): int { 1 }; val answer = callback() } }")],
            TestContext.Current.CancellationToken);
        var program = Assert.IsType<VerifiedFirProgram>(compilation.Program).Program;
        var target = Assert.Single(program.Functions, function => function.AnonymousFunctionSource is not null);
        var local = Assert.Single(program.Functions.Single(function => function.Name == "main").Slots,
            slot => slot.Name == "seed");
        var fake = new FirSlot(target.Slots.Length, fabricatedName, target.ReturnType,
            Mutable: false, Mode: AstPassingMode.Value, IsParameter: false, Origin: local.Origin);
        var returnStatement = Assert.IsType<FirReturn>(Assert.Single(target.Body!.Statements));
        var fakeRead = new FirRead(fake.Id, fake.Type, local.Origin);
        var forgedBody = target.Body with
        {
            Statements = [new FirDeclare(fake.Id, null, fake.Origin), returnStatement with { Value = fakeRead }],
        };
        var forgedTarget = target with { Slots = target.Slots.Add(fake), Body = forgedBody };

        var result = FirVerifier.Verify(Replace(program, forgedTarget), TestContext.Current.CancellationToken);

        Assert.Null(result.Program);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2402"
            && diagnostic.Message.Contains("anonymous-function FIR statement count", System.StringComparison.OrdinalIgnoreCase));
    }

    private const string CaptureSource = "func localCaptures(seed: int) { val capturedAnonymous: () -> int = func(): int = seed; "
        + "val capturedLambda: () -> int = { seed } }\n"
        + "class Box(val value: int) { func receiverCaptures() { "
        + "val capturedAnonymous: () -> int = func(): int = value; "
        + "val capturedLambda: () -> int = { value } } }\n"
        + "object Main { func main(args: []String) { "
        + "val plainAnonymous: () -> int = func(): int = 1; "
        + "val plainLambda: () -> int = { 2 }; val result = plainAnonymous() + plainLambda() } }";

    private static FirProgram BuildUnverified(string text)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var source = AstTestSources.Parse(text);
        var input = ImmutableArray.Create(source);
        var catalog = RuntimeCatalog.Create();
        var session = AnalysisSession.Create(input.AddRange(catalog.Files), cancellationToken: cancellation);
        var bodies = BodyAnalysis.Create(SourceAnalysis.Create(session, cancellation), cancellation);
        Assert.False(bodies.HasErrors, string.Join('\n', bodies.Diagnostics.Select(item => item.Code + " " + item.Message)));
        var builder = new FirBuilder(bodies, catalog, input, cancellation);
        var program = builder.Build();
        Assert.Contains(builder.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
        return program;
    }

    private static AstNode FindCallableSource(FirProgram program, bool anonymousFunction, bool receiverCapture,
        bool? capture = null)
    {
        var lexical = program.Analysis.Sources.Lexical;
        var nodes = program.Analysis.Sources.Declarations.Files.SelectMany(file => file.Root.Descendants());
        if (anonymousFunction)
        {
            var functions = nodes.OfType<AstAnonymousFunction>();
            return functions.Single(function => capture is null || (capture.Value
                ? (receiverCapture
                    ? !lexical.GetAnonymousFunctionReceiverCaptures(function).IsEmpty
                    : !lexical.GetAnonymousFunctionCaptures(function).IsEmpty)
                : lexical.GetAnonymousFunctionReceiverCaptures(function).IsEmpty
                    && lexical.GetAnonymousFunctionCaptures(function).IsEmpty));
        }

        var lambdas = nodes.OfType<AstLambda>();
        return lambdas.Single(lambda => capture is null || (capture.Value
            ? (receiverCapture
                ? !lexical.GetLambdaReceiverCaptures(lambda).IsEmpty
                : !lexical.GetLambdaCaptures(lambda).IsEmpty)
            : lexical.GetLambdaReceiverCaptures(lambda).IsEmpty && lexical.GetLambdaCaptures(lambda).IsEmpty));
    }

    private static FirFunction FindSyntheticFunction(FirProgram program, AstNode source, bool anonymousFunction)
        => program.Functions.Single(function => anonymousFunction
            ? ReferenceEquals(function.AnonymousFunctionSource, source)
            : ReferenceEquals(function.LambdaSource, source));

    private static (FirFunction Host, FirDeclare Declaration) FindSourceDeclaration(FirProgram program, AstNode source)
    {
        var file = program.Analysis.Sources.Declarations.Files.Single(candidate =>
            candidate.Root.Descendants().Any(node => ReferenceEquals(node, source)));
        AstFunction owner = Ancestors(source, file).OfType<AstFunction>().First();
        AstProperty property = Ancestors(source, file).OfType<AstProperty>().First();
        var host = program.Functions.Single(function => ReferenceEquals(function.Origin.Element, owner.Origin.Element));
        var slot = host.Slots.Single(candidate => candidate.Name == property.BindingNames.Single()
            && ReferenceEquals(candidate.Origin.Element, property.Origin.Element));
        return (host, Assert.Single(host.Body!.Statements.OfType<FirDeclare>(), declaration => declaration.Slot == slot.Id));
    }

    private static System.Collections.Generic.IEnumerable<AstNode> Ancestors(AstNode node, AstSnapshot file)
    {
        for (AstNode? parent = file.GetParent(node); parent is not null; parent = file.GetParent(parent))
        { yield return parent; }
    }

    private static FirFunction ReplaceInitializer(FirFunction function, FirDeclare before, FirExpression after)
    {
        int index = function.Body!.Statements.IndexOf(before);
        Assert.True(index >= 0);
        var declaration = before with { Initializer = after };
        return function with { Body = function.Body with { Statements = function.Body.Statements.SetItem(index, declaration) } };
    }

    private static FirProgram Replace(FirProgram program, FirFunction replacement)
        => program with { Functions = program.Functions.SetItem(replacement.Id, replacement) };
}
