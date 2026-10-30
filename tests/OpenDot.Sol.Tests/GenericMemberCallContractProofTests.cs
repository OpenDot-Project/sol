// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericMemberCallContractProofTests
{
    [Fact]
    public void OutParameterAssignmentIsRetainedWithACompleteBodyProof()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func write(out destination: T, value: T) { destination = value }\n}\n"
            + "func use(owner: Root<int>): int { var result: int; owner.write(out result, 7); result }");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "write");
        Assert.Equal(AstPassingMode.Out, call.Target.Parameters[0].PassingMode);
        Assert.Equal("int", call.ParameterTypes[0].Name);
        Assert.Equal(BodyBindingStatus.Bound, analysis.GetGenericMemberCallProof(call.Call)!.Status);
        Assert.False(analysis.HasErrors);
        Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2242");
    }

    [Fact]
    public void MissingOutAssignmentCannotBeCertifiedByCallSignatureAlone()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func write(out destination: T, value: T) { }\n}\n"
            + "func use(owner: Root<int>): int { var result: int; owner.write(out result, 7); result }");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "write");
        Assert.Equal(BodyBindingStatus.Invalid, analysis.GetGenericMemberCallProof(call.Call)!.Status);
        Assert.True(analysis.HasErrors);
        Assert.Equal(BodyBindingStatus.Invalid,
            Assert.Single(analysis.Bodies, body => body.Declaration.Name == "use").Status);
    }

    [Fact]
    public void RefArgumentsKeepExactClosedStorageType()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func replace(ref destination: T, value: T) { destination = value }\n}\n"
            + "func use(owner: Root<int>): int { var result: int = 1; owner.replace(ref result, 7); result }");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "replace");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        Assert.Equal(AstPassingMode.Ref, call.Target.Parameters[0].PassingMode);
        Assert.Equal("int", call.ParameterTypes[0].Name);
        Assert.False(analysis.HasErrors);
    }

    [Fact]
    public void OmittedDefaultRemainsMappedToTheSelectedDeclaration()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func echo(value: T, repeat: int = 1): T = value\n}\n"
            + "func use(owner: Root<int>): int = owner.echo(7)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "echo");
        Assert.Equal(new[] { 1 }, call.DefaultParameters);
        Assert.Single(call.Arguments);
        Assert.Equal(BodyBindingStatus.Bound, analysis.GetGenericMemberCallProof(call.Call)!.Status);
    }

    [Fact]
    public void EmptyVarargDoesNotInventAnExplicitArgument()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func count(vararg values: T): int = 0\n}\n"
            + "func use(owner: Root<int>): int = owner.count()");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "count");
        Assert.Empty(call.Arguments);
        Assert.Equal(new[] { 0 }, call.EmptyVarargParameters);
        Assert.Equal(BodyBindingStatus.Bound, analysis.GetGenericMemberCallProof(call.Call)!.Status);
    }

    [Fact]
    public void NonEmptyVarargKeepsSourceArgumentOrder()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> {\n"
            + "func count(vararg values: T): int = 0\n}\n"
            + "func use(owner: Root<int>): int = owner.count(1, 2, 3)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "count");
        Assert.Equal(new[] { 0, 1, 2 }, call.Arguments.Select(argument => argument.ArgumentIndex));
        Assert.All(call.Arguments, argument => Assert.Equal(0, argument.ParameterIndex));
        Assert.Empty(call.EmptyVarargParameters);
        Assert.Equal(BodyBindingStatus.Bound, analysis.GetGenericMemberCallProof(call.Call)!.Status);
    }

    [Fact]
    public void SafeCallLiftsTheExpressionWithoutChangingMemberSignature()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<T> { func echo(value: T): T = value }\n"
            + "func use(owner: Root<int>?): int? = owner?.echo(7)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "echo");
        Assert.False(call.ReturnType!.IsNullable);
        Assert.True(analysis.GetExpression(call.Call)!.Type!.IsNullable);
        Assert.Equal(BodyBindingStatus.Bound, analysis.GetGenericMemberCallProof(call.Call)!.Status);
        Assert.False(analysis.HasErrors);
    }

    [Fact]
    public void OwnerAndMethodConstraintVectorsAreCheckedForTheAutomaticBody()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Widget: Object {}\n"
            + "class Root<T: Object> { func <U> accept(value: U): U where U: T = value }\n"
            + "func use(owner: Root<Object>, value: Widget): Widget = owner.accept<Widget>(value)");
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "accept");
        var proof = analysis.GetGenericMemberCallProof(call.Call)!;
        Assert.Equal(BodyBindingStatus.Bound, proof.Status);
        Assert.Equal("std.core.Object", Assert.Single(proof.Projection!.Body!.OwnerTypeArguments).Name);
        Assert.Equal("app.Widget", Assert.Single(proof.Projection.Body.TypeArguments).Name);
        Assert.False(analysis.HasErrors);
    }
}
