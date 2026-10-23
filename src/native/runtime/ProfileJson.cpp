// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include "GcProfile.h"
#include <array>
#include <charconv>
#include <string_view>

namespace OpenDot::Sol::Runtime::Test
{
namespace
{
/// Small harness-only writer. There is no general JSON parser or product configuration surface here.
/// A stack distinguishes objects from arrays and rejects missing names and unbalanced containers.
class Json final
{
    struct Level final
    {
        bool Object;
        bool First = true;
    };

    std::vector<Level> _stack;
    std::string _text;

    void Quote(std::string_view value)
    {
        _text += '"';
        constexpr char hex[] = "0123456789abcdef";
        for (const unsigned char letter : value)
        {
            switch (letter)
            {
            case '"':
                _text += "\\\"";
                break;
            case '\\':
                _text += "\\\\";
                break;
            case '\n':
                _text += "\\n";
                break;
            case '\r':
                _text += "\\r";
                break;
            case '\t':
                _text += "\\t";
                break;
            default:
                if (letter < 32)
                {
                    _text += "\\u00";
                    _text += hex[letter >> 4];
                    _text += hex[letter & 15];
                }
                else
                {
                    _text += static_cast<char>(letter);
                }
                break;
            }
        }
        _text += '"';
    }

    void Prefix(const char *name)
    {
        if (_stack.empty())
        {
            if (!_text.empty() || name)
            {
                sol_rt_fail(7);
            }
            return;
        }
        auto &level = _stack.back();
        if (level.Object != (name != nullptr))
        {
            sol_rt_fail(7);
        }
        if (!level.First)
        {
            _text += ',';
        }
        level.First = false;
        if (name)
        {
            Quote(name);
            _text += ':';
        }
    }

public:
    void Begin(bool object, const char *name = nullptr)
    {
        Prefix(name);
        _text += object ? '{' : '[';
        _stack.push_back({object});
    }

    void End()
    {
        if (_stack.empty())
        {
            sol_rt_fail(7);
        }
        _text += _stack.back().Object ? '}' : ']';
        _stack.pop_back();
    }

    void Number(std::uint64_t value, const char *name = nullptr)
    {
        Prefix(name);
        std::array<char, 32> digits;
        const auto result = std::to_chars(digits.data(), digits.data() + digits.size(), value);
        if (result.ec != std::errc{})
        {
            sol_rt_fail(7);
        }
        _text.append(digits.data(), result.ptr);
    }

    void Text(const char *value, const char *name)
    {
        Prefix(name);
        Quote(value);
    }

    void Boolean(bool value, const char *name)
    {
        Prefix(name);
        _text += value ? "true" : "false";
    }

