// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;
using OpenDot.Sol.Cst;

namespace OpenDot.Sol.Fir;

public static partial class FirVerifier
{
    private sealed partial class Checker
    {
        private readonly Dictionary<object, int> _sourceCallMatches = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, int> _sourceOperatorMatches = new(ReferenceEqualityComparer.Instance);

        private void CheckSourceCorrespondence()
        {
            _sourceCallMatches.Clear();
            _sourceOperatorMatches.Clear();
            var source = program.Analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
                .OfType<AstFunction>().SingleOrDefault(function => ReferenceEquals(function.Origin.Element, _function.Origin.Element));
            if (source?.Body.Node is not { } sourceBody)
            {
                Fail("FIR body has no source body to correspond to.", _function.Origin);
                return;
            }

            if (!Enter(_function.Body!)) { return; }
            try
            {
                if (sourceBody is AstBlock sourceBlock)
                {
                    MatchFunctionBlock(_function.Body!, sourceBlock);
                }
                else if (sourceBody is AstExpression expression && source.Body.Kind == AstBodyKind.Expression)
                {
                    if (!ReferenceEquals(_function.Body!.Origin.Element, _function.Origin.Element))
                    { Fail("Expression-bodied FIR wrapper differs from its source declaration.", _function.Body.Origin); }
                    if (_function.Body.Statements is not [FirReturn { Value: { } value } ret])
                    { Fail("Expression-bodied FIR must contain exactly one value return.", _function.Body.Origin); }
                    else
                    {
                        SameOrigin(ret, expression);
                        MatchExpression(value, expression);
                    }
                }
                else
                {
                    Fail("FIR body form differs from the source function body.", _function.Body!.Origin);
                }
            }
            finally { Leave(_function.Body!); }

            var expectedCalls = new HashSet<object>(ReferenceEqualityComparer.Instance);
            if (sourceBody is AstCall rootCall) { expectedCalls.Add(rootCall.Origin.Element); }
            foreach (var call in sourceBody.Descendants().OfType<AstCall>()) { expectedCalls.Add(call.Origin.Element); }
            foreach (var call in expectedCalls)
            {
                if (!_sourceCallMatches.TryGetValue(call, out int count) || count == 0)
                { Fail("A source-bound call is absent from FIR.", source.Origin); }
                else if (count != 1) { Fail("A source-bound call is represented more than once in FIR.", source.Origin); }
            }
            foreach (var call in _sourceCallMatches.Keys)
            {
                if (!expectedCalls.Contains(call)) { Fail("FIR contains a call outside the source function body.", _function.Origin); }
            }

            var expectedOperators = new HashSet<object>(ReferenceEqualityComparer.Instance);
            if (sourceBody is AstOperation or AstAssignment && GetSourceOperator(sourceBody.Origin.Element) is not null)
            { expectedOperators.Add(sourceBody.Origin.Element); }
            foreach (var site in sourceBody.Descendants().Where(node => node is AstOperation or AstAssignment)
                .Where(node => GetSourceOperator(node.Origin.Element) is not null))
            { expectedOperators.Add(site.Origin.Element); }
            foreach (var site in expectedOperators)
            {
                if (!_sourceOperatorMatches.TryGetValue(site, out int count) || count == 0)
                { Fail("A source-bound operator is absent from FIR.", source.Origin); }
                else if (count != 1) { Fail("A source-bound operator is represented more than once in FIR.", source.Origin); }
            }
            foreach (var site in _sourceOperatorMatches.Keys)
            {
                if (!expectedOperators.Contains(site)) { Fail("FIR contains an operator call outside the source function body.", _function.Origin); }
            }
        }

        private void MatchFunctionBlock(FirBlock actual, AstBlock source)
        {
            SameOrigin(actual, source);
            MatchBlockContents(actual, source, functionTail: true, functionBody: true);
        }

        private void MatchBlockContents(FirBlock actual, AstBlock source, bool functionTail, bool functionBody = false)
        {
            var statements = source.Statements;
            bool finalSourceReturns = statements.LastOrDefault() is AstReturn
                || functionTail && !IsEmpty(_function.ReturnType)
                    && statements.LastOrDefault() is AstExpressionStatement { Expression: not null };
            bool appendUnitReturn = functionBody && IsEmpty(_function.ReturnType) && !finalSourceReturns;
            int expectedCount = statements.Length + (appendUnitReturn ? 1 : 0);
            if (actual.Statements.Length != expectedCount)
            { Fail("FIR statement count differs from the source block.", actual.Origin); }

            int common = Math.Min(statements.Length, actual.Statements.Length);
            for (int index = 0; index < common; index++)
            {
                bool tailValue = functionTail && index == statements.Length - 1
                    && !IsEmpty(_function.ReturnType)
                    && statements[index] is AstExpressionStatement { Expression: not null };
                MatchStatement(actual.Statements[index], statements[index], tailValue);
            }

            if (appendUnitReturn && actual.Statements.Length > statements.Length)
            {
                var last = actual.Statements[^1];
                if (last is not FirReturn { Value: null } || !ReferenceEquals(last.Origin.Element, _function.Origin.Element))
                { Fail("Implicit unit return differs from the source function tail.", last.Origin); }
            }
        }

