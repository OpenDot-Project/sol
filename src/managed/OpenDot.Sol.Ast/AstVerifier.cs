// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Threading;

namespace OpenDot.Sol.Ast;

/// <summary>Checks source-model invariants. This verifier does not certify type or runtime safety.</summary>
public static class AstVerifier
{
    public static void Verify(AstSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var ids = new HashSet<int>();
        var visited = new HashSet<AstNode>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<AstNode>();
        pending.Push(snapshot.Root);
        while (pending.TryPop(out var node))
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot.ValidateOwnership(node);
            if (!ids.Add(node.Id) || !visited.Add(node))
            {
                throw new InvalidOperationException("An AST node or ID occurs more than once.");
            }
            node.Origin.Source.Validate(node.Origin.Span);
            foreach (var child in node.Children)
            {
                if (!ReferenceEquals(snapshot.GetParent(child), node))
                {
                    throw new InvalidOperationException("AST parent mapping is inconsistent.");
                }
                if (child.Origin.Span.Start < node.Origin.Span.Start || child.Origin.Span.End > node.Origin.Span.End)
                {
                    throw new InvalidOperationException("A child AST origin is outside its parent.");
                }
                pending.Push(child);
            }
            if (node is AstCallable callable)
            {
                foreach (var parameter in callable.Parameters)
                {
                    if (parameter.Type is not null) { snapshot.ValidateOwnership(parameter.Type); }
                    if (parameter.DefaultValue is not null) { snapshot.ValidateOwnership(parameter.DefaultValue); }
                    if (!ReferenceEquals(parameter.GroupOrigin.Element.Snapshot, snapshot.Psi)
                        || !ReferenceEquals(parameter.NameOrigin.Element.Snapshot, snapshot.Psi))
                    {
                        throw new InvalidOperationException("A parameter origin belongs to another snapshot.");
                    }
                }
            }
        }
        if (visited.Count != snapshot.Nodes.Length || snapshot.GetParent(snapshot.Root) is not null)
        {
            throw new InvalidOperationException("The AST has detached nodes or a parented root.");
        }
    }
}
