// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Generic;
using System.Linq;
using OpenDot.Sol.Analysis;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericLexicalSubstitutionOracleTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return ["A", "B", "C", "D", "E", "int", "String", "long", "bool", "double"];
        yield return ["T", "T", "T", "T", "T", "int", "String", "long", "bool", "double"];
        yield return ["X", "Y", "X", "Y", "X", "String", "int", "double", "long", "bool"];
        yield return ["Item", "Other", "Item", "Other", "Item", "[]int", "String?", "int?", "bool", "long"];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void LexicalVectorsAndCapturedTypesMatchDeclarationPositionRatherThanSpelling(
        string firstName, string secondName, string methodName, string middleName, string localName,
        string firstType, string secondType, string methodType, string middleType, string localType)
    {
        // The two owner parameters must have distinct names. Deliberate shadowing starts at
        // method boundaries, where each declaration owns an independent parameter identity.
        if (firstName == secondName) { secondName += "Other"; }
        if (methodName == firstName || methodName == secondName) { methodName += "Method"; }
        var source = "class Root<" + firstName + ", " + secondName + "> {\n"
            + "func <" + methodName + "> run(first: " + firstName + ", second: " + secondName
            + ", third: " + methodName + ", fourth: " + middleType + ", fifth: " + localType + ") {\n"
            + "func <" + middleName + "> middle(item: " + middleName + ") {\n"
            + "func <" + localName + "> local(other: " + localName + ") { first; second; third; item; other }\n"
            + "local<" + localType + ">(fifth)\n}\n"
            + "middle<" + middleType + ">(fourth)\n}\n}\n"
            + "func use(owner: Root<" + firstType + ", " + secondType + ">, first: " + firstType
            + ", second: " + secondType + ", third: " + methodType + ", fourth: " + middleType
            + ", fifth: " + localType + ") { owner.run<" + methodType + ">(first, second, third, fourth, fifth) }";
        var analysis = GenericMemberProofTestSource.Analyze(source);
        var call = GenericMemberProofTestSource.ClosedCall(analysis, "run");
        var root = analysis.GetGenericMemberCallProof(call.Call)!;
        GenericLexicalTestSource.Bound(root);
        var middle = GenericLexicalTestSource.Local(root, "middle");
        var local = Assert.Single(middle.Dependencies);
        var expected = new[] { firstType, secondType, methodType, middleType }
            .Select(type => CanonicalName(type)).ToArray();
        Assert.Equal(expected, local.Binding.LexicalContext!.EnclosingArguments.Select(pair => Display(pair.Argument)));
        Assert.Equal(4, local.Binding.LexicalContext.EnclosingArguments.Select(pair => pair.Parameter).Distinct().Count());
        Assert.Equal(CanonicalName(localType), Display(Assert.Single(local.Binding.TypeArguments)));
        var names = new[] { "first", "second", "third", "item", "other" };
        var types = expected.Concat([CanonicalName(localType)]).ToArray();
        for (int index = 0; index < names.Length; index++)
        {
            var read = Assert.Single(GenericLexicalTestSource.Reads(local, names[index]));
            Assert.Equal(types[index], Display(read.Type!));
            Assert.Equal(BodyBindingStatus.Bound, read.Status);
        }
    }

    [Fact]
    public void ReorderingTheOwnerVectorChangesOnlyTheCorrespondingCapturedTypes()
    {
        var analysis = GenericMemberProofTestSource.Analyze("class Root<A, B> {\n"
            + "func run(first: A, second: B) {\n"
            + "func <U> local(item: U) { first; second; item }\nlocal<bool>(true)\n}\n}\n"
            + "func left(owner: Root<int, String>, first: int, second: String) { owner.run(first, second) }\n"
            + "func right(owner: Root<String, int>, first: String, second: int) { owner.run(first, second) }");
        var calls = analysis.Calls.Where(call => call.Target.Name == "run").ToArray();
        Assert.Equal(2, calls.Length);
        var roots = calls.Select(call => analysis.GetGenericMemberCallProof(call.Call)!).ToArray();
        Assert.All(roots, GenericLexicalTestSource.Bound);
        var left = GenericLexicalTestSource.Local(roots[0]);
        var right = GenericLexicalTestSource.Local(roots[1]);
        Assert.Equal(new[] { "int", "std.core.String" }, new[]
        {
            Assert.Single(GenericLexicalTestSource.Reads(left, "first")).Type!.Name,
            Assert.Single(GenericLexicalTestSource.Reads(left, "second")).Type!.Name,
        });
        Assert.Equal(new[] { "std.core.String", "int" }, new[]
        {
            Assert.Single(GenericLexicalTestSource.Reads(right, "first")).Type!.Name,
            Assert.Single(GenericLexicalTestSource.Reads(right, "second")).Type!.Name,
        });
        Assert.Equal("bool", Assert.Single(GenericLexicalTestSource.Reads(left, "item")).Type!.Name);
        Assert.Equal("bool", Assert.Single(GenericLexicalTestSource.Reads(right, "item")).Type!.Name);
        Assert.NotSame(left.SpecializedBody, right.SpecializedBody);
    }

    private static string CanonicalName(string source) => source switch
    {
        "String" => "std.core.String",
        "String?" => "std.core.String?",
        _ => source,
    };

    private static string Display(ResolvedType type) => type.Kind switch
    {
        ResolvedTypeKind.Array => "[]" + Display(Assert.Single(type.Arguments)),
        ResolvedTypeKind.Nullable => Display(Assert.Single(type.Arguments)) + "?",
        _ => type.Name,
    };
}