        private void MatchStatement(FirStatement actual, AstNode source, bool tailValue = false)
        {
            if (!Enter(actual)) { return; }
            try
            {
                if (tailValue && source is AstExpressionStatement { Expression: { } tail })
                {
                    if (actual is not FirReturn { Value: { } value })
                    { Fail("Function tail expression must lower to a FIR return.", actual.Origin); return; }
                    SameOrigin(actual, source);
                    MatchExpression(value, tail);
                    return;
                }

                switch (source)
                {
                    case AstBlock block when actual is FirBlock actualBlock:
                        SameOrigin(actualBlock, block);
                        MatchBlockContents(actualBlock, block, functionTail: false);
                        break;
                    case AstExpressionStatement { Expression: { } expression } expressionStatement when actual is FirEvaluate evaluation:
                        SameOrigin(evaluation, expressionStatement);
                        MatchExpression(evaluation.Value, expression);
                        break;
                    case AstProperty property when actual is FirDeclare declaration:
                        MatchDeclaration(declaration, property);
                        break;
                    case AstAssignment assignment when actual is FirStore store:
                        MatchAssignment(store, assignment);
                        break;
                    case AstReturn ret when actual is FirReturn firReturn:
                        SameOrigin(firReturn, ret);
                        if (ret.Value is null)
                        {
                            if (firReturn.Value is not null) { Fail("FIR return adds a value absent from source.", firReturn.Origin); }
                        }
                        else if (firReturn.Value is { } returnValue) { MatchExpression(returnValue, ret.Value); }
                        else { Fail("FIR return omits its source value.", firReturn.Origin); }
                        break;
                    case AstIf conditional when actual is FirIf firIf:
                        MatchIf(firIf, conditional);
                        break;
                    case AstFor loop when loop.Form == AstForForm.Each && actual is FirArrayForEach iteration:
                        MatchArrayForEach(iteration, loop);
                        break;
                    case AstFor loop when loop.Form != AstForForm.Each && actual is FirLoop firLoop:
                        MatchLoop(firLoop, loop);
                        break;
                    case AstNode breakSource when breakSource.SyntaxKind == NodeKind.BreakStatement && actual is FirBreak:
                    case AstNode continueSource when continueSource.SyntaxKind == NodeKind.ContinueStatement && actual is FirContinue:
                        SameOrigin(actual, source);
                        break;
                    default:
                        Fail("FIR statement kind or source origin differs from its source statement.", actual.Origin);
                        break;
                }
            }
            finally { Leave(actual); }
        }

        private void MatchDeclaration(FirDeclare actual, AstProperty source)
        {
            SameOrigin(actual, source);
            string[] names = source.BindingNames.ToArray();
            if (names.Length != 1 || string.IsNullOrWhiteSpace(names[0]))
            { Fail("Verified FIR local declaration requires one source binding.", source.Origin); return; }
            var slot = FindVariableSlot(source.Origin.Element, names[0], source.Origin);
            if (slot is null) { return; }
            if (actual.Slot != slot.Id) { Fail("FIR declaration slot differs from its source local.", actual.Origin); }
            ResolvedType? expected = source.Type is not null ? program.Analysis.Sources.Types.GetType(source.Type)
                : source.Initializer is { } initializer ? GetSourceExpression(initializer)?.Type : null;
            if (expected is null || Substitute(expected) != slot.Type)
            { Fail("FIR local slot type differs from source declaration analysis.", actual.Origin); }
            if (slot.Mutable != source.IsMutable || slot.IsParameter || slot.Mode != AstPassingMode.Value)
            { Fail("FIR local slot mutability or mode differs from its source declaration.", actual.Origin); }
            if (source.Initializer is null)
            {
                if (actual.Initializer is not null) { Fail("FIR declaration adds a source-absent initializer.", actual.Origin); }
            }
            else if (actual.Initializer is { } value) { MatchExpression(value, source.Initializer); }
            else { Fail("FIR declaration omits its source initializer.", actual.Origin); }
        }

