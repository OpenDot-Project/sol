// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using SharpFuzz;

namespace OpenDot.Sol.Fuzz;

internal static class CoverageTarget
{
    private const int MaximumInputBytes = 8192;

    internal static void Run(string target)
    {
        if (target is not ("frontend" or "semantic"))
        {
            throw new ArgumentException("Coverage target must be frontend or semantic.", nameof(target));
        }

        Fuzzer.LibFuzzer.Run(bytes =>
        {
            if (bytes.Length > MaximumInputBytes) { return; }
            if (target == "frontend")
            {
                _ = FrontendOracle.CheckBytes(bytes);
            }
            else
            {
                _ = SemanticOracle.CheckSourceBytes(bytes);
            }
        });
    }
}
