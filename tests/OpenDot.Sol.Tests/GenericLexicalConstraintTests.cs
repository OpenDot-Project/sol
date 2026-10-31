// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalConstraintTests
{
    [Fact]
    public void LocalNominalConstraintIsCheckedUnderTheClosedOwner()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <U: Marker> local(item: U): U = item\n"
            + "local<Marked>(value)", "interface Marker {}\nclass Marked: Marker {}", type: "Marked");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal("app.Marked", Assert.Single(local.Binding.TypeArguments).Name);
        Assert.Equal("app.Marked", local.SpecializedBody!.Summary.ReturnType!.Name);
        Assert.Empty(local.SpecializedBody.Diagnostics);
    }

    [Fact]
    public void ClosedOwnerSubstitutionRewritesConstructedLocalBounds()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Marker<V> {}\nclass Marked: Marker<int> {}\n"
            + "class Root<T> { func run(value: T, marked: Marked): T {\n"
            + "func <U: Marker<T>> local(item: U): T = value\nlocal<Marked>(marked)\n}\n}\n"
            + "func use(owner: Root<int>, value: int, marked: Marked): int = owner.run(value, marked)");
        var rootCall = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(rootCall.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal("app.Marked", Assert.Single(local.Binding.TypeArguments).Name);
        Assert.Equal("int", local.SpecializedBody!.Summary.ReturnType!.Name);
        Assert.Single(local.Binding.LexicalContext!.EnclosingArguments);
    }

    [Fact]
    public void UnsatisfiedClosedConstraintDoesNotBecomeAProof()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Marker<V> {}\nclass Marked: Marker<long> {}\n"
            + "class Root<T> { func run(value: T, marked: Marked): T {\n"
            + "func <U: Marker<T>> local(item: U): T = value\nlocal<Marked>(marked)\n}\n}\n"
            + "func use(owner: Root<int>, value: int, marked: Marked): int = owner.run(value, marked)");
        var rootCall = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(rootCall.Call)!;
        Assert.Equal(BodyBindingStatus.Invalid, root.Status);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.DoesNotContain(root.Dependencies, proof => proof.Status == BodyBindingStatus.Bound);
    }

    [Fact]
    public void APrimitiveDoesNotSatisfyAnObjectConstraintThroughBoxing()
    {
        var (analysis, root) = GenericLexicalTestSource.Member("func <U: Object> local(item: U): U = item\n"
            + "local<T>(value)");
        Assert.Equal(BodyBindingStatus.Invalid, root.Status);
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.DoesNotContain(root.Dependencies, proof => proof.Status == BodyBindingStatus.Bound);
    }

    [Fact]
    public void SameSpelledOwnerAndLocalParametersHaveSeparateIdentities()
    {
        var (_, root) = GenericLexicalTestSource.Member("func <T> local(item: T): T = item\n"
            + "local<String>(\"text\")\nvalue");
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        var outer = Assert.Single(local.Binding.LexicalContext!.EnclosingArguments);
        var inner = Assert.Single(local.Binding.TypeArguments);
        Assert.Equal("T", outer.Parameter.Name);
        Assert.Equal("int", outer.Argument.Name);
        Assert.Equal("std.core.String", inner.Name);
        Assert.Equal("std.core.String", local.SpecializedBody!.Summary.ReturnType!.Name);
    }

    [Fact]
    public void AClosedReferenceArgumentCanSatisfyAnEnclosingGenericBound()
    {
        var analysis = GenericMemberProofTestSource.Analyze("interface Marker<V> {}\n"
            + "class Marked: Marker<int> {}\nclass Root<T> {\n"
            + "func <V: Marker<T>> run(value: V): V {\n"
            + "func <U: Marker<T>> local(item: U): U = item\nlocal<V>(value)\n}\n}\n"
            + "func use(owner: Root<int>, value: Marked): Marked = owner.run<Marked>(value)");
        var rootCall = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(rootCall.Call)!;
        GenericLexicalTestSource.Bound(root);
        var local = GenericLexicalTestSource.Local(root);
        Assert.Equal(2, local.Binding.LexicalContext!.EnclosingArguments.Length);
        Assert.Equal("app.Marked", local.SpecializedBody!.Summary.ReturnType!.Name);
    }
}
