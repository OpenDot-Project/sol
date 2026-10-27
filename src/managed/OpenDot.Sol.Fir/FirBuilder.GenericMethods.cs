// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using OpenDot.Sol.Analysis;
using OpenDot.Sol.Ast;

namespace OpenDot.Sol.Fir;

internal sealed partial class FirBuilder
{
    private bool IsSupportedGenericMethodCall(AstCall call, AstFunction target, BodyCallBinding binding) =>
        _current is AstFunction caller
        && GenericMethodSourcePolicy.IsSupportedGenericMethodCall(call, caller, target, binding, _analysis.Sources);
}
