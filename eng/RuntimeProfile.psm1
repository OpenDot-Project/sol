# SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
# SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ProfileFields {
    param($Value, [string[]]$Names, [string]$Context)
    if ($Value -isnot [System.Collections.IDictionary]) { throw "$Context must be an object." }
    $actual = @($Value.Keys | Sort-Object)
    $expected = @($Names | Sort-Object)
    if ($actual.Count -ne $expected.Count) { throw "$Context has missing or unexpected fields." }
    for ($index = 0; $index -lt $actual.Count; $index++) {
        if ($actual[$index] -cne $expected[$index]) { throw "$Context has missing or unexpected fields." }
    }
}

function Assert-ProfileInteger {
    param($Value, [string]$Context, [decimal]$Minimum = 0, [decimal]$Maximum = 18446744073709551615d)
    $integers = @([byte], [sbyte], [short], [ushort], [int], [uint], [long], [ulong], [System.Numerics.BigInteger])
    if ($null -eq $Value -or $Value.GetType() -notin $integers) { throw "$Context must be an integer." }
    $number = [decimal]$Value
    if ($number -lt $Minimum -or $number -gt $Maximum) { throw "$Context is out of range." }
}

function Assert-ProfileIntegers {
    param([System.Collections.IDictionary]$Value, [string]$Context)
    foreach ($key in $Value.Keys) { Assert-ProfileInteger $Value[$key] "$Context.$key" }
}

function Assert-ProfileArray {
    param($Value, [string]$Context)
    if ($Value -isnot [System.Collections.IList]) { throw "$Context must be an array." }
}

function Assert-ProfileEqual {
    param($Actual, $Expected, [string]$Context)
    if ($Expected -is [string] -and $Actual -isnot [string]) { throw "$Context must be a string." }
    if ($Actual -cne $Expected) { throw "$Context is inconsistent." }
}

function Get-RuntimeCounterNames {
    return @(
        'root_slots', 'root_frames', 'strong_roots', 'cache_slots_returned',
        'mark_objects', 'mark_reference_slots', 'mark_batches',
        'assist_batches', 'assist_reference_slots', 'assist_objects',
        'shade_calls', 'new_marks', 'already_marked',
        'reclaimed_objects', 'reclaimed_bytes', 'sweep_objects', 'sweep_batches',
        'collections', 'termination_retries', 'allocation_bytes', 'allocation_calls',
        'pressure_waits', 'refill_failures', 'phase_changes',
        'mark_max_reference_slots', 'mark_max_objects', 'assist_debt_paid',
        'active_barriers', 'idle_barriers'
    )
}

function Get-RuntimeHistogramNames {
    return @('safepoint_wait', 'root_snapshot', 'mark_batch', 'assist_batch', 'sweep_batch')
}

function Get-RuntimeEventNames {
    return @(
        'start_requested', 'roots_scanned', 'mark_started', 'mark_batch', 'assist_batch',
        'termination_requested', 'sweep_started', 'sweep_batch', 'cycle_completed',
        'pressure_wait', 'refill_failure'
    )
}

function Get-HistogramRankBound {
    param([System.Collections.IDictionary]$Histogram, [object[]]$Bounds, [int]$Percentile)
    if ($Histogram.samples -eq 0) { return [ulong]0 }
    $rank = [decimal]::Ceiling(([decimal]$Histogram.samples * $Percentile) / 100)
    $cumulative = [decimal]0
    for ($index = 0; $index -lt $Bounds.Count; $index++) {
        $cumulative += [decimal]$Histogram.buckets[$index]
        if ($cumulative -ge $rank) { return $Bounds[$index] }
    }
    throw 'Histogram rank exceeds available observations.'
}

