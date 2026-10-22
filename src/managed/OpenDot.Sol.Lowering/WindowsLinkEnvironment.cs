// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.IO;
using System.Linq;

namespace OpenDot.Sol.Lowering;

/// <summary>Locates installed Windows libraries without distributing build-machine paths or SDK files.</summary>
internal static class WindowsLinkEnvironment
{
    internal static string[] Resolve(string backendDirectory, string? libraryEnvironment)
    {
        string configuration = Path.Combine(backendDirectory, "link-paths.txt");
        string[] candidates = File.Exists(configuration) ? File.ReadAllLines(configuration)
            : (libraryEnvironment ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        string[] paths = candidates.Select(path => path.Trim().Trim('"')).Where(path => path.Length != 0)
            .Select(path => Path.IsPathFullyQualified(path) ? Path.GetFullPath(path)
                : throw new IOException("Windows library paths must be absolute."))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] required = ["libcmt.lib", "libvcruntime.lib", "libucrt.lib", "oldnames.lib", "kernel32.lib"];
        string[] missing = required.Where(name => !paths.Any(path => File.Exists(Path.Combine(path, name)))).ToArray();
        if (missing.Length != 0)
        {
            throw new IOException("Windows x64 C++ build libraries are unavailable: " + string.Join(", ", missing)
                + ". Run Solc from an x64 Visual Studio developer shell with the Windows SDK installed."
                + " ORC execution and library archiving do not require these libraries.");
        }
        return paths;
    }
}
