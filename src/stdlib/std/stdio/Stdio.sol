// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Implemented for scalar/String arguments by the qualified native SolRT print adapters.
module std.stdio

class IOException: Exception {}
func print(vararg values: Object) throws IOException
func println(vararg values: Object) throws IOException
func error(vararg values: Object) throws IOException
func errorln(vararg values: Object) throws IOException