        private void MatchAssignment(FirStore actual, AstAssignment source)
        {
            SameOrigin(actual, source);
            if (source.Location is not AstAtom location || location.SyntaxKind != NodeKind.NameExpression
                || source.Value is not { } sourceValue)
            { Fail("Verified FIR assignment requires a named local and a source value.", source.Origin); return; }
            var slot = ResolveLocalSlot(location);
            if (slot is null) { return; }
            if (actual.Slot != slot.Id) { Fail("FIR store slot differs from its source assignment target.", actual.Origin); }
            if (source.Operation == "=")
            {
                MatchExpression(actual.Value, sourceValue);
                return;
            }

            if (GetSourceOperator(source.Origin.Element) is { } operatorBinding)
            {
                string selectedOperation = source.Operation.Length > 1 && source.Operation[^1] == '='
                    ? source.Operation[..^1] : "";
                if (actual.Value is not FirCall operatorCall || operatorBinding.Operator != selectedOperation)
                { Fail("FIR compound store does not call the selected source operator.", actual.Origin); return; }
                MatchOperatorCall(operatorCall, source, selectedOperation, [location, sourceValue]);
                return;
            }

            if (source.Operation.Length < 2 || source.Operation[^1] != '=' || actual.Value is not FirBinary binary
                || binary.Operation != source.Operation[..^1])
            { Fail("FIR compound store operation differs from its source assignment.", actual.Origin); return; }
            if (!Enter(binary)) { return; }
            try
            {
                if (binary.Type != slot.Type) { Fail("FIR compound store result type differs from its local.", binary.Origin); }
                if (binary.Left is FirRead read) { MatchRead(read, location, slot); }
                else { Fail("FIR compound store omits its source local read.", binary.Origin); }
                MatchExpression(binary.Right, sourceValue);
            }
            finally { Leave(binary); }
        }

        private void MatchIf(FirIf actual, AstIf source)
        {
            SameOrigin(actual, source);
            if (source.Condition is { } condition && actual.Condition is { } actualCondition)
            { MatchExpression(actualCondition, condition); }
            else if (source.Condition is not null) { Fail("FIR if omits its source condition.", actual.Origin); }
            else { Fail("Source if statement has no condition.", source.Origin); }
            if (source.Then is { } thenBlock) { MatchStatement(actual.Then, thenBlock); }
            else { Fail("Source if statement has no then block.", source.Origin); }
            if (source.Else is null)
            {
                if (actual.Else is not null) { Fail("FIR if adds an else branch absent from source.", actual.Else.Origin); }
            }
            else if (source.Else is AstBlock elseBlock)
            {
                if (actual.Else is { } actualElse) { MatchStatement(actualElse, elseBlock); }
                else { Fail("FIR if omits its source else block.", source.Else.Origin); }
            }
            else if (actual.Else is { } nestedBlock)
            {
                SameOrigin(nestedBlock, source.Else);
                if (nestedBlock.Statements is [var nested]) { MatchStatement(nested, source.Else); }
                else { Fail("Nested source else statement must occupy one FIR block entry.", nestedBlock.Origin); }
            }
            else { Fail("FIR if omits its source else statement.", source.Else.Origin); }
        }

        private void MatchLoop(FirLoop actual, AstFor source)
        {
            SameOrigin(actual, source);
            MatchOptionalStatement(actual.Initializer, source.Initializer);
            if (source.Condition is { } condition && actual.Condition is { } actualCondition)
            { MatchExpression(actualCondition, condition); }
            else if (source.Condition is not null) { Fail("FIR loop omits its source condition.", actual.Origin); }
            else if (actual.Condition is not null) { Fail("FIR loop adds a source-absent condition.", actual.Origin); }
            if (source.Body is { } body) { MatchStatement(actual.Body, body); }
            else { Fail("Source loop has no body.", source.Origin); }
            MatchOptionalStatement(actual.Update, source.Update);
        }

        private void MatchOptionalStatement(FirStatement? actual, AstNode? source)
        {
            if (source is null)
            {
                if (actual is not null) { Fail("FIR loop header adds a source-absent statement.", actual.Origin); }
            }
            else if (actual is not null) { MatchStatement(actual, source); }
            else { Fail("FIR loop header omits a source statement.", source.Origin); }
        }

        private void MatchArrayForEach(FirArrayForEach actual, AstFor source)
        {
            SameOrigin(actual, source);
            if (source.Iterable is { } iterable) { MatchExpression(actual.Array, iterable); }
            else { Fail("Source array iteration has no iterable.", source.Origin); }
            if (source.Binding is { } binding)
            {
                var names = binding.BindingNames.ToArray();
                if (names is not [var name] || string.IsNullOrWhiteSpace(name))
                { Fail("Verified array iteration requires one binding name.", binding.Origin); }
                else
                {
                    var slot = FindVariableSlot(binding.Origin.Element, name, binding.Origin);
                    if (slot is not null && actual.ItemSlot != slot.Id)
                    { Fail("FIR array item slot differs from the source loop binding.", actual.Origin); }
                }
            }
            else { Fail("Source array iteration has no item binding.", source.Origin); }
            if (source.Body is { } body) { MatchStatement(actual.Body, body); }
            else { Fail("Source array iteration has no body.", source.Origin); }
        }