function Test-RuntimeHistogram {
    param($Histogram, [object[]]$Bounds, [string]$Context)
    Assert-ProfileFields $Histogram @('samples', 'total_ns', 'max_ns', 'p50_upper_ns', 'p95_upper_ns', 'p99_upper_ns', 'buckets') $Context
    foreach ($key in @('samples', 'total_ns', 'max_ns', 'p50_upper_ns', 'p95_upper_ns', 'p99_upper_ns')) {
        Assert-ProfileInteger $Histogram[$key] "$Context.$key"
    }
    Assert-ProfileArray $Histogram.buckets "$Context.buckets"
    if ($Histogram.buckets.Count -ne $Bounds.Count) { throw "$Context bucket count is inconsistent." }
    $sum = [decimal]0
    foreach ($bucket in $Histogram.buckets) {
        Assert-ProfileInteger $bucket "$Context.buckets[]"
        $sum += [decimal]$bucket
    }
    Assert-ProfileEqual $sum ([decimal]$Histogram.samples) "$Context.samples"
    if ($Histogram.samples -eq 0) {
        foreach ($key in @('total_ns', 'max_ns', 'p50_upper_ns', 'p95_upper_ns', 'p99_upper_ns')) {
            Assert-ProfileEqual $Histogram[$key] 0 "$Context.$key"
        }
    } elseif ($Histogram.total_ns -lt $Histogram.max_ns) {
        throw "$Context total duration is smaller than its maximum."
    }
    foreach ($percentile in @(50, 95, 99)) {
        $expected = Get-HistogramRankBound $Histogram $Bounds $percentile
        Assert-ProfileEqual $Histogram["p${percentile}_upper_ns"] $expected "$Context.p${percentile}_upper_ns"
    }
    # The last bucket is open-ended in practice. A finite maximum must fit in a nonempty bucket.
    if ($Histogram.samples -gt 0) {
        $maximumBucket = 0
        while ($Bounds[$maximumBucket] -lt $Histogram.max_ns) { $maximumBucket++ }
        if ($Histogram.buckets[$maximumBucket] -eq 0) { throw "$Context maximum has no matching bucket." }
        for ($index = $maximumBucket + 1; $index -lt $Bounds.Count; $index++) {
            if ($Histogram.buckets[$index] -ne 0) { throw "$Context has observations above its maximum." }
        }
    }
}

function Test-RuntimeEventRing {
    param($Ring, [decimal]$Epoch)
    Assert-ProfileFields $Ring @('enabled', 'capacity', 'first_sequence', 'next_sequence', 'dropped_events', 'events') 'event_ring'
    if ($Ring.enabled -isnot [bool]) { throw 'event_ring.enabled must be a Boolean.' }
    foreach ($key in @('capacity', 'first_sequence', 'next_sequence', 'dropped_events')) {
        Assert-ProfileInteger $Ring[$key] "event_ring.$key"
    }
    Assert-ProfileEqual $Ring.capacity 4096 'event_ring.capacity'
    Assert-ProfileArray $Ring.events 'event_ring.events'
    if ($Ring.events.Count -gt 4096) { throw 'Event ring exceeds its resident capacity.' }
    if (-not $Ring.enabled) {
        Assert-ProfileEqual $Ring.first_sequence 1 'disabled event first sequence'
        Assert-ProfileEqual $Ring.next_sequence 1 'disabled event next sequence'
        Assert-ProfileEqual $Ring.dropped_events 0 'disabled event losses'
        Assert-ProfileEqual $Ring.events.Count 0 'disabled event records'
        return
    }
    if ($Ring.first_sequence -lt 1 -or $Ring.next_sequence -lt $Ring.first_sequence) {
        throw 'Event sequence window is invalid.'
    }
    Assert-ProfileEqual ([decimal]$Ring.next_sequence - [decimal]$Ring.first_sequence) ([decimal]$Ring.events.Count) 'event window length'
    Assert-ProfileEqual ([decimal]$Ring.first_sequence - 1) ([decimal]$Ring.dropped_events) 'event overwrite accounting'
    $expected = [decimal]$Ring.first_sequence
    $timestamp = [decimal]0
    $previousEpoch = [decimal]0
    $names = Get-RuntimeEventNames
    foreach ($event in $Ring.events) {
        Assert-ProfileFields $event @('sequence', 'timestamp_ns', 'epoch', 'kind', 'work', 'duration_ns') 'event'
        foreach ($key in @('sequence', 'timestamp_ns', 'epoch', 'work', 'duration_ns')) {
            Assert-ProfileInteger $event[$key] "event.$key"
        }
        Assert-ProfileEqual ([decimal]$event.sequence) $expected 'event sequence'
        if ($event.kind -isnot [string] -or $event.kind -cnotin $names) { throw 'Unknown event kind.' }
        if ($event.timestamp_ns -lt $timestamp -or $event.epoch -lt $previousEpoch -or $event.epoch -gt $Epoch) {
            throw 'Event timestamps or epochs are out of order.'
        }
        $timestamp = [decimal]$event.timestamp_ns
        $previousEpoch = [decimal]$event.epoch
        $expected++
    }
}

