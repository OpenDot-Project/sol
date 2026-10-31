// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.IO;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Fir;
using OpenDot.Sol.Psi;
using OpenDot.Sol.Text;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class FirIndirectCallFlowTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String {}\nopen class Throwable {}\n"
        + "open class Exception: Throwable {}\nopen class RuntimeException: Exception {}\n";

    private static BodyAnalysis Analyze(string source)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors,
            string.Join('\n', sources.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static FirProgram Compile(string source)
    {
        var compilation = FirCompilation.Create([AstTestSources.Parse(source)], TestContext.Current.CancellationToken);
        Assert.NotNull(compilation.Program);
        Assert.Empty(compilation.Diagnostics);
        return compilation.Program.Program;
    }

    [Fact]
    public void IndirectCallbackFlowFixtureParses()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "spec", "examples", "semantics", "IndirectCallbackFlow.sol");
        var syntax = SyntaxTree.Parse(SourceText.FromBytes(File.ReadAllBytes(path), path),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(syntax.HasErrors,
            string.Join('\n', syntax.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var tree = AstSnapshot.Create(PsiSnapshot.Create(syntax, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        AstVerifier.Verify(tree, TestContext.Current.CancellationToken);
        Assert.Equal("examples.semantics.indirect_callback_flow", tree.Root.ModuleName);
    }

    [Fact]
    public void IndirectOutCallAssignsStorageOnlyOnItsNormalContinuation()
    {
        var program = Compile("""
            func invoke(callback: (out int) -> ()): int {
                var result: int
                callback(out result)
                result
            }
            object Main { func main(args: []String): () { } }
            """);
        var function = Assert.Single(program.Functions, candidate => candidate.Name == "invoke");
        var graph = Assert.Single(FirVerifier.Verify(program, TestContext.Current.CancellationToken)
            .Program!.FlowGraphs, candidate => candidate.FunctionId == function.Id);
        Assert.Empty(graph.Diagnostics);
        var call = Assert.Single(graph.Blocks.SelectMany(block => block.Calls));
        Assert.Null(call.Target);
        Assert.Empty(call.Effects);
        Assert.Contains(graph.Blocks, block => block.Writes.Contains(function.Slots
            .Single(slot => slot.Name == "result").Id));
    }

    [Fact]
    public void OutCallbackCannotGuaranteeAssignmentAcrossShortCircuitSkip()
    {
        var result = Analyze("""
            func read(callback: (out int) -> bool): int {
                var value: int
                false && callback(out value)
                value
            }
            """);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2240");
    }

    [Fact]
    public void ThrowingOutCallbackDoesNotAssignValueObservedByCatchOrAfterTry()
    {
        var result = Analyze("""
            class Failure: Exception {}
            func read(callback: (out int) -> () throws Failure): int {
                var value: int
                try { callback(out value) } catch (error: Failure) {}
                value
            }
            """);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2240");
    }

    [Fact]
    public void IndirectCallbackEffectsMustBeDeclaredOrCaught()
    {
        var unhandled = Analyze("""
            class Failure: Exception {}
            func invoke(callback: () -> () throws Failure) { callback() }
            """);
        Assert.Contains(unhandled.Diagnostics, diagnostic => diagnostic.Code == "SOL2250");

        var declared = Analyze("""
            class Failure: Exception {}
            func invoke(callback: () -> () throws Failure) throws Exception { callback() }
            """);
        Assert.False(declared.HasErrors,
            string.Join('\n', declared.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));

        var caught = Analyze("""
            class Failure: Exception {}
            func invoke(callback: () -> () throws Failure) {
                try { callback() } catch (error: Failure) {}
            }
            """);
        Assert.False(caught.HasErrors,
            string.Join('\n', caught.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
    }

    [Fact]
    public void RefCallbackInvalidatesPriorNullableSmartCast()
    {
        var result = Analyze("""
            class Item { val size: int = 1 }
            func inspect(item: Item?, mutate: (ref Item?) -> ()) {
                if item == null { return }
                mutate(ref item)
                item.size
            }
            """);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2243");
    }

    [Fact]
    public void RefCallbackRejectsImmutableStorage()
    {
        var result = Analyze("""
            func invoke(callback: (ref int) -> ()) {
                val value = 1
                callback(ref value)
            }
            """);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SOL2244");
    }
}
