// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
namespace OpenDot.Sol.Analysis;

/// <summary>
/// Source-level storage candidate for a field required by a lambda or anonymous-function
/// environment. This is not an emitted layout, root map, or allocation decision.
/// </summary>
public enum CallableEnvironmentFieldStorage
{
    /// <summary>The lexical receiver identity is required; representation depends on its type.</summary>
    ReceiverCapture,
    /// <summary>An immutable local's value may be copied into the environment.</summary>
    ValueCopy,
    /// <summary>A mutable local must preserve one shared lexical cell.</summary>
    SharedMutableCell,
    /// <summary>A ref/out local remains borrowed and cannot escape unchecked.</summary>
    BorrowedLocation,
    /// <summary>A captured local callable requires its own callable-environment representation.</summary>
    NestedCallableEnvironment,
}
