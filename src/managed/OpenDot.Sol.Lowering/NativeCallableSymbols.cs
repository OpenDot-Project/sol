// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;

namespace OpenDot.Sol.Lowering;

internal static class NativeCallableSymbols
{
    internal static Dictionary<int, string> Build(FirProgram program, CancellationToken cancellation = default)
    {
        var sources = program.Analysis.Sources;
        var files = sources.Declarations.Files.ToDictionary(file => file.Psi);
        var declarations = sources.Declarations.Files.SelectMany(file => file.Nodes.OfType<AstFunction>())
            .ToDictionary(function => function.Origin.Element);
        var names = new Dictionary<int, string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var function in program.Functions)
        {
            cancellation.ThrowIfCancellationRequested();
            if (function.RuntimeFunction != FirRuntimeFunction.None) { continue; }
            var file = files[function.Origin.Element.Snapshot];
            AstNode? lifted = (AstNode?)function.LambdaSource ?? function.AnonymousFunctionSource;
            var source = lifted is null ? declarations[function.Origin.Element] : EnclosingFunction(file, lifted);
            var declaration = sources.Declarations.GetDeclaredSymbol(source);
            var identity = new StringBuilder("sol-callable-v0;");
            void Atom(string value) => identity.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
            Atom(file.Root.ModuleName ?? throw new InvalidOperationException("A native callable requires a logical module."));
            Atom(declaration?.QualifiedName ?? throw new InvalidOperationException("A native callable requires a canonical source declaration."));
            Atom(source.TypeParameters.Count().ToString(CultureInfo.InvariantCulture));
            Atom(source.Parameters.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var parameter in source.Parameters)
            {
                Atom(parameter.PassingMode.ToString());
                Atom(FirNativeIdentity.Type(program.Analysis.Types, sources.Types.GetType(parameter.Type!), cancellation,
                    parameterIdentity => ReferenceEquals(parameterIdentity.Owner, source)
                        ? parameterIdentity.Ordinal.ToString(CultureInfo.InvariantCulture)
                        : throw new InvalidOperationException("A native callable has an unbound formal owner.")));
            }
            if (lifted is not null)
            {
                Atom(lifted.GetType().Name);
                int ordinal = 0;
                foreach (var node in source.Descendants().Where(node => node is AstLambda or AstAnonymousFunction))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (ReferenceEquals(node, lifted)) { break; }
                    ordinal++;
                }
                Atom(ordinal.ToString(CultureInfo.InvariantCulture));
            }
            Atom(function.SpecializationArguments.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var argument in function.SpecializationArguments)
            { Atom(FirNativeIdentity.Type(program.Analysis.Types, argument, cancellation)); }
            Atom(FirNativeIdentity.Type(program.Analysis.Types, function.ReturnType, cancellation));
            foreach (var effect in function.Effects.Select(type => FirNativeIdentity.Type(program.Analysis.Types, type, cancellation))
                .OrderBy(value => value, StringComparer.Ordinal)) { Atom(effect); }
            string symbol = FirNativeIdentity.Symbol(identity.ToString());
            if (!unique.Add(symbol)) { throw new InvalidOperationException("Native callable identity collision: " + symbol); }
            names.Add(function.Id, symbol);
        }
        return names;
    }

    private static AstFunction EnclosingFunction(AstSnapshot file, AstNode node)
    {
        for (var parent = file.GetParent(node); parent is not null; parent = file.GetParent(parent))
        { if (parent is AstFunction function) { return function; } }
        throw new InvalidOperationException("A lifted native callable requires an enclosing source function.");
    }
}