    std::string Finish()
    {
        if (!_stack.empty() || _text.empty())
        {
            sol_rt_fail(7);
        }
        _text += '\n';
        return std::move(_text);
    }
};

void Histogram(Json &json, const GcDistribution &distribution)
{
    json.Number(distribution.Samples, "samples");
    json.Number(distribution.TotalNanoseconds, "total_ns");
    json.Number(distribution.MaximumNanoseconds, "max_ns");
    json.Number(Heap::DistributionUpperBound(distribution, 50), "p50_upper_ns");
    json.Number(Heap::DistributionUpperBound(distribution, 95), "p95_upper_ns");
    json.Number(Heap::DistributionUpperBound(distribution, 99), "p99_upper_ns");
    json.Begin(false, "buckets");
    for (auto value : distribution.Buckets)
    {
        json.Number(value);
    }
    json.End();
}

void Allocator(Json &json, const AllocatorStats &allocator)
{
    json.Begin(true, "allocator");
    json.Number(allocator.CommittedBytes, "committed_bytes");
    json.Number(allocator.ReservedBytes, "reserved_bytes");
    json.Number(allocator.CapacityBytes, "capacity_bytes");
    json.Number(allocator.SpanCount, "span_count");
    json.Number(allocator.LargeObjectCount, "large_object_count");
    json.Number(allocator.SmallObjectCount, "small_object_count");
    json.Number(allocator.ReusedSlots, "reused_slots");
    json.End();
}

void Cache(Json &json, const ThreadAllocationStats &cache)
{
    json.Begin(true, "cache");
    json.Number(cache.CacheHits, "hits");
    json.Number(cache.BatchRefills, "batch_refills");
    json.Number(cache.CachedSlots, "cached_slots");
    json.Number(cache.ReturnedSlots, "returned_slots");
    json.Number(cache.CacheFlushes, "flushes");
    json.Number(cache.DirectAllocations, "direct_allocations");
    json.End();
}

void Queue(Json &json, const GcDiagnosticSnapshot &diagnostics)
{
    json.Begin(true, "ready_queue");
    json.Number(diagnostics.ReadyRegions, "regions");
    json.Number(diagnostics.PeakReadyRegions, "peak_regions");
    json.Number(diagnostics.RegionClaims, "claims");
    json.Number(diagnostics.RegionPublications, "publications");
    json.Number(diagnostics.CoalescedPublications, "coalesced_publications");
    json.End();
}

void Events(Json &json, const ProfileResult &result)
{
    const auto &snapshot = result.Diagnostics;
    json.Begin(true, "event_ring");
    json.Boolean(snapshot.EventsEnabled != 0, "enabled");
    json.Number(4096, "capacity");
    json.Number(snapshot.FirstEvent, "first_sequence");
    json.Number(snapshot.NextEvent, "next_sequence");
    json.Number(snapshot.DroppedEvents, "dropped_events");
    json.Begin(false, "events");
    for (const auto &event : result.Events)
    {
        json.Begin(true);
        json.Number(event.Sequence, "sequence");
        json.Number(event.TimestampNanoseconds, "timestamp_ns");
        json.Number(event.Epoch, "epoch");
        json.Text(Heap::EventName(static_cast<GcEventKind>(event.Kind)), "kind");
        json.Number(event.Work, "work");
        json.Number(event.DurationNanoseconds, "duration_ns");
        json.End();
    }
    json.End();
    json.End();
}
}

std::string SerializeProfile(const ProfileResult &result)
{
    Json json;
    json.Begin(true);
    json.Number(1, "schema_version");
    json.Text("yellow-tea-native-profile", "kind");
    json.Begin(true, "settings");
    json.Text(ScenarioName(result.Options.Scenario), "scenario");
    json.Number(result.Options.Nodes, "nodes");
    json.Number(result.Options.Cycles, "cycles");
    json.Number(result.Options.Roots, "roots");
    json.Number(result.Options.Threads, "threads");
    json.Number(result.Options.Seed, "seed");
    json.Number(result.HeapLimit, "heap_limit_bytes");
    json.End();
    json.Begin(true, "environment");
    json.Text("windows-x64", "platform");
    json.Number(_MSC_VER, "msvc_version");
    json.Number(result.LogicalProcessors, "logical_processors");
    json.Boolean(result.AddressSanitized, "address_sanitized");
    json.Text("steady_clock", "clock");
    json.End();
    json.Number(result.ElapsedNanoseconds, "elapsed_ns");
    json.Begin(true, "allocation_latency");
    json.Text("all_calls_nearest_rank", "method");
    json.Number(result.Allocations.Nanoseconds.size(), "samples");
    json.Number(result.Allocations.Total, "total_ns");
    json.Number(result.Allocations.Maximum, "max_ns");
    json.Number(result.Allocations.Percentile(50), "p50_ns");
    json.Number(result.Allocations.Percentile(95), "p95_ns");
    json.Number(result.Allocations.Percentile(99), "p99_ns");
    json.End();
    json.Begin(true, "diagnostics");
    json.Number(result.Diagnostics.Version, "version");
    json.Number(result.Diagnostics.Epoch, "epoch");
    json.Number(result.Diagnostics.Phase, "phase");
    json.Begin(true, "counters");
    for (std::size_t index = 0; index < result.Diagnostics.Counters.size(); ++index)
    {
        json.Number(result.Diagnostics.Counters[index], Heap::CounterName(static_cast<GcCounter>(index)));
    }
    json.End();
    json.Begin(false, "histogram_upper_bounds_ns");
    for (auto upper : GcHistogramBounds)
    {
        json.Number(upper);
    }
    json.End();
    json.Begin(true, "histograms");
    for (std::size_t index = 0; index < result.Diagnostics.Distributions.size(); ++index)
    {
        json.Begin(true, Heap::HistogramName(static_cast<GcHistogram>(index)));
        Histogram(json, result.Diagnostics.Distributions[index]);
        json.End();
    }
    json.End();
    Queue(json, result.Diagnostics);
    json.End();
    Allocator(json, result.Allocator);
    Cache(json, result.Cache);
    json.Begin(true, "heap");
    json.Number(result.Heap.HeapBytes, "registered_bytes");
    json.Number(result.Heap.ObjectCount, "object_count");
    json.Number(result.Heap.LiveBytes, "live_bytes");
    json.Number(result.Heap.Epoch, "completed_epoch");
    json.End();
    json.Begin(false, "checkpoints");
    for (const auto &checkpoint : result.Checkpoints)
    {
        json.Begin(true);
        json.Number(checkpoint.Cycle, "cycle");
        json.Number(checkpoint.ExpectedObjects, "expected_objects");
        json.Number(checkpoint.ActualObjects, "actual_objects");
        json.Number(checkpoint.CommittedBytes, "committed_bytes");
        json.Number(checkpoint.LiveBytes, "live_bytes");
        json.Number(checkpoint.Epoch, "epoch");
        json.End();
    }
    json.End();
    Events(json, result);
    json.End();
    return json.Finish();
}
}