        private void MatchExpression(FirExpression actual, AstExpression source)
        {
            while (source is AstSequence { IsGrouping: true } grouping)
            {
                var elements = grouping.Elements.Take(2).ToArray();
                if (elements is not [var nested]) { break; }
                source = nested;
            }
            if (!Enter(actual)) { return; }
            try
            {
                SameOrigin(actual, source);
                if (!_sourceExpressions.TryGetValue(source.Origin.Element, out var binding)
                    || binding.Status != BodyBindingStatus.Bound || binding.Type is null
                    || Substitute(binding.Type) != actual.Type)
                { Fail("FIR node type or source binding differs from this source expression.", actual.Origin); }

                switch (source)
                {
                    case AstAtom { SyntaxKind: NodeKind.LiteralExpression } literal when actual is FirConstant constant:
                        if (constant.Spelling != literal.Spelling)
                        { Fail("FIR literal spelling differs from its source token.", actual.Origin); }
                        break;
                    case AstAtom { SyntaxKind: NodeKind.NameExpression } name when actual is FirRead read:
                        var slot = ResolveLocalSlot(name);
                        if (slot is not null) { MatchRead(read, name, slot); }
                        break;
                    case AstSequence { SyntaxKind: NodeKind.ArrayExpression } array when actual is FirArrayLiteral literal:
                        var elements = array.Elements.ToArray();
                        if (literal.Elements.Length != elements.Length)
                        { Fail("FIR array literal element count differs from source.", actual.Origin); }
                        for (int index = 0; index < Math.Min(literal.Elements.Length, elements.Length); index++)
                        { MatchExpression(literal.Elements[index], elements[index]); }
                        break;
                    case AstSequence { SyntaxKind: NodeKind.TupleExpression } tuple when !tuple.Elements.Any() && actual is FirConstant empty:
                        if (empty.Spelling != "()") { Fail("FIR empty tuple differs from its source literal.", actual.Origin); }
                        break;
                    case AstOperation { Operation: "-", Operands: var operands } when operands.ToArray() is [AstAtom { SyntaxKind: NodeKind.LiteralExpression } literal]
                        && actual is FirConstant negative:
                        if (negative.Spelling != "-" + literal.Spelling)
                        { Fail("FIR negative literal differs from its source token.", actual.Origin); }
                        break;
                    case AstOperation operation when actual is FirUnary unary && operation.Operands.ToArray() is [var operand]:
                        if (unary.Operation != operation.Operation) { Fail("FIR unary operator differs from source.", actual.Origin); }
                        MatchExpression(unary.Value, operand);
                        break;
                    case AstOperation operation when actual is FirBinary binary && operation.Operands.ToArray() is [var left, var right]:
                        if (binary.Operation != operation.Operation) { Fail("FIR binary operator differs from source.", actual.Origin); }
                        MatchExpression(binary.Left, left);
                        MatchExpression(binary.Right, right);
                        break;
                    case AstOperation operation when actual is FirCall call
                        && GetSourceOperator(operation.Origin.Element) is { } operatorBinding:
                        MatchOperatorCall(call, operation, operation.Operation,
                            operation.Operands.OfType<AstExpression>().ToArray());
                        break;
                    case AstMember { Name: "length", Receiver: not null, IsSafe: false } member when actual is FirArrayLength length:
                        MatchExpression(length.Array, member.Receiver);
                        break;
                    case AstCall call:
                        MatchCallExpression(actual, call);
                        break;
                    case AstExpression indexSource when indexSource.SyntaxKind == NodeKind.IndexExpression && actual is FirArrayIndex index:
                        MatchIndex(index, indexSource);
                        break;
                    default:
                        Fail("FIR expression kind or operands differ from source syntax.", actual.Origin);
                        break;
                }
            }
            finally { Leave(actual); }
        }

        private void MatchIndex(FirArrayIndex actual, AstExpression source)
        {
            var receiver = source.Children.OfType<AstExpression>().FirstOrDefault();
            var indexes = source.Children.Where(node => node.SyntaxKind == NodeKind.ArgumentList)
                .SelectMany(node => node.Children)
                .SelectMany(node => node is AstExpression expression ? [expression] : node.Children.OfType<AstExpression>())
                .ToArray();
            if (receiver is null || indexes is not [var index])
            { Fail("Source index expression does not have one receiver and index.", source.Origin); return; }
            MatchExpression(actual.Array, receiver);
            MatchExpression(actual.Index, index);
        }

        private void MatchRead(FirRead actual, AstAtom source, FirSlot slot)
        {
            SameOrigin(actual, source);
            if (actual.Slot != slot.Id) { Fail("FIR read names a different source local slot.", actual.Origin); }
            if (actual.Type != slot.Type) { Fail("FIR read type differs from its source local slot.", actual.Origin); }
        }

