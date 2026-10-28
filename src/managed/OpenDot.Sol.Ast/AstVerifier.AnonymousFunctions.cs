// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Linq;
using OpenDot.Sol.Cst;
using OpenDot.Sol.Psi;

namespace OpenDot.Sol.Ast;

public static partial class AstVerifier
{
    private static void VerifyAnonymousFunction(AstSnapshot snapshot, AstAnonymousFunction function)
    {
        VerifyAnonymousParameters(snapshot, function);
        if (function.ReturnType is { } returnType)
        {
            VerifyDirectReference(snapshot, function, returnType, "return type");
        }
        foreach (var thrownType in function.ThrownTypes)
        {
            VerifyDirectReference(snapshot, function, thrownType, "checked-effect type");
        }

        var bodyNode = function.Body.Node;
        bool validBody = function.Body.Kind switch
        {
            AstBodyKind.Missing => bodyNode is null,
            AstBodyKind.Expression => bodyNode is AstExpression,
            AstBodyKind.Block => bodyNode is AstBlock,
            _ => false,
        };
        if (!validBody)
        {
            throw new InvalidOperationException("An anonymous-function body kind does not match its node.");
        }
        if (bodyNode is not null)
        {
            VerifyDirectReference(snapshot, function, bodyNode, "body");
        }
    }

    private static void VerifyAnonymousParameters(AstSnapshot snapshot, AstAnonymousFunction function)
    {
        var parameterList = function.Children.FirstOrDefault(child => child.SyntaxKind == NodeKind.ParameterList);
        foreach (var parameter in function.Parameters)
        {
            if (parameter.GroupOrigin.Element is not PsiParameterGroup group
                || !ReferenceEquals(group.Snapshot, snapshot.Psi))
            {
                throw new InvalidOperationException("An anonymous-function parameter group has an invalid origin.");
            }
            var groupNode = snapshot.GetNode(group);
            if (parameterList is null || !ReferenceEquals(snapshot.GetParent(groupNode), parameterList)
                || !group.Names.Any(name => ReferenceEquals(name, parameter.NameOrigin.Element)))
            {
                throw new InvalidOperationException("An anonymous-function parameter does not belong to its source group.");
            }
            if (parameter.Type is { } parameterType
                && !ReferenceEquals(snapshot.GetParent(parameterType), groupNode))
            {
                throw new InvalidOperationException("An anonymous-function parameter type is outside its source group.");
            }
            if (parameter.DefaultValue is { } defaultValue
                && !ReferenceEquals(snapshot.GetParent(defaultValue), groupNode))
            {
                throw new InvalidOperationException("An anonymous-function parameter default is outside its source group.");
            }
        }
    }

    private static void VerifyDirectReference(AstSnapshot snapshot, AstAnonymousFunction function,
        AstNode referencedNode, string role)
    {
        snapshot.ValidateOwnership(referencedNode);
        if (!ReferenceEquals(snapshot.GetParent(referencedNode), function))
        {
            throw new InvalidOperationException($"An anonymous-function {role} is not a direct child.");
        }
    }
}