function Test-RuntimeProfile {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Profile, [switch]$AllowSanitized)
    Assert-ProfileFields $Profile @('schema_version', 'kind', 'settings', 'environment', 'elapsed_ns', 'allocation_latency', 'diagnostics', 'allocator', 'cache', 'heap', 'checkpoints', 'event_ring') 'profile'
    Assert-ProfileInteger $Profile.schema_version 'schema_version'
    Assert-ProfileEqual $Profile.schema_version 1 'schema_version'
    Assert-ProfileEqual $Profile.kind 'yellow-tea-native-profile' 'profile kind'
    Assert-ProfileInteger $Profile.elapsed_ns 'elapsed_ns' 1
    $settings = $Profile.settings
    Assert-ProfileFields $settings @('scenario', 'nodes', 'cycles', 'roots', 'threads', 'seed', 'heap_limit_bytes') 'settings'
    if ($settings.scenario -isnot [string] -or $settings.scenario -cnotin @('dense', 'sparse', 'wide', 'churn', 'roots', 'concurrent')) {
        throw 'Unknown workload scenario.'
    }
    Assert-ProfileInteger $settings.nodes 'settings.nodes' 16 200000
    Assert-ProfileInteger $settings.cycles 'settings.cycles' 1 10
    Assert-ProfileInteger $settings.roots 'settings.roots' 1 4096
    Assert-ProfileInteger $settings.threads 'settings.threads' 1 8
    Assert-ProfileInteger $settings.seed 'settings.seed' 0 4294967295d
    Assert-ProfileInteger $settings.heap_limit_bytes 'settings.heap_limit_bytes' 16777216
    if (($settings.scenario -eq 'concurrent' -and $settings.threads -lt 2) -or
        ($settings.scenario -ne 'concurrent' -and $settings.threads -ne 1)) { throw 'Workload thread count is invalid.' }
    $operations = [decimal]$settings.nodes * $settings.cycles * $settings.threads
    if ($operations -gt 2000000) { throw 'Profile exceeds its allocation sample budget.' }
    if ($settings.scenario -eq 'wide') { $operations += $settings.cycles }
    Assert-ProfileFields $Profile.environment @('platform', 'msvc_version', 'logical_processors', 'address_sanitized', 'clock') 'environment'
    Assert-ProfileEqual $Profile.environment.platform 'windows-x64' 'environment.platform'
    Assert-ProfileEqual $Profile.environment.clock 'steady_clock' 'environment.clock'
    Assert-ProfileInteger $Profile.environment.msvc_version 'environment.msvc_version' 1900 9999
    Assert-ProfileInteger $Profile.environment.logical_processors 'environment.logical_processors' 1 65536
    if ($Profile.environment.address_sanitized -isnot [bool]) { throw 'address_sanitized must be a Boolean.' }
    if ($Profile.environment.address_sanitized -and -not $AllowSanitized) { throw 'Sanitized runs are correctness evidence, not timing profiles.' }
    $latency = $Profile.allocation_latency
    Assert-ProfileFields $latency @('method', 'samples', 'total_ns', 'max_ns', 'p50_ns', 'p95_ns', 'p99_ns') 'allocation_latency'
    Assert-ProfileEqual $latency.method 'all_calls_nearest_rank' 'allocation sampling method'
    foreach ($key in @('samples', 'total_ns', 'max_ns', 'p50_ns', 'p95_ns', 'p99_ns')) {
        Assert-ProfileInteger $latency[$key] "allocation_latency.$key"
    }
    Assert-ProfileEqual ([decimal]$latency.samples) $operations 'allocation sample count'
    if ($latency.p50_ns -gt $latency.p95_ns -or $latency.p95_ns -gt $latency.p99_ns -or
        $latency.p99_ns -gt $latency.max_ns -or $latency.max_ns -gt $latency.total_ns) {
        throw 'Allocation latency order is invalid.'
    }
    $diagnostics = $Profile.diagnostics
    Assert-ProfileFields $diagnostics @('version', 'epoch', 'phase', 'counters', 'histogram_upper_bounds_ns', 'histograms', 'ready_queue') 'diagnostics'
    Assert-ProfileInteger $diagnostics.version 'diagnostics.version'
    Assert-ProfileInteger $diagnostics.phase 'diagnostics.phase'
    Assert-ProfileEqual $diagnostics.version 1 'diagnostics.version'
    Assert-ProfileInteger $diagnostics.epoch 'diagnostics.epoch' 1
    Assert-ProfileEqual $diagnostics.phase 0 'completed diagnostics phase'
    $counter = $diagnostics.counters
    Assert-ProfileFields $counter (Get-RuntimeCounterNames) 'diagnostics.counters'
    Assert-ProfileIntegers $counter 'diagnostics.counters'
    Assert-ProfileEqual ([decimal]$counter.allocation_calls) $operations 'allocation counter'
    Assert-ProfileEqual $counter.reclaimed_objects $counter.allocation_calls 'complete reclamation'
    Assert-ProfileEqual $counter.reclaimed_bytes $counter.allocation_bytes 'reclaimed byte accounting'
    Assert-ProfileEqual ([decimal]$counter.mark_objects + [decimal]$counter.assist_objects) ([decimal]$counter.new_marks) 'mark completion'
    if ([decimal]$counter.new_marks + [decimal]$counter.already_marked -gt [decimal]$counter.shade_calls) { throw 'Shade accounting is invalid.' }
    if ($counter.mark_max_reference_slots -gt 64 -or $counter.mark_max_objects -gt 32) { throw 'Trace batch exceeded its work budget.' }
    $expectedBounds = @(1000, 4000, 16000, 64000, 256000, 1000000, 4000000, 16000000, 64000000, 256000000, 1000000000, [ulong]::MaxValue)
    Assert-ProfileArray $diagnostics.histogram_upper_bounds_ns 'histogram bounds'
    if ($diagnostics.histogram_upper_bounds_ns.Count -ne $expectedBounds.Count) { throw 'Histogram bounds are incomplete.' }
    for ($index = 0; $index -lt $expectedBounds.Count; $index++) {
        Assert-ProfileInteger $diagnostics.histogram_upper_bounds_ns[$index] 'histogram bound'
        Assert-ProfileEqual ([decimal]$diagnostics.histogram_upper_bounds_ns[$index]) ([decimal]$expectedBounds[$index]) 'histogram bound'
    }
    Assert-ProfileFields $diagnostics.histograms (Get-RuntimeHistogramNames) 'histograms'
    foreach ($name in Get-RuntimeHistogramNames) {
        Test-RuntimeHistogram $diagnostics.histograms[$name] $expectedBounds "histograms.$name"
    }
    foreach ($pair in @(@('mark_batch', 'mark_batches'), @('assist_batch', 'assist_batches'), @('sweep_batch', 'sweep_batches'))) {
        Assert-ProfileEqual $diagnostics.histograms[$pair[0]].samples $counter[$pair[1]] 'batch histogram count'
    }
    Assert-ProfileEqual $diagnostics.histograms.root_snapshot.samples $diagnostics.histograms.safepoint_wait.samples 'handshake sample count'
    Assert-ProfileEqual ([decimal]$diagnostics.histograms.root_snapshot.samples) ([decimal]$counter.collections * 2 + [decimal]$counter.termination_retries) 'root snapshot count'
    $queue = $diagnostics.ready_queue
    Assert-ProfileFields $queue @('regions', 'peak_regions', 'claims', 'publications', 'coalesced_publications') 'ready_queue'
    Assert-ProfileIntegers $queue 'ready_queue'
    Assert-ProfileEqual $queue.regions 0 'drained ready queue'
    Assert-ProfileEqual $queue.claims $queue.publications 'ready queue claim accounting'
    if ($queue.peak_regions -gt $queue.publications) { throw 'Ready queue peak exceeds publications.' }
    Assert-ProfileFields $Profile.allocator @('committed_bytes', 'reserved_bytes', 'capacity_bytes', 'span_count', 'large_object_count', 'small_object_count', 'reused_slots') 'allocator'
    Assert-ProfileIntegers $Profile.allocator 'allocator'
    foreach ($key in @('capacity_bytes', 'large_object_count', 'small_object_count')) {
        Assert-ProfileEqual $Profile.allocator[$key] 0 "final allocator.$key"
    }
    # One empty 64 KiB span per size class can remain. Reservation/commitment is not live capacity.
    Assert-ProfileInteger $Profile.allocator.span_count 'retained empty spans' 0 44
    Assert-ProfileEqual ([decimal]$Profile.allocator.committed_bytes) ([decimal]$Profile.allocator.span_count * 65536) 'retained span commitment'
    Assert-ProfileEqual $Profile.allocator.reserved_bytes $Profile.allocator.committed_bytes 'retained span reservation'
    if ($Profile.allocator.committed_bytes -gt $settings.heap_limit_bytes) { throw 'Retained spans exceed the physical budget.' }
    Assert-ProfileFields $Profile.cache @('hits', 'batch_refills', 'cached_slots', 'returned_slots', 'flushes', 'direct_allocations') 'cache'
    Assert-ProfileIntegers $Profile.cache 'cache'
    Assert-ProfileEqual $Profile.cache.cached_slots 0 'final cached slots'
    Assert-ProfileFields $Profile.heap @('registered_bytes', 'object_count', 'live_bytes', 'completed_epoch') 'heap'
    Assert-ProfileIntegers $Profile.heap 'heap'
    foreach ($key in @('registered_bytes', 'object_count', 'live_bytes')) { Assert-ProfileEqual $Profile.heap[$key] 0 "final heap.$key" }
    Assert-ProfileEqual $Profile.heap.completed_epoch $diagnostics.epoch 'completed epoch'
    Assert-ProfileArray $Profile.checkpoints 'checkpoints'
    $expectedCheckpoints = if ($settings.scenario -eq 'concurrent') { 1 } else { $settings.cycles * 2 }
    Assert-ProfileEqual $Profile.checkpoints.Count $expectedCheckpoints 'checkpoint count'
    $epoch = [decimal]0
    $checkpointIndex = 0
    foreach ($checkpoint in $Profile.checkpoints) {
        Assert-ProfileFields $checkpoint @('cycle', 'expected_objects', 'actual_objects', 'committed_bytes', 'live_bytes', 'epoch') 'checkpoint'
        Assert-ProfileIntegers $checkpoint 'checkpoint'
        Assert-ProfileEqual $checkpoint.actual_objects $checkpoint.expected_objects 'checkpoint reachability'
        $expectedCycle = if ($settings.scenario -eq 'concurrent') { $settings.cycles } else { [math]::Floor($checkpointIndex / 2) }
        $expectedObjects = if ($settings.scenario -eq 'concurrent' -or $checkpointIndex % 2 -eq 1) {
            0
        } elseif ($settings.scenario -eq 'wide') {
            $settings.nodes + 1
        } elseif ($settings.scenario -eq 'churn') {
            [math]::Min($settings.nodes, $settings.roots)
        } else {
            $settings.nodes
        }
        Assert-ProfileEqual $checkpoint.cycle $expectedCycle 'checkpoint workload cycle'
        Assert-ProfileEqual $checkpoint.expected_objects $expectedObjects 'checkpoint workload expectation'
        if ($checkpoint.live_bytes -lt [decimal]$expectedObjects * 16 -or
            ($expectedObjects -eq 0 -and $checkpoint.live_bytes -ne 0)) { throw 'Checkpoint live bytes disagree with its object population.' }
        if ($checkpoint.epoch -le $epoch -or $checkpoint.epoch -gt $diagnostics.epoch) { throw 'Checkpoint epoch order is invalid.' }
        if ($checkpoint.committed_bytes -gt $settings.heap_limit_bytes -or $checkpoint.live_bytes -gt $checkpoint.committed_bytes) {
            throw 'Checkpoint exceeds its physical heap budget.'
        }
        $epoch = [decimal]$checkpoint.epoch
        $checkpointIndex++
    }
    Test-RuntimeEventRing $Profile.event_ring $diagnostics.epoch
}

Export-ModuleMember -Function Test-RuntimeProfile