        private FirSlot? ResolveLocalSlot(AstAtom source)
        {
            var locals = program.Analysis.Sources.Lexical.Lookup(source, source.Spelling).Locals;
            if (locals.Length != 1 || locals[0].Kind is not (LocalSymbolKind.Parameter or LocalSymbolKind.Variable))
            {
                Fail("FIR local read is not bound to one source parameter or local.", source.Origin);
                return null;
            }
            var local = locals[0];
            return FindVariableSlot(local.Origin.Element, local.Name, source.Origin);
        }

        private FirSlot? FindVariableSlot(object origin, string name, AstOrigin reportOrigin)
        {
            var locals = program.Analysis.Sources.Lexical.Scopes.SelectMany(scope => scope.Symbols)
                .Where(local => local.Kind is LocalSymbolKind.Parameter or LocalSymbolKind.Variable
                    && local.Name == name && ReferenceEquals(local.Origin.Element, origin)).ToArray();
            if (locals.Length != 1)
            {
                Fail("Source local identity is missing or ambiguous.", reportOrigin);
                return null;
            }
            var slots = _function.Slots.Where(slot => slot.Name == name
                && ReferenceEquals(slot.Origin.Element, locals[0].Origin.Element)).ToArray();
            if (slots.Length != 1)
            {
                Fail("FIR slot identity does not correspond to one source local.", reportOrigin);
                return null;
            }
            return slots[0];
        }

        private void MatchCallExpression(FirExpression actual, AstCall source)
        {
            _sourceCallMatches[source.Origin.Element] = _sourceCallMatches.GetValueOrDefault(source.Origin.Element) + 1;
            var binding = GetSourceCall(source);
            if (binding is null)
            { Fail("FIR call has no source-bound overload selection.", source.Origin); return; }
            var intrinsic = binding.Target is AstFunction function ? program.Catalog.Intrinsic(function) : null;
            if (intrinsic is { } intrinsicKind && actual is FirIntrinsic firIntrinsic)
            { MatchIntrinsic(firIntrinsic, source, binding, intrinsicKind); }
            else if (intrinsic is null && actual is FirCall call)
            { MatchCall(call, source, binding); }
            else { Fail("FIR call lowering kind differs from the selected source target.", actual.Origin); }
        }

        private void MatchOperatorCall(FirCall actual, AstNode site, string operation,
            IReadOnlyList<AstExpression> sourceOperands)
        {
            _sourceOperatorMatches[site.Origin.Element] = _sourceOperatorMatches.GetValueOrDefault(site.Origin.Element) + 1;
            var binding = GetSourceOperator(site.Origin.Element);
            if (binding is null)
            { Fail("FIR operator call has no source-bound overload selection.", site.Origin); return; }
            if (binding.Operator != operation || sourceOperands.Count != binding.ParameterTypes.Length
                || actual.Arguments.Length != sourceOperands.Count)
            { Fail("FIR operator operands differ from source overload resolution.", actual.Origin); }
            int common = Math.Min(actual.Arguments.Length, sourceOperands.Count);
            for (int index = 0; index < common; index++)
            {
                var argument = actual.Arguments[index];
                if (argument.Parameter != index || argument.Mode != AstPassingMode.Value
                    || index >= binding.ParameterTypes.Length
                    || Substitute(binding.ParameterTypes[index]) != argument.Value.Type)
                { Fail("FIR operator argument mapping differs from its selected signature.", actual.Origin); }
                MatchExpression(argument.Value, sourceOperands[index]);
            }
        }

