// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Analysis;

internal sealed partial class BodyBindingBuilder
{
    private void CompleteConstructionProofs()
    {
        var contextual = _constructionOnlyErrors.Select(pair =>
            (pair.Item2, pair.Item1.Origin.Source, pair.Item1.Origin.Span)).ToHashSet();
        foreach (var pair in ConstructionProofs.ToArray())
        {
            _cancellation.ThrowIfCancellationRequested();
            var proof = pair.Value;
            Types.ValidateOwnership(proof.Selection.Owner);
            var dependencies = proof.Origins.Select(origin => origin.Site)
                .Concat(proof.Selection.Owner.Declaration?.Parts ?? [])
                .Append(proof.Selection.Site).ToArray();
            bool invalid = dependencies.Any(node => Diagnostics.Any(diagnostic =>
                ReferenceEquals(diagnostic.Source, node.Origin.Source)
                && diagnostic.Span.Start >= node.Origin.Span.Start && diagnostic.Span.End <= node.Origin.Span.End
                && !contextual.Contains((diagnostic.Code, diagnostic.Source, diagnostic.Span))));
            var status = invalid ? BodyBindingStatus.Invalid : proof.Status;
            ConstructionProofs[pair.Key] = proof with
            {
                Status = status,
                Reason = invalid ? "Construction depends on an invalid source declaration." : proof.Reason,
            };
            if (invalid && proof.Selection.Site is AstCall)
            { Report("SOL2277", "The selected constructor has an invalid construction dependency.", proof.Selection.Site); }
            PropagateInterfaceFailure(proof.Selection.Site, status);
        }
    }
}
