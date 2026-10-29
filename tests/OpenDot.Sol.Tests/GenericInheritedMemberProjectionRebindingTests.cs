// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericInheritedMemberProjectionRebindingTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\nclass IOException: Exception {}";

    [Fact]
    public void ProjectedOwnerRebindingRepeatsOverloadSelectionWithClosedReceiverType()
    {
        var analysis = Analyze("module app\n"
            + "class Widget: Object {}\n"
            + "open class Root<T: Object> {\n"
            + "  func choose(value: Object): int = 1\n"
            + "  func choose(value: Widget): String = \"closed\"\n"
            + "  func resolve(value: T) = choose(value)\n"
            + "}\n"
            + "open class Middle<T: Object>: Root<T> {}\n"
            + "class Leaf<T: Object>: Middle<T> {}");
        var root = Type(analysis, "Root");
        var method = Method(root, "resolve");
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "Widget"));
        var openCall = Assert.Single(analysis.Calls, call => call.Target.Name == "choose");
        Assert.Equal("std.core.Object", Assert.Single(openCall.ParameterTypes).Name);
        Assert.Equal("int", openCall.ReturnType!.Name);

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        Assert.Equal("app.Root", Assert.Single(result.Projection.Candidates).ConstructedOwner.Name);
        Assert.Equal("std.core.String", result.Body!.Summary.ReturnType!.Name);
        Assert.Equal(BodyBindingStatus.Bound, result.Body.Summary.Status);
        var closedCall = Assert.Single(result.Body.Calls);
        Assert.Equal("choose", closedCall.Target.Name);
        Assert.Equal("app.Widget", Assert.Single(closedCall.ParameterTypes).Name);
        Assert.Equal("std.core.String", closedCall.ReturnType!.Name);
    }

    [Fact]
    public void ProjectedOwnerAndMethodConstraintsAreCheckedTogether()
    {
        var analysis = Analyze("module app\n"
            + "interface Named {}\n"
            + "class Entity: Object, Named {}\n"
            + "class Other: Object {}\n"
            + "open class Root<T> { func <U> accept(value: U): U where U: T = value }\n"
            + "open class Middle<T>: Root<T> {}\n"
            + "class Leaf<T>: Middle<T> {}");
        var method = Method(Type(analysis, "Root"), "accept");
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "Named"));
        var entity = Nominal(analysis, "Entity");
        var other = Nominal(analysis, "Other");

        var accepted = analysis.GetGenericMemberBodyForReceiver(method, receiver, [entity],
            TestContext.Current.CancellationToken);
        var rejected = analysis.GetGenericMemberBodyForReceiver(method, receiver, [other],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, accepted.Projection.Status);
        Assert.Equal(BodyBindingStatus.Bound, accepted.Body!.Summary.Status);
        Assert.Equal(entity, accepted.Body.Summary.ReturnType);
        Assert.Equal(OwnerProjectionStatus.Unique, rejected.Projection.Status);
        Assert.Equal(BodyBindingStatus.Invalid, rejected.Body!.Summary.Status);
        Assert.Contains(rejected.Body.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.NotSame(accepted.Body, rejected.Body);
    }

    [Fact]
    public void ProjectedBodyRecomputesNullableResultAndCheckedEffects()
    {
        var analysis = Analyze("module app\n"
            + "func read(): () throws IOException {}\n"
            + "open class Root<T> { func echo(value: T?): T? throws IOException { read(); value } }\n"
            + "open class Middle<T>: Root<T> {}\n"
            + "class Leaf<T>: Middle<T> {}");
        var method = Method(Type(analysis, "Root"), "echo");
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));

        var result = analysis.GetGenericMemberBodyForReceiver(method, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, result.Projection.Status);
        Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
        Assert.True(result.Body.Summary.ReturnType!.IsNullable);
        Assert.Equal(Nominal(analysis, "String"), result.Body.Summary.ReturnType.Arguments[0]);
        Assert.Contains(result.Body.Summary.EscapingEffects,
            effect => effect.Name == "std.core.IOException");
        Assert.Contains(result.Body.Calls, call => call.Target.Name == "read");
    }

    [Fact]
    public void ProjectedBodyRechecksRefLikeOutAssignmentRules()
    {
        var analysis = Analyze("module app\n"
            + "open class Root<T> {\n"
            + "  func assign(out target: T, value: T): () { target = value }\n"
            + "  func missing(out target: T): () {}\n"
            + "}\n"
            + "open class Middle<T>: Root<T> {}\n"
            + "class Leaf<T>: Middle<T> {}", allowSourceErrors: true);
        var root = Type(analysis, "Root");
        var receiver = Constructed(analysis, "Leaf", Nominal(analysis, "String"));
        var assign = Method(root, "assign");
        var missing = Method(root, "missing");

        var assigned = analysis.GetGenericMemberBodyForReceiver(assign, receiver, [],
            TestContext.Current.CancellationToken);
        var unassigned = analysis.GetGenericMemberBodyForReceiver(missing, receiver, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(OwnerProjectionStatus.Unique, assigned.Projection.Status);
        Assert.Equal(BodyBindingStatus.Bound, assigned.Body!.Summary.Status);
        Assert.Equal(OwnerProjectionStatus.Unique, unassigned.Projection.Status);
        Assert.Equal(BodyBindingStatus.Invalid, unassigned.Body!.Summary.Status);
        Assert.Contains(unassigned.Body.Diagnostics, diagnostic => diagnostic.Code == "SOL2242");
    }

    [Fact]
    public void InheritedBodyViewDoesNotChangeOrdinaryFirAdmission()
    {
        const string declarations = "class Widget: Object {}\n"
            + "open class Root<T: Object> { func identity(value: T): T = value }\n"
            + "open class Middle<T: Object>: Root<T> {}\n"
            + "class Leaf<T: Object>: Middle<T> {}\n"
            + "func use(value: Leaf<Widget>, widget: Widget): Widget = value.identity(widget)";
        var session = AstTestSources.Session("module app\n" + declarations, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics
            .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var root = Type(analysis, "Root");
        var method = Method(root, "identity");
        var call = Assert.Single(analysis.Calls, binding => ReferenceEquals(binding.Target, method));
        var result = analysis.GetGenericMemberBodyForReceiver(method, call.Receiver!, call.TypeArguments,
            TestContext.Current.CancellationToken);
        var compilation = FirCompilation.Create([AstTestSources.Parse(declarations, "app")],
            TestContext.Current.CancellationToken);

        Assert.Equal(BodyBindingStatus.Bound, result.Body!.Summary.Status);
        Assert.Equal("app.Root", result.Body.ConstructedOwner!.Name);
        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
    }

    private static BodyAnalysis Analyze(string source, bool allowSourceErrors = false)
    {
        var session = AstTestSources.Session(source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        if (!allowSourceErrors)
        {
            Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics
                .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message)));
        }
        return BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
    }

    private static AstTypeDeclaration Type(BodyAnalysis analysis, string name) =>
        analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>().Single(type => type.Name == name);

    private static AstFunction Method(AstTypeDeclaration owner, string name) =>
        owner.Members.OfType<AstFunction>().Single(method => method.Name == name);

    private static ResolvedType Nominal(BodyAnalysis analysis, string name) => analysis.Types.Nominal(
        analysis.Sources.Declarations.GetDeclaredSymbol(Type(analysis, name))!, []);

    private static ResolvedType Constructed(BodyAnalysis analysis, string name, params ResolvedType[] arguments) =>
        analysis.Types.Nominal(analysis.Sources.Declarations.GetDeclaredSymbol(Type(analysis, name))!,
            arguments.ToImmutableArray());
}