        private DefaultCallPlan? ValidateCallArguments(FirCall call, BodyCallBinding sourceBinding)
        {
            var defaultPlan = ValidateDefaultCallPlan(call, sourceBinding);
            bool runtimeAdapter = program.Functions[call.Target].RuntimeFunction != FirRuntimeFunction.None;
            var firArguments = new Dictionary<int, FirArgument>();
            if (!runtimeAdapter)
            {
                var mappedParameters = sourceBinding.Arguments.Select(argument => argument.ParameterIndex).ToArray();
                bool duplicateNonVararg = mappedParameters.GroupBy(index => index).Any(group => group.Count() > 1
                    && (group.Key < 0 || group.Key >= sourceBinding.Target.Parameters.Length
                        || sourceBinding.Target.Parameters[group.Key].PassingMode != AstPassingMode.Vararg));
                bool duplicateEmpty = sourceBinding.EmptyVarargParameters.Any(mappedParameters.Contains)
                    || sourceBinding.EmptyVarargParameters.Distinct().Count() != sourceBinding.EmptyVarargParameters.Length;
                var defaultParameters = sourceBinding.DefaultParameters.IsDefault
                    ? ImmutableArray<int>.Empty : sourceBinding.DefaultParameters;
                int expectedParameterCount = mappedParameters.Concat(sourceBinding.EmptyVarargParameters)
                    .Concat(defaultParameters).Distinct().Count();
                if (duplicateNonVararg || duplicateEmpty || expectedParameterCount != call.Arguments.Length)
                { Fail("FIR call parameter set differs from source argument and empty-vararg binding.", call.Origin); }
                foreach (var argument in call.Arguments)
                {
                    if (!firArguments.TryAdd(argument.Parameter, argument))
                    { Fail("FIR call maps a parameter more than once.", call.Origin); }
                }
            }
            else if (sourceBinding.Arguments.Length != call.Arguments.Length)
            { Fail("Runtime adapter argument count differs from source binding.", call.Origin); }

            for (int index = 0; index < sourceBinding.Arguments.Length; index++)
            {
                var mapping = sourceBinding.Arguments[index];
                if (mapping.ArgumentIndex < 0 || mapping.ArgumentIndex >= sourceBinding.Call.Arguments.Length
                    || mapping.ParameterIndex < 0 || mapping.ParameterIndex >= sourceBinding.Target.Parameters.Length)
                { Fail("Source call mapping is outside its declaration or argument list.", call.Origin); continue; }
                var sourceParameter = sourceBinding.Target.Parameters[mapping.ParameterIndex];
                bool packedVararg = !runtimeAdapter && sourceParameter.PassingMode == AstPassingMode.Vararg;
                if (!runtimeAdapter && !packedVararg && (mapping.IsArrayArgument || mapping.IsSpread || mapping.IsExpandedVararg))
                { Fail("Non-vararg source parameter has vararg mapping metadata.", call.Origin); continue; }
                if (!runtimeAdapter && packedVararg && mapping.IsSpread && mapping.IsArrayArgument)
                { Fail("A vararg source mapping cannot be both spread and named-array form.", call.Origin); continue; }
                FirArgument? argument = runtimeAdapter ? call.Arguments[index]
                    : firArguments.GetValueOrDefault(mapping.ParameterIndex);
                if (argument is null || argument.Parameter != mapping.ParameterIndex)
                { Fail("FIR call omitted a source-bound argument.", call.Origin); continue; }
                var sourceArgument = sourceBinding.Call.Arguments[mapping.ArgumentIndex];
                if (sourceArgument.Value is not AstExpression)
                { Fail("Native call source argument must be an expression.", call.Origin); }
                if (packedVararg)
                {
                    var group = sourceBinding.Arguments.Where(candidate => candidate.ParameterIndex == mapping.ParameterIndex).ToArray();
                    if (group.Length == 1 && (group[0].IsArrayArgument || group[0].IsSpread))
                    {
                        if (sourceArgument.Value is AstExpression direct
                            && !ReferenceEquals(UnwrapGrouping(direct).Origin.Element, argument.Value.Origin.Element))
                        { Fail("FIR vararg array differs from the source array argument.", call.Origin); }
                    }
                    else if (group.All(candidate => candidate.IsExpandedVararg && !candidate.IsArrayArgument && !candidate.IsSpread))
                    {
                        if (argument.Value is not FirArrayLiteral packed || packed.Elements.Length != group.Length
                            || !ReferenceEquals(packed.Origin.Element, sourceBinding.Call.Origin.Element))
                        { Fail("Expanded varargs must lower to one call-origin array literal.", call.Origin); }
                    }
                    else { Fail("Native varargs require either expanded scalars or one array/spread argument.", call.Origin); }
                }
                else if (sourceArgument.Value is AstExpression sourceValue
                    && !ReferenceEquals(UnwrapGrouping(sourceValue).Origin.Element, argument.Value.Origin.Element))
                { Fail("FIR argument value differs from the source expression.", call.Origin); }
                var expectedMode = sourceArgument.Mode switch
                {
                    AstArgumentMode.Value => AstPassingMode.Value,
                    AstArgumentMode.Ref => AstPassingMode.Ref,
                    AstArgumentMode.Out or AstArgumentMode.OutVariable => AstPassingMode.Out,
                    _ => AstPassingMode.Vararg,
                };
                if (packedVararg) { expectedMode = AstPassingMode.Value; }
                if (argument.Mode != expectedMode || mapping.IsTrailingLambda != sourceArgument.IsTrailingLambda)
                { Fail("FIR argument mode differs from the source call.", call.Origin); }
            }

            if (!runtimeAdapter)
            {
                foreach (int parameterIndex in sourceBinding.EmptyVarargParameters)
                {
                    if (parameterIndex < 0 || parameterIndex >= sourceBinding.Target.Parameters.Length
                        || sourceBinding.Target.Parameters[parameterIndex].PassingMode != AstPassingMode.Vararg
                        || !firArguments.TryGetValue(parameterIndex, out var argument)
                        || argument.Value is not FirArrayLiteral { Elements.IsEmpty: true } empty
                        || !ReferenceEquals(empty.Origin.Element, sourceBinding.Call.Origin.Element)
                        || argument.Mode != AstPassingMode.Value)
                    { Fail("Empty varargs must lower to one empty call-origin array literal.", call.Origin); }
                }
            }
            return defaultPlan;
        }

