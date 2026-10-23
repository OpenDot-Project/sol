# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
[CmdletBinding()]
param(
    [string[]]$ProfilePath,
    [switch]$SkipBuild,
    [switch]$AllowSanitized
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'RuntimeProfile.psm1') -Force
if (-not $ProfilePath) {
    $arguments = @{ Nodes = 512; Cycles = 2; Roots = 17; Threads = 3; Events = $true; SkipBuild = $SkipBuild }
    $ProfilePath = @(& (Join-Path $PSScriptRoot 'measure-runtime.ps1') @arguments)
}
if ($ProfilePath.Count -eq 0) { throw 'No runtime profiles were supplied or generated.' }
$profiles = @($ProfilePath | ForEach-Object {
    $value = Get-Content -LiteralPath $_ -Raw | ConvertFrom-Json -AsHashtable -Depth 64
    Test-RuntimeProfile $value -AllowSanitized:$AllowSanitized
    $value
})
$baseline = $profiles[0]
$cases = [ordered]@{
    'missing schema' = { param($p) $p.Remove('schema_version') | Out-Null }
    'new schema' = { param($p) $p.schema_version = 2 }
    'string schema' = { param($p) $p.schema_version = '1' }
    'Boolean schema' = { param($p) $p.schema_version = $true }
    'array schema' = { param($p) $p.schema_version = @(1) }
    'unexpected top-level field' = { param($p) $p.extra = 0 }
    'wrong kind' = { param($p) $p.kind = 'other' }
    'array kind' = { param($p) $p.kind = @('yellow-tea-native-profile') }
    'negative duration' = { param($p) $p.elapsed_ns = -1 }
    'floating duration' = { param($p) $p.elapsed_ns = 1.5 }
    'string duration' = { param($p) $p.elapsed_ns = '1' }
    'Boolean duration' = { param($p) $p.elapsed_ns = $true }
    'null duration' = { param($p) $p.elapsed_ns = $null }
    'unsupported scenario' = { param($p) $p.settings.scenario = 'automatic' }
    'wrong scenario case' = { param($p) $p.settings.scenario = 'Dense' }
    'too few nodes' = { param($p) $p.settings.nodes = 15 }
    'too many nodes' = { param($p) $p.settings.nodes = 200001 }
    'zero cycles' = { param($p) $p.settings.cycles = 0 }
    'too many cycles' = { param($p) $p.settings.cycles = 11 }
    'no root slots' = { param($p) $p.settings.roots = 0 }
    'too many root slots' = { param($p) $p.settings.roots = 4097 }
    'negative seed' = { param($p) $p.settings.seed = -1 }
    'seed overflow' = { param($p) $p.settings.seed = [long]4294967296 }
    'insufficient heap limit' = { param($p) $p.settings.heap_limit_bytes = 1 }
    'unknown settings key' = { param($p) $p.settings.unused = 1 }
    'unsupported environment' = { param($p) $p.environment.platform = 'other' }
    'unknown clock' = { param($p) $p.environment.clock = 'wall_clock' }
    'array clock' = { param($p) $p.environment.clock = @('steady_clock') }
    'missing processor count' = { param($p) $p.environment.logical_processors = 0 }
    'sanitized timing' = { param($p) $p.environment.address_sanitized = $true }
    'string sanitizer flag' = { param($p) $p.environment.address_sanitized = 'false' }
    'unknown latency method' = { param($p) $p.allocation_latency.method = 'subset' }
    'array latency method' = { param($p) $p.allocation_latency.method = @('all_calls_nearest_rank') }
    'missing allocation sample' = { param($p) $p.allocation_latency.samples-- }
    'percentile order' = { param($p) $p.allocation_latency.p50_ns = $p.allocation_latency.max_ns + 1 }
    'total below maximum' = { param($p) $p.allocation_latency.total_ns = $p.allocation_latency.max_ns - 1 }
    'new diagnostic version' = { param($p) $p.diagnostics.version = 2 }
    'string diagnostic version' = { param($p) $p.diagnostics.version = '1' }
    'string phase' = { param($p) $p.diagnostics.phase = '0' }
    'array phase' = { param($p) $p.diagnostics.phase = @(0) }
    'unfinished phase' = { param($p) $p.diagnostics.phase = 2 }
    'missing epoch' = { param($p) $p.diagnostics.epoch = 0 }
    'unknown counter' = { param($p) $p.diagnostics.counters.other = 1 }
    'missing counter' = { param($p) $p.diagnostics.counters.Remove('shade_calls') | Out-Null }
    'negative counter' = { param($p) $p.diagnostics.counters.shade_calls = -1 }
    'incomplete reclaim' = { param($p) $p.diagnostics.counters.reclaimed_objects-- }
    'byte mismatch' = { param($p) $p.diagnostics.counters.reclaimed_bytes-- }
    'uncompleted marks' = { param($p) $p.diagnostics.counters.new_marks++ }
    'impossible shade count' = { param($p) $p.diagnostics.counters.shade_calls = 0 }
    'reference work overflow' = { param($p) $p.diagnostics.counters.mark_max_reference_slots = 65 }
    'object work overflow' = { param($p) $p.diagnostics.counters.mark_max_objects = 33 }
    'missing histogram' = { param($p) $p.diagnostics.histograms.Remove('mark_batch') | Out-Null }
    'wrong bound' = { param($p) $p.diagnostics.histogram_upper_bounds_ns[0] = 1001 }
    'floating bound' = { param($p) $p.diagnostics.histogram_upper_bounds_ns[0] = 1000.5 }
    'missing bound' = { param($p) $p.diagnostics.histogram_upper_bounds_ns = @(1000) }
    'object instead of array' = { param($p) $p.diagnostics.histograms.mark_batch.buckets = @{} }
    'missing bucket' = { param($p) $p.diagnostics.histograms.mark_batch.buckets = @(0) }
    'negative bucket' = { param($p) $p.diagnostics.histograms.mark_batch.buckets[0] = -1 }
    'missing observation' = { param($p) $p.diagnostics.histograms.mark_batch.samples++ }
    'false percentile' = { param($p) $p.diagnostics.histograms.root_snapshot.p99_upper_ns++ }
    'unknown histogram field' = { param($p) $p.diagnostics.histograms.root_snapshot.p90_ns = 0 }
    'unclaimed region' = { param($p) $p.diagnostics.ready_queue.regions = 1 }
    'queue count mismatch' = { param($p) $p.diagnostics.ready_queue.claims++ }
    'impossible queue peak' = { param($p) $p.diagnostics.ready_queue.peak_regions = $p.diagnostics.ready_queue.publications + 1 }
    'unaccounted commitment' = { param($p) $p.allocator.committed_bytes += 65536 }
    'too many retained spans' = { param($p) $p.allocator.span_count = 45 }
    'remaining large object' = { param($p) $p.allocator.large_object_count = 1 }
    'negative reuse count' = { param($p) $p.allocator.reused_slots = -1 }
    'remaining cache slot' = { param($p) $p.cache.cached_slots = 1 }
    'remaining object' = { param($p) $p.heap.object_count = 1 }
    'remaining registered bytes' = { param($p) $p.heap.registered_bytes = 16 }
    'different completed epoch' = { param($p) $p.heap.completed_epoch++ }
    'missing checkpoints' = { param($p) $p.checkpoints = @() }
    'reachability mismatch' = { param($p) $p.checkpoints[0].actual_objects++ }
    'fabricated matching counts' = { param($p) $p.checkpoints[0].actual_objects++; $p.checkpoints[0].expected_objects++ }
    'wrong workload cycle' = { param($p) $p.checkpoints[0].cycle++ }
    'future checkpoint' = { param($p) $p.checkpoints[0].epoch = $p.diagnostics.epoch + 1 }
    'checkpoint physical overflow' = { param($p) $p.checkpoints[0].committed_bytes = $p.settings.heap_limit_bytes + 1 }
    'checkpoint live overflow' = { param($p) $p.checkpoints[0].live_bytes = $p.checkpoints[0].committed_bytes + 1 }
    'wrong ring capacity' = { param($p) $p.event_ring.capacity = 4097 }
    'string ring flag' = { param($p) $p.event_ring.enabled = 'true' }
    'false loss count' = { param($p) $p.event_ring.dropped_events++ }
    'incomplete event window' = { param($p) $p.event_ring.next_sequence++ }
}
$encoded = $baseline | ConvertTo-Json -Depth 64 -Compress
$rejected = 0
foreach ($case in $cases.GetEnumerator()) {
    if ($AllowSanitized -and $case.Key -eq 'sanitized timing') { continue }
    $corrupted = $encoded | ConvertFrom-Json -AsHashtable -Depth 64
    & $case.Value $corrupted
    $failure = $null
    try { Test-RuntimeProfile $corrupted -AllowSanitized:$AllowSanitized } catch { $failure = $_ }
    if ($null -eq $failure) { throw "Runtime profile validator accepted corrupted evidence: $($case.Key)." }
    $rejected++
}
$withEvents = $profiles | Where-Object { $_.event_ring.events.Count -gt 1 } | Select-Object -First 1
if ($withEvents) {
    $eventCases = [ordered]@{
        'duplicate sequence' = { param($p) $p.event_ring.events[1].sequence = $p.event_ring.events[0].sequence }
        'backward timestamp' = { param($p) $p.event_ring.events[1].timestamp_ns = 0 }
        'future event' = { param($p) $p.event_ring.events[0].epoch = $p.diagnostics.epoch + 1 }
        'unknown event' = { param($p) $p.event_ring.events[0].kind = 'other' }
        'negative event work' = { param($p) $p.event_ring.events[0].work = -1 }
        'disabled nonempty ring' = { param($p) $p.event_ring.enabled = $false }
        'missing event field' = { param($p) $p.event_ring.events[0].Remove('work') | Out-Null }
    }
    $encodedEvents = $withEvents | ConvertTo-Json -Depth 64 -Compress
    foreach ($case in $eventCases.GetEnumerator()) {
        $corrupted = $encodedEvents | ConvertFrom-Json -AsHashtable -Depth 64
        & $case.Value $corrupted
        $failure = $null
        try { Test-RuntimeProfile $corrupted -AllowSanitized:$AllowSanitized } catch { $failure = $_ }
        if ($null -eq $failure) { throw "Runtime profile validator accepted corrupted event evidence: $($case.Key)." }
        $rejected++
    }
}
Write-Host "Runtime profiles validated: $($profiles.Count) real workloads; $rejected corrupted reports rejected."
