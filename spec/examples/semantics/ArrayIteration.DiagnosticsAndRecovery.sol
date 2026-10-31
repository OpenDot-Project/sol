// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Normative matrix: incomplete resolution never produces verified executable loop state.
module spec.arrayiteration.recovery

func sourceBindingFailureIsLocal(values: UnknownIterable): () {
    for val item in values { consume(item) }
}

func elementResolutionFailureKeepsOtherFunctions(values: []MissingType): () {
    for val item in values { consume(item) }
}

func ambiguousSourceDoesNotPickFirstCandidate(values: AmbiguousIterable): () {
    for val item in values { consume(item) }
}

func unresolvedCursorEffectDefersTheBody(values: UnresolvedEffects): () {
    for val item in values { consume(item) }
}

func unresolvedCursorCloseDoesNotPretendNoCleanup(values: UnresolvedClose): () {
    for val item in values { consume(item) }
}

func unverifiedSourceDoesNotBecomeArray(values: UnknownIterable): () {
    for val item in values { consume(item) }
}

func unsupportedFIRNodeReportsSourceOrigin(values: UserIterable<int>): () {
    // Current native gate reports the for-in source location rather than silently dropping it.
    for val item in values { consume(item) }
}

func failedElementQualificationDoesNotPublishHalfNode(values: []half): () {
    for val item in values { consume(item) }
}

func failedSlotIdentityDoesNotMintVerifiedFir(values: []int): () {
    for val item in values { consume(item) }
}

func failedFlowGraphDoesNotMintCapability(values: []int): () {
    for val item in values { consume(item) }
}

func deterministicDiagnosticOrder(values: []int): () {
    for val item in values {
        unresolvedFirst
        unresolvedSecond
    }
}

func sourceSpanPointsToBadBodyNode(values: []int): () {
    for val item in values { unresolvedBodyName }
}

func sourceSpanPointsToIterable(values: UnknownIterable): () {
    for val item in values { consume(item) }
}

func cancellationDuringSourceEvaluationDoesNotCommitPartialAnalysis(values: []int): () {
    for val item in values { consume(item) }
}

func cancellationDuringBodyDoesNotCommitPartialFir(values: []int): () {
    for val item in values { consume(item) }
}

func resourceLimitOnNestedLoopsIsExplicit(outer: [][]int): () {
    for val row in outer {
        for val item in row { analyzeDeeply(item) }
    }
}

func diagnosticBudgetDoesNotChangeAcceptedSemantics(values: []int): () {
    for val item in values { consume(item) }
}

func parserRecoveryDoesNotInventMissingCursorMethods(values: IncompleteCursor): () {
    for val item in values { consume(item) }
}

func compilerRecoveryPreservesFollowingDeclaration(values: UnknownIterable): int {
    for val item in values { consume(item) }
    validFollowingDeclaration()
}

func duplicateSourceOriginsCannotBindOneFirNode(values: []int): () {
    for val item in values { consume(item) }
    for val item in values { consume(item) }
}

func unsupportedLoopAndSupportedSiblingAreIndependent(values: []int, source: UserIterable<int>): int {
    for val item in source { consume(item) }
    var total = 0
    for val item in values { total += item }
    total
}

func failedCursorDoesNotChangeArrayAnalysis(values: []int, source: UserIterable<int>): int {
    for val item in source { consume(item) }
    var total = 0
    for val item in values { total += item }
    total
}

func completeArrayNodeRemainsBounded(values: []int): int {
    var total = 0
    for val item in values { total += item }
    total
}

// Error recovery preserves useful analysis of unrelated declarations, but never upgrades a partial
// protocol, unsupported layout, cancelled transfer, or malformed FIR graph into success.