        private void MatchCall(FirCall actual, AstCall source, BodyCallBinding binding)
        {
            if (binding.Target is AstFunction genericMethod && genericMethod.TypeParameters.Any()
                && GenericMethodSourcePolicy.DirectOwner(genericMethod, program.Analysis.Sources) is not null)
            {
                var caller = program.Analysis.Sources.Declarations.Files.SelectMany(file => file.Nodes)
                    .OfType<AstFunction>().SingleOrDefault(function => ReferenceEquals(function.Origin.Element, _function.Origin.Element));
                if (caller is null || !GenericMethodSourcePolicy.IsSupportedGenericMethodCall(source, caller,
                        genericMethod, binding, program.Analysis.Sources))
                { Fail("Generic method call receiver is not a direct verified singleton reference.", actual.Origin); }
            }

            bool adapter = actual.Target >= 0 && actual.Target < program.Functions.Length
                && program.Functions[actual.Target].RuntimeFunction != FirRuntimeFunction.None;
            if (adapter)
            {
                if (actual.Arguments.Length != binding.Arguments.Length)
                { Fail("Runtime adapter argument count differs from source binding.", actual.Origin); }
                for (int index = 0; index < Math.Min(actual.Arguments.Length, binding.Arguments.Length); index++)
                {
                    var mapping = binding.Arguments[index];
                    if (mapping.ArgumentIndex < 0 || mapping.ArgumentIndex >= source.Arguments.Length
                        || source.Arguments[mapping.ArgumentIndex].Value is not AstExpression sourceArgument)
                    { Fail("Runtime adapter source argument is missing.", actual.Origin); continue; }
                    MatchExpression(actual.Arguments[index].Value, sourceArgument);
                }
                return;
            }

            foreach (var group in binding.Arguments.GroupBy(mapping => mapping.ParameterIndex))
            {
                int parameterIndex = group.Key;
                var mappings = group.ToArray();
                var parameter = parameterIndex >= 0 && parameterIndex < binding.Target.Parameters.Length
                    ? binding.Target.Parameters[parameterIndex] : null;
                var matchingArguments = actual.Arguments.Where(candidate => candidate.Parameter == parameterIndex).ToArray();
                if (parameter is null || matchingArguments.Length != 1)
                { Fail("FIR omitted a source-bound parameter argument.", actual.Origin); continue; }
                var argument = matchingArguments[0];
                if (parameter.PassingMode != AstPassingMode.Vararg)
                {
                    if (mappings.Length != 1) { Fail("A non-vararg source parameter has multiple mappings.", actual.Origin); continue; }
                    var mapping = mappings[0];
                    if (mapping.ArgumentIndex < 0 || mapping.ArgumentIndex >= source.Arguments.Length
                        || source.Arguments[mapping.ArgumentIndex].Value is not AstExpression sourceArgument)
                    { Fail("Source call mapping has no expression argument.", actual.Origin); continue; }
                    MatchExpression(argument.Value, sourceArgument);
                    continue;
                }

                if (mappings.Length == 1 && (mappings[0].IsArrayArgument || mappings[0].IsSpread))
                {
                    int argumentIndex = mappings[0].ArgumentIndex;
                    if (argumentIndex < 0 || argumentIndex >= source.Arguments.Length
                        || source.Arguments[argumentIndex].Value is not AstExpression sourceArray)
                    { Fail("Source vararg array mapping has no expression.", actual.Origin); continue; }
                    MatchExpression(argument.Value, sourceArray);
                    continue;
                }

                if (mappings.Any(mapping => !mapping.IsExpandedVararg || mapping.IsArrayArgument || mapping.IsSpread)
                    || argument.Value is not FirArrayLiteral packed
                    || !ReferenceEquals(packed.Origin.Element, source.Origin.Element)
                    || packed.Elements.Length != mappings.Length)
                { Fail("Expanded vararg pack differs from its source call mappings.", actual.Origin); continue; }
                for (int index = 0; index < mappings.Length; index++)
                {
                    int argumentIndex = mappings[index].ArgumentIndex;
                    if (argumentIndex < 0 || argumentIndex >= source.Arguments.Length
                        || source.Arguments[argumentIndex].Value is not AstExpression sourceValue)
                    { Fail("Expanded vararg mapping has no source expression.", actual.Origin); continue; }
                    MatchExpression(packed.Elements[index], sourceValue);
                }
            }

            foreach (int parameterIndex in binding.EmptyVarargParameters)
            {
                var matchingArguments = actual.Arguments.Where(candidate => candidate.Parameter == parameterIndex).ToArray();
                if (matchingArguments is not [{ Mode: AstPassingMode.Value, Value: FirArrayLiteral { Elements.IsEmpty: true } packed }]
                    || !ReferenceEquals(packed.Origin.Element, source.Origin.Element))
                { Fail("Empty vararg FIR argument differs from its source call.", actual.Origin); }
            }

            if (TryGetDefaultCallPlan(actual, binding, out var defaults))
            {
                int argumentIndex = binding.Arguments.Length;
                foreach (var parameter in defaults)
                {
                    if (argumentIndex >= actual.Arguments.Length)
                    { Fail("FIR omitted a source default argument.", actual.Origin); break; }
                    MatchDefaultExpression(actual.Arguments[argumentIndex].Value, parameter.Expression,
                        actual, binding, parameter.ParameterIndex, argumentIndex);
                    argumentIndex++;
                }
            }
        }

