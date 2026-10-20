// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Fir;

internal sealed partial class FirBuilder
{
    private readonly BodyAnalysis _analysis;
    private readonly RuntimeCatalog _catalog;
    private readonly ImmutableArray<AstSnapshot> _input;
    private readonly CancellationToken _cancellation;
    private readonly Dictionary<AstCallable, int> _functions = [];
    private readonly Dictionary<AstNode, AstSnapshot> _files = [];
    private readonly Dictionary<LocalSymbol, FirSlot> _slots = [];
    private readonly Dictionary<AstCall, BodyCallBinding> _calls;
    private AstCallable _current = null!;
    private ResolvedType Empty => _analysis.Types.GetTuple([]);
    internal List<Diagnostic> Diagnostics { get; } = [];

    internal FirBuilder(BodyAnalysis analysis, RuntimeCatalog catalog, ImmutableArray<AstSnapshot> input, CancellationToken cancellation)
    {
        _analysis = analysis; _catalog = catalog; _input = input; _cancellation = cancellation;
        _calls = analysis.Calls.ToDictionary(c => c.Call);
        foreach (var file in analysis.Sources.Declarations.Files) { foreach (var node in file.Nodes) { _files.Add(node, file); } }
    }

    internal FirProgram Build()
    {
        foreach (var property in _input.SelectMany(f => f.Nodes).OfType<AstProperty>()
            .Where(p => !Ancestors(p).OfType<AstFunction>().Any()))
        { Unsupported(property, "Global or receiver storage initialization"); }
        var declarations = _analysis.Sources.Declarations.Files.SelectMany(f => f.Nodes).OfType<AstFunction>().ToArray();
        foreach (var declaration in declarations) { _functions.Add(declaration, _functions.Count); }
        var functions = declarations.Select(BuildFunction).ToImmutableArray();
        var entries = declarations.Where(IsEntry).ToArray();
        if (entries.Length != 1)
        {
            var origin = _input[0].Root;
            Error("SOL2401", "Exactly one Main object with main(args: []String): () is required.", origin);
        }
        return new(_analysis, _catalog, functions, entries.Length == 1 ? _functions[entries[0]] : -1);
    }

    private bool IsEntry(AstFunction function)
    {
        var parent = Ancestors(function).OfType<AstTypeDeclaration>().FirstOrDefault();
        var parameters = function.Parameters;
        return parent is { Name: "Main", SyntaxKind: NodeKind.ObjectDeclaration } && function.Name == "main"
            && !function.TypeParameters.Any() && parameters is [{ PassingMode: AstPassingMode.Value, Type: not null } parameter]
            && _analysis.Sources.Types.GetType(parameter.Type) is { Kind: ResolvedTypeKind.Array, Arguments: [{ Declaration: { ModuleName: "std.core", Name: "String" } }] }
            && _analysis.GetBody(function)?.ReturnType == Empty && !function.Modifiers.Contains("private");
    }

    private FirFunction BuildFunction(AstFunction function)
    {
        _cancellation.ThrowIfCancellationRequested();
        _current = function; _slots.Clear();
        var summary = _analysis.GetBody(function);
        var result = summary?.ReturnType ?? Empty;
        if (_catalog.Owns(function))
        {
            var runtime = function.Name switch { "print" => FirRuntimeFunction.Print, "println" => FirRuntimeFunction.Println,
                "error" => FirRuntimeFunction.Error, "errorln" => FirRuntimeFunction.Errorln, _ => FirRuntimeFunction.None };
            return new(_functions[function], function.Name!, [], [], result, null, runtime, function.ThrownTypes.Select(_analysis.Sources.Types.GetType).ToImmutableArray(), function.Origin);
        }
        var owner = Ancestors(function).OfType<AstTypeDeclaration>().FirstOrDefault();
        if (function.TypeParameters.Any() || function.ReturnsByReference || function.Body.Kind == AstBodyKind.Missing
            || owner is not null && (owner.SyntaxKind != NodeKind.ObjectDeclaration || owner.Members.Any(m => m is not AstFunction)))
        { Unsupported(function, "Generic, unimplemented, ref-return, or stateful receiver body"); }
        var parameters = ImmutableArray.CreateBuilder<FirSlot>();
        foreach (var parameter in function.Parameters)
        {
            var local = _analysis.Sources.Lexical.Scopes.Where(s => s.Owner == function).SelectMany(s => s.Symbols)
                .SingleOrDefault(l => ReferenceEquals(l.Origin.Element, parameter.NameOrigin.Element));
            if (local is null || parameter.Type is null) { Unsupported(function, "Missing parameter identity"); continue; }
            var type = _analysis.Sources.Types.GetType(parameter.Type);
            if (parameter.PassingMode == AstPassingMode.Vararg || parameter.DefaultValue is not null) { Unsupported(function, "User vararg/default lowering"); }
            var slot = new FirSlot(_slots.Count, parameter.Name, type, local.IsMutable, parameter.PassingMode, true, parameter.NameOrigin);
            _slots.Add(local, slot); parameters.Add(slot);
        }
        FirBlock body;
        if (function.Body.Node is AstBlock block) { body = Block(block, true); }
        else if (function.Body.Node is AstExpression expression)
        { body = new([new FirReturn(Expression(expression), expression.Origin)], function.Origin); }
        else { body = new([], function.Origin); }
        if (result == Empty && (body.Statements.IsEmpty || body.Statements[^1] is not FirReturn))
        { body = body with { Statements = body.Statements.Add(new FirReturn(null, function.Origin)) }; }
        foreach (var gap in _analysis.UnverifiedNodes.Where(n => Ancestors(n).Contains(function) && n is not AstType))
        { Unsupported(gap, "Unverified source obligation"); }
        return new(_functions[function], function.Name ?? "", parameters.ToImmutable(), _slots.Values.OrderBy(s => s.Id).ToImmutableArray(),
            result, body, FirRuntimeFunction.None, function.ThrownTypes.Select(_analysis.Sources.Types.GetType).ToImmutableArray(), function.Origin);
    }

    private IEnumerable<AstNode> Ancestors(AstNode node)
    {
        for (var parent = _files[node].GetParent(node); parent is not null; parent = _files[node].GetParent(parent)) { yield return parent; }
    }
    private void Error(string code, string message, AstNode node) => Diagnostics.Add(new(code, message, node.Origin.Source, node.Origin.Span));
    private void Unsupported(AstNode node, string subject) => Error("SOL2400", subject + " is not supported by the verified native subset.", node);
    private FirExpression Invalid(AstNode node)
    { Unsupported(node, "Expression shape"); return new FirConstant("()", Empty, node.Origin); }
}
