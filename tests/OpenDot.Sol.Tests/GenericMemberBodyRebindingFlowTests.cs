// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Fir;
using Xunit;

namespace OpenDot.Sol.Tests;

public sealed class GenericMemberBodyRebindingFlowTests
{
    private const string Core = "module std.core\nopen class Object {}\nclass String: Object {}\n"
        + "open class Throwable: Object {}\nopen class Exception: Throwable {}\n"
        + "open class RuntimeException: Exception {}\nclass IOException: Exception {}\n";

    [Fact]
    public void ClosedOwnerRebindingCanSelectAnOverloadUnavailableToTheOpenBody()
    {
        var fixture = Create("class Widget: Object {}\n"
            + "class Holder<T: Object> {\n"
            + "  func choose(value: Object): int = 1\n"
            + "  func choose(value: Widget): String = \"closed\"\n"
            + "  func resolve(value: T) = choose(value)\n"
            + "}", "Holder", "resolve");

        Assert.False(fixture.Analysis.HasErrors, Describe(fixture.Analysis));
        var openCall = Assert.Single(fixture.Analysis.Calls, call => call.Target.Name == "choose");
        Assert.Equal("std.core.Object", Assert.Single(openCall.ParameterTypes).Name);
        Assert.Equal("int", openCall.ReturnType!.Name);

        var widget = Nominal(fixture.Analysis, "Widget");
        var owner = ConstructedOwner(fixture.Analysis, fixture.Owner, widget);
        var specialized = fixture.Analysis.GetGenericMemberBody(fixture.Method, owner, [],
            TestContext.Current.CancellationToken);

        Assert.Equal(BodyBindingStatus.Bound, specialized.Summary.Status);
        Assert.Equal("std.core.String", specialized.Summary.ReturnType!.Name);
        var closedCall = Assert.Single(specialized.Calls);
        Assert.Equal("choose", closedCall.Target.Name);
        Assert.Equal("app.Widget", Assert.Single(closedCall.ParameterTypes).Name);
        Assert.Equal("std.core.String", closedCall.ReturnType!.Name);
    }

    [Fact]
    public void OwnerAndSameNamedMethodParametersKeepSeparateIdentitiesAndMaps()
    {
        var fixture = Create("class Widget: Object {}\n"
            + "class Holder<T> { func <T> echo(value: T): T { this; value } }", "Holder", "echo");
        var ownerParameter = Assert.Single(fixture.Analysis.Sources.Types.GetTypeParameters(fixture.Owner));
        var methodParameter = Assert.Single(fixture.Analysis.Sources.Types.GetTypeParameters(fixture.Method));
        Assert.NotSame(ownerParameter, methodParameter);

        var widget = Nominal(fixture.Analysis, "Widget");
        var text = Nominal(fixture.Analysis, "String");
        var specialized = fixture.Analysis.GetGenericMemberBody(fixture.Method,
            ConstructedOwner(fixture.Analysis, fixture.Owner, widget), [text],
            TestContext.Current.CancellationToken);

        Assert.Equal(BodyBindingStatus.Bound, specialized.Summary.Status);
        Assert.Equal(widget, Assert.Single(specialized.OwnerTypeArguments));
        Assert.Equal(text, Assert.Single(specialized.TypeArguments));
        Assert.Equal(text, specialized.Summary.ReturnType);
        var receiver = fixture.Method.Descendants().OfType<AstAtom>().Single(atom => atom.Spelling == "this");
        var receiverType = specialized.GetExpression(receiver)!.Type!;
        Assert.Equal(fixture.Analysis.Sources.Declarations.GetDeclaredSymbol(fixture.Owner), receiverType.Declaration);
        Assert.Equal(widget, Assert.Single(receiverType.Arguments));
        var value = fixture.Method.Descendants().OfType<AstAtom>().Last(atom => atom.Spelling == "value");
        Assert.Equal(text, specialized.GetExpression(value)!.Type);
    }

    [Fact]
    public void MethodConstraintIsRecheckedAfterTheOwnerMapIsApplied()
    {
        var fixture = Create("interface Named {}\nclass Entity: Object, Named {}\nclass Other: Object {}\n"
            + "class Holder<T> { func <U> accept(value: U): U where U: T = value }", "Holder", "accept");
        var named = Nominal(fixture.Analysis, "Named");
        var entity = Nominal(fixture.Analysis, "Entity");
        var other = Nominal(fixture.Analysis, "Other");
        var owner = ConstructedOwner(fixture.Analysis, fixture.Owner, named);

        var accepted = fixture.Analysis.GetGenericMemberBody(fixture.Method, owner, [entity],
            TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Bound, accepted.Summary.Status);
        Assert.Equal(entity, accepted.Summary.ReturnType);

        var rejected = fixture.Analysis.GetGenericMemberBody(fixture.Method, owner, [other],
            TestContext.Current.CancellationToken);
        var rejectedAgain = fixture.Analysis.GetGenericMemberBody(fixture.Method, owner, [other],
            TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Invalid, rejected.Summary.Status);
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
        Assert.NotSame(rejected, rejectedAgain);
    }

