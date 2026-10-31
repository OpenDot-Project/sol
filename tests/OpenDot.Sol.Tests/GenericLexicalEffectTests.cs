// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalEffectTests
{
    [Fact]
    public void CapturedExceptionTypeIsSubstitutedInTheLocalEffectBudget()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Failure: Exception {}\n"
            + "class Root<T: Exception> { func run(error: T): int throws T {\n"
            + "func <U> local(item: U): int throws T = throw error\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<Failure>, error: Failure): int throws Failure = owner.run(error)");
        var rootCall = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(rootCall.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var effect = Assert.Single(local.SpecializedBody!.Summary.EscapingEffects);
        Assert.Equal("app.Failure", effect.Name);
        Assert.Equal("app.Failure", GenericLexicalTestSource.Fact(local, "error").Type!.Name);
        Assert.DoesNotContain(local.SpecializedBody.Diagnostics, diagnostic => diagnostic.Code == "SOL2250");
    }

    [Fact]
    public void ALocalBodyCannotHideAnUndeclaredCapturedCheckedEffect()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Failure: Exception {}\n"
            + "class Root<T: Exception> { func run(error: T): int {\n"
            + "func <U> local(item: U): int = throw error\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<Failure>, error: Failure): int = owner.run(error)");
        var rootCall = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(rootCall.Call)!;
        GenericLexicalTestSource.Invalid(analysis, root, "SOL2250");
        var local = GenericLexicalTestSource.Local(root);
        Assert.Contains(local.SpecializedBody!.Diagnostics, diagnostic => diagnostic.Code == "SOL2250");
        Assert.Equal(BodyBindingStatus.Invalid, local.Status);
    }

    [Fact]
    public void RuntimeExceptionCaptureDoesNotInventACheckedEffect()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Failure: RuntimeException {}\n"
            + "class Root<T: RuntimeException> { func run(error: T): int {\n"
            + "func <U> local(item: U): int = throw error\nlocal<int>(1)\n}\n}\n"
            + "func use(owner: Root<Failure>, error: Failure): int = owner.run(error)");
        var rootCall = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(rootCall.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Empty(local.SpecializedBody!.Summary.EscapingEffects);
        Assert.DoesNotContain(local.SpecializedBody.Diagnostics, diagnostic => diagnostic.Code == "SOL2250");
    }

    [Fact]
    public void CatchInsideTheLocalBodyHandlesItsCapturedEffect()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Failure: Exception {}\n"
            + "class Root<T: Exception> { func run(error: T): int {\n"
            + "func <U> local(item: U) {\ntry { throw error } catch (caught: Exception) {}\n}\n"
            + "local<int>(1)\n42\n}\n}\n"
            + "func use(owner: Root<Failure>, error: Failure): int = owner.run(error)");
        var rootCall = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(rootCall.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Empty(local.SpecializedBody!.Summary.EscapingEffects);
        Assert.Empty(local.SpecializedBody.Diagnostics);
    }

    [Fact]
    public void NestedLocalEffectsUseTheirOwnMethodArguments()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Failure: Exception {}\n"
            + "class Root<T> { func run(error: Failure): int throws Failure {\n"
            + "func <E: Exception> local(problem: E): int throws E = throw problem\n"
            + "local<Failure>(error)\n}\n}\n"
            + "func use(owner: Root<int>, error: Failure): int throws Failure = owner.run(error)");
        var rootCall = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(rootCall.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal("app.Failure", Assert.Single(local.Binding.TypeArguments).Name);
        Assert.Equal("app.Failure", Assert.Single(local.SpecializedBody!.Summary.EscapingEffects).Name);
        Assert.Equal("int", Assert.Single(local.Binding.LexicalContext!.EnclosingArguments).Argument.Name);
    }
}
