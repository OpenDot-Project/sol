// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Diagnostics;

namespace OpenDot.Sol.Analysis;

internal sealed partial class CallBindingBuilder
{
    private readonly CancellationToken _cancellation;
    private readonly Dictionary<AstNode, AstSnapshot> _files = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<AstCall> _active = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DeclarationSymbol> _moduleFunctions = [];
    private readonly bool _hasUserConversions;

    internal CallBindingBuilder(SourceAnalysis sources, CancellationToken cancellation)
    {
        Sources = sources;
        Types = new(sources.Types);
        _cancellation = cancellation;
        foreach (var file in sources.Declarations.Files)
        {
            foreach (var node in file.Nodes) { _files.Add(node, file); }
            foreach (var function in file.Root.Declarations.OfType<AstFunction>())
            {
                if (sources.Declarations.GetDeclaredSymbol(function) is { } symbol) { _moduleFunctions.Add(symbol); }
            }
        }
        _hasUserConversions = _files.Keys.OfType<AstFunction>().Any(f => f.Name == "implicit" && f.Modifiers.Contains("operator"));
    }

    internal SourceAnalysis Sources { get; }
    internal TypeSpecialization Types { get; }
    internal Dictionary<AstCall, CallBinding> Bindings { get; } = new(ReferenceEqualityComparer.Instance);
    internal List<Diagnostic> Diagnostics { get; } = [];

    internal void Build()
    {
        foreach (var call in Sources.Declarations.Files.SelectMany(f => f.Nodes).OfType<AstCall>())
        {
            Bind(call, 0);
        }
    }

    private CallBinding Bind(AstCall call, int depth)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (Bindings.TryGetValue(call, out var previous)) { return previous; }
        if (depth > 128 || !_active.Add(call)) { return new(call, CallBindingStatus.Deferred, null, [], "Cyclic or nested call inference requires an annotation."); }
        CallBinding result;
        try { result = BindCore(call, depth); }
        catch (TypeSpecializationException error)
        {
            Report("SOL2134", error.Message, call);
            result = new(call, CallBindingStatus.Deferred, null, [], error.Message);
        }
        finally { _active.Remove(call); }
        Bindings.Add(call, result);
        return result;
    }

    private CallBinding BindCore(AstCall call, int depth)
    {
        var target = call.Target is AstGenericApplication generic ? generic.Target : call.Target;
        var explicitTypes = call.Target is AstGenericApplication application
            ? application.TypeArguments.Select(Sources.Types.GetType).ToImmutableArray() : default;
        var symbols = FindTargets(call, target);
        if (symbols is null) { return new(call, CallBindingStatus.Deferred, null, [], "Receiver, local function, constructor, or function-value binding is not available."); }
        if (symbols.Value.Select(s => s.ModuleName).Distinct(StringComparer.Ordinal).Take(2).Count() > 1)
        {
            Report("SOL2132", "Ambiguous imported function name.", call);
            return new(call, CallBindingStatus.Ambiguous, null, [], "Imported declarations do not form one module overload set.");
        }
        if (symbols.Value.IsEmpty)
        {
            Report("SOL2130", "Unresolved direct function call.", call);
            return new(call, CallBindingStatus.Unresolved, null, [], "No visible module function.");
        }
        var candidates = symbols.Value.Select(symbol => Candidate(call, symbol, explicitTypes, depth + 1)).ToImmutableArray();
        // A deferred candidate may beat every currently applicable candidate. Never guess a winner.
        if (candidates.Any(c => c.Status == CallCandidateStatus.Deferred))
        {
            return new(call, CallBindingStatus.Deferred, null, candidates, "Candidate applicability needs an unimplemented analysis.");
        }
        var applicable = candidates.Where(c => c.Status == CallCandidateStatus.Applicable).ToArray();
        if (applicable.Length == 0)
        {
            Report("SOL2131", "No applicable direct function overload.", call);
            return new(call, CallBindingStatus.NoApplicableCandidate, null, candidates, "All candidates were rejected.");
        }
        var best = applicable.Where(candidate => !applicable.Any(other => !ReferenceEquals(other, candidate) && Better(other, candidate))).ToArray();
        if (best.Length != 1)
        {
            Report("SOL2132", "Ambiguous direct function overload.", call);
            return new(call, CallBindingStatus.Ambiguous, null, candidates, "No unique best candidate.");
        }
        return new(call, CallBindingStatus.Bound, best[0], candidates, null);
    }

    private ImmutableArray<DeclarationSymbol>? FindTargets(AstCall call, AstExpression? target)
    {
        ImmutableArray<DeclarationSymbol> found;
        if (target is AstAtom { SyntaxKind: NodeKind.NameExpression } name && !string.IsNullOrWhiteSpace(name.Spelling))
        {
            if (name.Spelling is "this" or "super") { return null; }
            var lookup = Sources.Lexical.Lookup(name, name.Spelling);
            if (lookup.IsLocal) { return null; }
            found = lookup.Declarations.Candidates;
            if (found.IsEmpty && name.Spelling is "default" or "size_of" or "align_of" or "offset_of"
                or "type_of" or "unreachable" or "assert" or "debug_assert") { return null; }
        }
        else if (target is AstMember { IsSafe: false } member && member.Name is not null)
        {
            var parts = new Stack<string>();
            AstExpression? node = member.Receiver;
            while (node is AstMember { IsSafe: false, Name: not null } part)
            {
                parts.Push(part.Name);
                node = part.Receiver;
            }
            if (node is not AstAtom { SyntaxKind: NodeKind.NameExpression } root || string.IsNullOrWhiteSpace(root.Spelling)) { return null; }
            var shadow = Sources.Lexical.Lookup(root, root.Spelling);
            if (!shadow.IsMissing) { return null; }
            parts.Push(root.Spelling);
            found = Sources.Declarations.LookupQualified(_files[call].Root, string.Join('.', parts), member.Name).Candidates;
            if (found.IsEmpty) { return null; }
        }
        else { return null; }
        if (found.Any(s => !_moduleFunctions.Contains(s))) { return null; }
        return found;
    }

    private static bool Better(CallCandidate left, CallCandidate right)
    {
        bool strictlyBetter = false;
        for (int index = 0; index < left.ConversionRanks.Length; index++)
        {
            if (left.ConversionRanks[index] > right.ConversionRanks[index]) { return false; }
            strictlyBetter |= left.ConversionRanks[index] < right.ConversionRanks[index];
        }
        if (strictlyBetter) { return true; }
        if (left.ExpandsVararg != right.ExpandsVararg) { return !left.ExpandsVararg; }
        if (left.DefaultParameters.Length != right.DefaultParameters.Length) { return left.DefaultParameters.Length < right.DefaultParameters.Length; }
        return left.Symbol.Arity == 0 && right.Symbol.Arity != 0;
    }

    private void Report(string code, string message, AstCall call)
    {
        if (Diagnostics.Count < Sources.Declarations.Options.DiagnosticLimit)
        {
            Diagnostics.Add(new(code, message, call.Origin.Source, call.Origin.Span));
        }
    }
}
