// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System.Collections.Immutable;
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Fir;

public abstract record FirNode(AstOrigin Origin);
public abstract record FirExpression(ResolvedType Type, AstOrigin Origin) : FirNode(Origin);
public sealed record FirConstant(string Spelling, ResolvedType Type, AstOrigin Origin) : FirExpression(Type, Origin);
public sealed record FirRead(int Slot, ResolvedType Type, AstOrigin Origin) : FirExpression(Type, Origin);
/// <summary>Reads an earlier value captured by the containing call's ordered argument evaluation.</summary>
public sealed record FirCallValueReference(int EvaluationIndex, ResolvedType Type, AstOrigin Origin) : FirExpression(Type, Origin);
public sealed record FirUnary(string Operation, FirExpression Value, ResolvedType Type, AstOrigin Origin) : FirExpression(Type, Origin);
public sealed record FirBinary(string Operation, FirExpression Left, FirExpression Right, ResolvedType Type, AstOrigin Origin) : FirExpression(Type, Origin);
public sealed record FirArrayLength(FirExpression Array, ResolvedType Type, AstOrigin Origin) : FirExpression(Type, Origin);
public sealed record FirArrayIndex(FirExpression Array, FirExpression Index, ResolvedType Type, AstOrigin Origin) : FirExpression(Type, Origin);
public sealed record FirArrayLiteral(ImmutableArray<FirExpression> Elements, ResolvedType Type, AstOrigin Origin) : FirExpression(Type, Origin);
public enum FirIntrinsicKind { StringSubstring, StringIndexOf, StringCompare, StringHash, GcCollect }
public sealed record FirIntrinsic(FirIntrinsicKind Operation, ImmutableArray<FirExpression> Arguments, ResolvedType Type, AstOrigin Origin) : FirExpression(Type, Origin)
{
    public ImmutableArray<int> ParameterOrder { get; init; } = [];
}
public sealed record FirArgument(int Parameter, AstPassingMode Mode, FirExpression Value);
public sealed record FirCall(int Target, ImmutableArray<FirArgument> Arguments, ResolvedType Type, AstOrigin Origin) : FirExpression(Type, Origin)
{
    /// <summary>Closed, declaration-ordered type arguments for a specialized generic target.</summary>
    public ImmutableArray<ResolvedType> TypeArguments { get; init; } = [];
}

public abstract record FirStatement(AstOrigin Origin) : FirNode(Origin);
public sealed record FirBlock(ImmutableArray<FirStatement> Statements, AstOrigin Origin) : FirStatement(Origin);
public sealed record FirDeclare(int Slot, FirExpression? Initializer, AstOrigin Origin) : FirStatement(Origin);
public sealed record FirStore(int Slot, FirExpression Value, AstOrigin Origin) : FirStatement(Origin);
public sealed record FirEvaluate(FirExpression Value, AstOrigin Origin) : FirStatement(Origin);
public sealed record FirReturn(FirExpression? Value, AstOrigin Origin) : FirStatement(Origin);
public sealed record FirIf(FirExpression Condition, FirBlock Then, FirBlock? Else, AstOrigin Origin) : FirStatement(Origin);
public sealed record FirLoop(FirStatement? Initializer, FirExpression? Condition, FirBlock Body, FirStatement? Update, AstOrigin Origin) : FirStatement(Origin);
public sealed record FirArrayForEach(int ItemSlot, FirExpression Array, FirBlock Body, AstOrigin Origin) : FirStatement(Origin);
public sealed record FirBreak(AstOrigin Origin) : FirStatement(Origin);
public sealed record FirContinue(AstOrigin Origin) : FirStatement(Origin);

public sealed record FirSlot(int Id, string Name, ResolvedType Type, bool Mutable, AstPassingMode Mode, bool IsParameter, AstOrigin Origin);
public enum FirRuntimeFunction { None, Print, Println, Error, Errorln }
public sealed record FirFunction(int Id, string Name, ImmutableArray<FirSlot> Parameters, ImmutableArray<FirSlot> Slots,
    ResolvedType ReturnType, FirBlock? Body, FirRuntimeFunction RuntimeFunction, ImmutableArray<ResolvedType> Effects, AstOrigin Origin)
{
    public string? ExportName { get; init; }
    /// <summary>Closed generic arguments for this monomorphic body; empty for ordinary functions.</summary>
    public ImmutableArray<ResolvedType> SpecializationArguments { get; init; } = [];
}

/// <summary>Unverified source FIR; verification is required before code generation.</summary>
public sealed record FirProgram(BodyAnalysis Analysis, RuntimeCatalog Catalog, ImmutableArray<FirFunction> Functions, int Entry)
{
    public bool IsLibrary { get; init; }
}

/// <summary>A capability minted only by the FIR verifier. Consumers cannot construct one directly.</summary>
public sealed class VerifiedFirProgram
{
    internal VerifiedFirProgram(FirProgram program, ImmutableArray<FirFlowGraph> graphs)
    { Program = program; FlowGraphs = graphs; }
    public FirProgram Program { get; }
    public ImmutableArray<FirFlowGraph> FlowGraphs { get; }
}