    [Fact]
    public void OwnerConstraintsAreCheckedForTheConstructedType()
    {
        var fixture = Create("interface Named {}\nclass Entity: Object, Named {}\nclass Other: Object {}\n"
            + "class Holder<T: Named> { func value(input: T): T = input }", "Holder", "value");
        var entity = Nominal(fixture.Analysis, "Entity");
        var other = Nominal(fixture.Analysis, "Other");

        var accepted = fixture.Analysis.GetGenericMemberBody(fixture.Method,
            ConstructedOwner(fixture.Analysis, fixture.Owner, entity), [],
            TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Bound, accepted.Summary.Status);

        var rejected = fixture.Analysis.GetGenericMemberBody(fixture.Method,
            ConstructedOwner(fixture.Analysis, fixture.Owner, other), [],
            TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Invalid, rejected.Summary.Status);
        Assert.Contains(rejected.Diagnostics, diagnostic => diagnostic.Code == "SOL2221");
    }

    [Fact]
    public void ClosedOwnerRebindRefreshesNullableTypesAndCheckedEffects()
    {
        var fixture = Create("func read(): () throws IOException {}\n"
            + "class Holder<T> { func echo(value: T?): T? throws IOException { read(); value } }",
            "Holder", "echo");
        var text = Nominal(fixture.Analysis, "String");
        var specialized = fixture.Analysis.GetGenericMemberBody(fixture.Method,
            ConstructedOwner(fixture.Analysis, fixture.Owner, text), [],
            TestContext.Current.CancellationToken);

        Assert.Equal(BodyBindingStatus.Bound, specialized.Summary.Status);
        Assert.True(specialized.Summary.ReturnType!.IsNullable);
        Assert.Equal(text, specialized.Summary.ReturnType.Arguments[0]);
        Assert.Contains(specialized.Summary.EscapingEffects,
            effect => effect.Name == "std.core.IOException");
        Assert.Contains(specialized.Calls, call => call.Target.Name == "read");
    }

    [Fact]
    public void OutAssignmentIsRecheckedInTheClosedMemberBody()
    {
        var fixture = Create("class Holder<T> {\n"
            + "  func assign(out target: T, value: T): () { target = value }\n"
            + "  func missing(out target: T): () {}\n"
            + "}", "Holder", "assign");
        var text = Nominal(fixture.Analysis, "String");
        var owner = ConstructedOwner(fixture.Analysis, fixture.Owner, text);

        var assigned = fixture.Analysis.GetGenericMemberBody(fixture.Method, owner, [],
            TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Bound, assigned.Summary.Status);

        var missing = fixture.Owner.Members.OfType<AstFunction>().Single(function => function.Name == "missing");
        var unassigned = fixture.Analysis.GetGenericMemberBody(missing, owner, [],
            TestContext.Current.CancellationToken);
        Assert.Equal(BodyBindingStatus.Invalid, unassigned.Summary.Status);
        Assert.Contains(unassigned.Diagnostics, diagnostic => diagnostic.Code == "SOL2242");
    }

    [Fact]
    public void BoundSourceBodyViewDoesNotAdmitAGenericOwnerToFir()
    {
        const string source = "class Widget: Object {}\n"
            + "class Holder<T: Object> { func identity(value: T): T = value }\n"
            + "func use(holder: Holder<Widget>, value: Widget): Widget = holder.identity(value)";
        var session = AstTestSources.Session("module app\n" + source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var ownerDeclaration = session.Files.SelectMany(file => file.Nodes).OfType<AstTypeDeclaration>()
            .Single(declaration => declaration.Name == "Holder");
        var method = Assert.Single(ownerDeclaration.Members.OfType<AstFunction>());
        var call = Assert.Single(analysis.Calls, binding => ReferenceEquals(binding.Target, method));

        var bodyView = analysis.GetGenericMemberBody(method, call.Receiver!, call.TypeArguments,
            TestContext.Current.CancellationToken);
        var compilation = FirCompilation.Create([AstTestSources.Parse(source, "app")],
            TestContext.Current.CancellationToken);

        Assert.Equal(BodyBindingStatus.Bound, bodyView.Summary.Status);
        Assert.Null(compilation.Program);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Code == "SOL2400");
    }

    private static (BodyAnalysis Analysis, AstTypeDeclaration Owner, AstFunction Method) Create(
        string source, string ownerName, string methodName)
    {
        var session = AstTestSources.Session("module app\n" + source, Core);
        var sources = SourceAnalysis.Create(session, TestContext.Current.CancellationToken);
        Assert.False(sources.HasErrors, string.Join('\n', sources.Diagnostics.Select(d => d.Code + " " + d.Message)));
        var analysis = BodyAnalysis.Create(sources, TestContext.Current.CancellationToken);
        var owner = sources.Declarations.Files.SelectMany(file => file.Nodes).OfType<AstTypeDeclaration>()
            .Single(declaration => declaration.Name == ownerName);
        var method = owner.Members.OfType<AstFunction>().Single(function => function.Name == methodName);
        return (analysis, owner, method);
    }

    private static ResolvedType Nominal(BodyAnalysis analysis, string name)
    {
        var declaration = analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
            .OfType<AstTypeDeclaration>().Single(type => type.Name == name);
        return analysis.Types.Nominal(analysis.Sources.Declarations.GetDeclaredSymbol(declaration)!, []);
    }

    private static ResolvedType ConstructedOwner(BodyAnalysis analysis, AstTypeDeclaration declaration,
        params ResolvedType[] arguments) => analysis.Types.Nominal(
            analysis.Sources.Declarations.GetDeclaredSymbol(declaration)!, arguments.ToImmutableArray());

    private static string Describe(BodyAnalysis analysis) => string.Join('\n', analysis.Diagnostics
        .Select(diagnostic => diagnostic.Code + " " + diagnostic.Message));
}
