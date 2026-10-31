# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Invoke-IsolatedFuzzWorker {
    param([string]$FilePath, [string[]]$Arguments, [string]$Name, [string]$OutputDirectory,
        [int]$TimeoutSeconds = 60, [int]$MemoryLimitMB = 512, [string]$ExpectedError,
        [string]$WorkingDirectory, [byte[]]$InputBytes)
    $start = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    if ($WorkingDirectory) { $start.WorkingDirectory = [IO.Path]::GetFullPath($WorkingDirectory) }
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $null -ne $InputBytes
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        if (-not $process.Start()) { throw 'Cannot start isolated fuzz worker.' }
        $started = $true
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $inputWrite = $null
        $inputClosed = $false
        if ($null -ne $InputBytes) {
            $inputWrite = $process.StandardInput.BaseStream.WriteAsync($InputBytes, 0, $InputBytes.Length)
        }
        $failure = ''
        while ($true) {
            if ($null -ne $inputWrite -and $inputWrite.IsCompleted -and -not $inputClosed) {
                try {
                    [void]$inputWrite.GetAwaiter().GetResult()
                } catch {
                    $failure = 'stdin pipe failed'
                }
                $process.StandardInput.Close()
                $inputClosed = $true
                if ($failure) { break }
            }
            $exited = $process.WaitForExit(100)
            if ($exited -and ($null -eq $inputWrite -or $inputWrite.IsCompleted)) { break }
            $process.Refresh()
            if ($timer.Elapsed.TotalSeconds -gt $TimeoutSeconds) { $failure = 'wall-clock limit exceeded'; break }
            if (-not $process.HasExited -and $process.WorkingSet64 -gt ($MemoryLimitMB * 1MB)) {
                $failure = 'working-set limit exceeded'
                break
            }
        }
        if ($failure) {
            if (-not $process.HasExited) { $process.Kill($true) }
            if (-not $process.WaitForExit(5000)) { throw 'Timed-out fuzz worker could not be terminated.' }
        }
        if ($null -ne $inputWrite -and -not $inputWrite.IsCompleted) {
            $pipeTimer = [Diagnostics.Stopwatch]::StartNew()
            while (-not $inputWrite.IsCompleted -and $pipeTimer.Elapsed.TotalSeconds -lt 5) {
                Start-Sleep -Milliseconds 10
            }
            if (-not $inputWrite.IsCompleted) {
                throw 'Fuzz worker stdin pipe did not close.'
            }
        }
        if ($null -ne $inputWrite -and -not $inputClosed) {
            try {
                [void]$inputWrite.GetAwaiter().GetResult()
            } catch {
                if (-not $failure) { $failure = 'stdin pipe failed' }
            }
            $process.StandardInput.Close()
        }
        if (-not [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout, $stderr)).Wait(5000)) {
            throw 'Fuzz worker output pipes did not close.'
        }
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $OutputDirectory "$Name.stdout.log"), $out)
        [IO.File]::WriteAllText((Join-Path $OutputDirectory "$Name.stderr.log"), $err)
        @{ exit_code = $process.ExitCode; elapsed_ms = $timer.ElapsedMilliseconds; limit_failure = $failure } |
            ConvertTo-Json | Set-Content (Join-Path $OutputDirectory "$Name.process.json") -Encoding utf8NoBOM
        if ($ExpectedError) {
            if ($failure -or $process.ExitCode -eq 0 -or $err -notmatch $ExpectedError) {
                throw "Fuzz instrumentation self-test failed: $Name (exit $($process.ExitCode)). See $OutputDirectory"
            }
        } elseif ($failure -or $process.ExitCode -ne 0) {
            throw "Fuzz worker $Name failed ($failure, exit $($process.ExitCode)). See $OutputDirectory and its last-case journal."
        }
    } finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
}
Export-ModuleMember -Function Invoke-IsolatedFuzzWorker
