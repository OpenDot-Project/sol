// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
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
    private readonly Dictionary<SpecializationKey, int> _specializedFunctions = [];
    private readonly Queue<PendingSpecialization> _pendingSpecializations = [];
    private readonly Dictionary<int, FirFunction> _builtFunctions = [];
    private Dictionary<ResolvedType, ResolvedType> _typeSubstitution = [];
    private int _nextFunctionId;
    private int _specializationCount;
    private long _specializedBodyNodeCount;
    private readonly Dictionary<AstNode, AstSnapshot> _files = [];
    private readonly Dictionary<LocalSymbol, FirSlot> _slots = [];
    private readonly Dictionary<AstCall, BodyCallBinding> _calls;
    private AstCallable _current = null!;
    private SpecializedBodyAnalysis? _specializedBody;
    private readonly bool _library;
    private ResolvedType Empty => _analysis.Types.GetTuple([]);
    internal List<Diagnostic> Diagnostics { get; } = [];

    private ExpressionBinding? BoundExpression(AstExpression expression) => _specializedBody is { } specialized
        ? specialized.GetExpression(expression) : _analysis.GetExpression(expression);

    private BodyCallBinding? BoundCall(AstCall call) => _specializedBody is { } specialized
        ? specialized.GetCall(call) : _calls.GetValueOrDefault(call);

    private BodySummary? BoundBody(AstCallable callable) => _specializedBody is { } specialized
        ? specialized.GetBody(callable) : _analysis.GetBody(callable);

    private IEnumerable<AstNode> BoundUnverifiedNodes => _specializedBody is { } specialized
        ? specialized.UnverifiedNodes : _analysis.UnverifiedNodes;

    internal FirBuilder(BodyAnalysis analysis, RuntimeCatalog catalog, ImmutableArray<AstSnapshot> input, CancellationToken cancellation, bool library = false)
    {
        _analysis = analysis; _catalog = catalog; _input = input; _cancellation = cancellation; _library = library;
        _calls = analysis.Calls.ToDictionary(c => c.Call);
        foreach (var file in analysis.Sources.Declarations.Files) { foreach (var node in file.Nodes) { _files.Add(node, file); } }
    }

    internal FirProgram Build()
    {
        foreach (var property in _input.SelectMany(f => f.Nodes).OfType<AstProperty>()
            .Where(p => !Ancestors(p).OfType<AstFunction>().Any()))
        { Unsupported(property, "Global or receiver storage initialization"); }
        var declarations = _analysis.Sources.Declarations.Files.SelectMany(f => f.Nodes).OfType<AstFunction>().Where(f => _catalog.Intrinsic(f) is null).ToArray();
        var ordinary = declarations.Where(f => !f.TypeParameters.Any()).ToArray();
        foreach (var declaration in ordinary) { _functions.Add(declaration, _nextFunctionId++); }
        foreach (var declaration in ordinary)
        {
            _cancellation.ThrowIfCancellationRequested();
            var id = _functions[declaration];
            _builtFunctions.Add(id, BuildFunction(declaration, id, []));
        }
        while (_pendingSpecializations.TryDequeue(out var pending))
        {
            _cancellation.ThrowIfCancellationRequested();
            _builtFunctions.Add(pending.Id, BuildFunction(pending.Function, pending.Id, pending.TypeArguments));
        }
        var functions = Enumerable.Range(0, _nextFunctionId).Select(id => _builtFunctions[id]).ToImmutableArray();
        var entries = declarations.Where(IsEntry).ToArray();
        if (!_library && entries.Length != 1)
        {
            var origin = _input[0].Root;
            Error("SOL2401", "Exactly one Main object with main(args: []String): () is required.", origin);
        }
        return new(_analysis, _catalog, functions, !_library && entries.Length == 1 ? _functions[entries[0]] : -1) { IsLibrary = _library };
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

    private FirFunction BuildFunction(AstFunction function, int id, ImmutableArray<ResolvedType> specializationArguments)
    {
        _cancellation.ThrowIfCancellationRequested();
        _current = function; _slots.Clear();
        _typeSubstitution = CreateSubstitution(function, specializationArguments);
        _specializedBody = null;
        bool bodyWithinBudget = specializationArguments.IsEmpty || AccountSpecializedBody(function);
        if (!bodyWithinBudget && !specializationArguments.IsEmpty)
        {
            return new(id, function.Name ?? "", [], [], Empty, null, FirRuntimeFunction.None, [], function.Origin)
            { SpecializationArguments = specializationArguments };
        }
        _specializedBody = specializationArguments.IsEmpty ? null
            : _analysis.GetGenericBody(function, specializationArguments, _cancellation);
        if (_specializedBody is { } specializedBody) { Diagnostics.AddRange(specializedBody.Diagnostics); }
        var summary = specializationArguments.IsEmpty ? _analysis.GetBody(function) : _specializedBody?.GetBody(function);
        var result = summary?.ReturnType is { } resultType ? Specialize(resultType) : Empty;
        if (_catalog.Owns(function) && function.Body.Kind == AstBodyKind.Missing)
        {
            var runtime = function.Name switch { "print" => FirRuntimeFunction.Print, "println" => FirRuntimeFunction.Println,
                "error" => FirRuntimeFunction.Error, "errorln" => FirRuntimeFunction.Errorln, _ => FirRuntimeFunction.None };
            return new(id, function.Name!, [], [], result, null, runtime, function.ThrownTypes.Select(_analysis.Sources.Types.GetType).Select(Specialize).ToImmutableArray(), function.Origin);
        }
        var owner = Ancestors(function).OfType<AstTypeDeclaration>().FirstOrDefault();
        if (function.TypeParameters.Any() && (owner is not null || specializationArguments.Length != function.TypeParameters.Count())
            || !function.TypeParameters.Any() && !specializationArguments.IsEmpty
            || function.ReturnsByReference || function.Body.Kind == AstBodyKind.Missing
            || owner is not null && (owner.SyntaxKind != NodeKind.ObjectDeclaration
                || _analysis.Sources.Types.GetTypeParameters(owner).Length != 0
                || owner.Members.Any(m => m is not AstFunction)))
        { Unsupported(function, "Generic, unimplemented, ref-return, or stateful receiver body"); }
        var parameters = ImmutableArray.CreateBuilder<FirSlot>();
        foreach (var parameter in function.Parameters)
        {
            var local = _analysis.Sources.Lexical.Scopes.Where(s => s.Owner == function).SelectMany(s => s.Symbols)
                .SingleOrDefault(l => ReferenceEquals(l.Origin.Element, parameter.NameOrigin.Element));
            if (local is null || parameter.Type is null) { Unsupported(function, "Missing parameter identity"); continue; }
            var sourceType = _analysis.Sources.Types.GetType(parameter.Type);
            if (parameter.PassingMode == AstPassingMode.Vararg) { sourceType = _analysis.Types.GetArray(sourceType); }
            var type = Specialize(sourceType);
            if (parameter.PassingMode == AstPassingMode.Vararg && !FirVerifier.Supported(type))
            { Unsupported(function, "Vararg array element layout"); }
            var passingMode = parameter.PassingMode == AstPassingMode.Vararg ? AstPassingMode.Value : parameter.PassingMode;
            var slot = new FirSlot(_slots.Count, parameter.Name, type, local.IsMutable, passingMode, true, parameter.NameOrigin);
            _slots.Add(local, slot); parameters.Add(slot);
        }
        FirBlock body;
        if (!bodyWithinBudget) { body = new([], function.Origin); }
        else if (function.Body.Node is AstBlock block) { body = Block(block, true); }
        else if (function.Body.Node is AstExpression expression)
        { body = new([new FirReturn(Expression(expression), expression.Origin)], function.Origin); }
        else { body = new([], function.Origin); }
        if (result == Empty && (body.Statements.IsEmpty || body.Statements[^1] is not FirReturn))
        { body = body with { Statements = body.Statements.Add(new FirReturn(null, function.Origin)) }; }
        foreach (var gap in BoundUnverifiedNodes.Where(n => Ancestors(n).Contains(function) && n is not AstType))
        { Unsupported(gap, "Unverified source obligation"); }
        return new(id, function.Name ?? "", parameters.ToImmutable(), _slots.Values.OrderBy(s => s.Id).ToImmutableArray(),
            result, body, FirRuntimeFunction.None, function.ThrownTypes.Select(_analysis.Sources.Types.GetType).Select(Specialize).ToImmutableArray(), function.Origin)
        {
            ExportName = _library && !_catalog.Owns(function) && owner is null && function.IsPublic && specializationArguments.IsEmpty ? function.Name : null,
            SpecializationArguments = specializationArguments,
        };
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
