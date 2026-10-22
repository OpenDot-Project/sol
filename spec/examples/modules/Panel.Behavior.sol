// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Status: two-file partial source-set fixture; member execution is planned.
// Source set: Panel.Storage.sol + Panel.Behavior.sol.
module demonstrations.logical.panel

partial class Panel {
    func description(): String = this.title
}

// Same module/name/arity/distribution merges the parts. This part has no initialization.
