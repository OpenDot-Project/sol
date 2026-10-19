// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace OpenDot.Sol.Cli;

internal static class OutputTransaction
{
    public static void Write(IReadOnlyList<(string Path, byte[] Bytes)> outputs, CancellationToken cancellation)
    {
        var staged = new List<(string Destination, string Temporary, string Backup, bool Existed)>();
        int committed = 0;
        try
        {
            foreach (var (path, bytes) in outputs)
            {
                cancellation.ThrowIfCancellationRequested();
                string directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                string temporary = Path.Combine(directory, $".solc-{Guid.NewGuid():N}.tmp");
                string backup = temporary + ".bak";
                staged.Add((path, temporary, backup, File.Exists(path)));
                using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            foreach (var entry in staged)
            {
                cancellation.ThrowIfCancellationRequested();
                if (entry.Existed) { File.Replace(entry.Temporary, entry.Destination, entry.Backup); }
                else { File.Move(entry.Temporary, entry.Destination); }
                committed++;
            }
        }
        catch
        {
            for (int index = committed - 1; index >= 0; index--)
            {
                var entry = staged[index];
                if (entry.Existed) { File.Move(entry.Backup, entry.Destination, overwrite: true); }
                else { File.Delete(entry.Destination); }
            }
            throw;
        }
        finally
        {
            foreach (var entry in staged)
            {
                File.Delete(entry.Temporary);
                // Preserve a rollback backup if restoration itself failed.
                if (committed == staged.Count) { File.Delete(entry.Backup); }
            }
        }
    }
}