        private void MatchIntrinsic(FirIntrinsic actual, AstCall source, BodyCallBinding binding, FirIntrinsicKind kind)
        {
            if (actual.Operation != kind) { Fail("FIR intrinsic differs from its selected source declaration.", actual.Origin); }
            var expected = new List<(AstExpression Expression, int Parameter)>();
            if (kind != FirIntrinsicKind.GcCollect && source.Target is AstMember { Receiver: { } receiver })
            { expected.Add((receiver, 0)); }
            int receiverCount = expected.Count;
            foreach (var mapping in binding.Arguments)
            {
                if (mapping.ArgumentIndex < 0 || mapping.ArgumentIndex >= source.Arguments.Length
                    || source.Arguments[mapping.ArgumentIndex].Value is not AstExpression argument)
                { Fail("Intrinsic source mapping has no expression argument.", actual.Origin); continue; }
                expected.Add((argument, mapping.ParameterIndex + receiverCount));
            }
            if (actual.Arguments.Length != expected.Count || !actual.ParameterOrder.SequenceEqual(expected.Select(item => item.Parameter)))
            { Fail("FIR intrinsic operands or parameter order differ from the source call.", actual.Origin); }
            for (int index = 0; index < Math.Min(actual.Arguments.Length, expected.Count); index++)
            { MatchExpression(actual.Arguments[index], expected[index].Expression); }
        }

        private bool IsSyntheticVarargArray(FirArrayLiteral array)
        {
            var binding = GetSourceCall(array.Origin.Element);
            if (binding is null || !ReferenceEquals(binding.Call.Origin.Element, array.Origin.Element)) { return false; }
            for (int parameterIndex = 0; parameterIndex < binding.Target.Parameters.Length; parameterIndex++)
            {
                var parameter = binding.Target.Parameters[parameterIndex];
                if (parameter.PassingMode != AstPassingMode.Vararg || parameter.Type is null
                    || parameterIndex >= binding.ParameterTypes.Length) { continue; }
                var group = binding.Arguments.Where(mapping => mapping.ParameterIndex == parameterIndex).ToArray();
                bool empty = group.Length == 0 && binding.EmptyVarargParameters.Contains(parameterIndex)
                    && array.Elements.IsEmpty;
                bool expanded = group.Length != 0 && group.All(mapping => mapping.IsExpandedVararg
                    && !mapping.IsArrayArgument && !mapping.IsSpread) && group.Length == array.Elements.Length;
                if (!empty && !expanded) { continue; }
                try
                {
                    var expected = program.Analysis.Types.GetArray(Substitute(binding.ParameterTypes[parameterIndex]));
                    if (array.Type != expected) { continue; }
                }
                catch (ArgumentException) { continue; }
                if (expanded)
                {
                    bool originsMatch = true;
                    for (int index = 0; index < group.Length; index++)
                    {
                        int argumentIndex = group[index].ArgumentIndex;
                        if (argumentIndex < 0 || argumentIndex >= binding.Call.Arguments.Length
                            || binding.Call.Arguments[argumentIndex].Value is not AstExpression sourceValue
                            || !ReferenceEquals(UnwrapGrouping(sourceValue).Origin.Element, array.Elements[index].Origin.Element))
                        { originsMatch = false; break; }
                    }
                    if (!originsMatch) { continue; }
                }
                return true;
            }
            return false;
        }

        private void SameOrigin(FirNode actual, AstNode source)
        {
            if (!ReferenceEquals(actual.Origin.Element, source.Origin.Element))
            { Fail("FIR node origin differs from its source node.", actual.Origin); }
        }
    }
}
