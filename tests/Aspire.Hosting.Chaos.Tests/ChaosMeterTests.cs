// <copyright file="ChaosMeterTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.Metrics;
using ChaosProxy.Container.Policy;
using ChaosProxy.Container.Telemetry;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// Asserts that <see cref="ChaosMeter"/> emits <c>chaos.proxy.fires</c> measurements
/// when transforms fire end-to-end through the pipeline, with the correct
/// <c>policy_id</c>, <c>transform</c>, and <c>fire_reason</c> tag schema. The
/// dashboard's Metrics tab depends on this tag shape — schema drift would silently
/// break harness visibility.
/// </summary>
public class ChaosMeterTests
{
    [Fact]
    public async Task ErrorMiddleware_Fires_EmitsCounterWithProbabilityFireReason()
    {
        var measurements = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = SubscribeToChaosFires(measurements);

        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "p-err", Matcher: null,
            Latency: null,
            Error: new ErrorConfig(503, null, null, null, 1.0, null),
            ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));

        await fx.Client.GetAsync("/api/x");
        await fx.Client.GetAsync("/api/x");

        var mine = FilterByPolicyId(measurements, "p-err");
        Assert.Equal(2, mine.Count);
        Assert.All(mine, m =>
        {
            Assert.Equal(1, m.Value);
            Assert.Equal("error", m.Tags["transform"]);
            Assert.Equal("probability", m.Tags["fire_reason"]);
        });
    }

    [Fact]
    public async Task LatencyMiddleware_FailFirst_EmitsCounterWithFailFirstFireReason()
    {
        var measurements = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = SubscribeToChaosFires(measurements);

        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "p-lat", Matcher: null,
            Latency: new LatencyConfig(1, 2, 0.0, FailFirst: 2),
            Error: null, ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));

        await fx.Client.GetAsync("/api/x");
        await fx.Client.GetAsync("/api/x");
        await fx.Client.GetAsync("/api/x"); // 3rd request: fail-first budget exhausted, probability=0 → no fire

        var mine = FilterByPolicyId(measurements, "p-lat");
        Assert.Equal(2, mine.Count);
        Assert.All(mine, m =>
        {
            Assert.Equal("latency", m.Tags["transform"]);
            Assert.Equal("fail-first", m.Tags["fire_reason"]);
        });
    }

    [Fact]
    public async Task RateLimitMiddleware_BlockedRequests_EmitsCounterWithRateExceededFireReason()
    {
        var measurements = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = SubscribeToChaosFires(measurements);

        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "p-rl", Matcher: null,
            Latency: null, Error: null, ReplayDuplicate: null, DropResponse: null,
            RateLimit: new RateLimitConfig(1, 10_000, 429, null),
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));

        await fx.Client.GetAsync("/api/x"); // admitted (no fire)
        await fx.Client.GetAsync("/api/x"); // blocked → fire
        await fx.Client.GetAsync("/api/x"); // blocked → fire

        var mine = FilterByPolicyId(measurements, "p-rl");
        Assert.Equal(2, mine.Count);
        Assert.All(mine, m =>
        {
            Assert.Equal("rate-limit", m.Tags["transform"]);
            Assert.Equal("rate-exceeded", m.Tags["fire_reason"]);
        });
    }

    [Fact]
    public async Task MeasurementTagSchema_IsStableAcrossTransforms()
    {
        var measurements = new List<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = SubscribeToChaosFires(measurements);

        await using var fx = new ChaosPipelineFixture();
        fx.Store.Add(new ActivePolicy(
            Id: "p-multi", Matcher: null,
            Latency: new LatencyConfig(1, 2, 1.0, null),
            Error: new ErrorConfig(503, null, null, null, 1.0, null),
            ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null));

        await fx.Client.GetAsync("/api/x");

        // Both latency and error fired in the same request → 2 measurements, same tag keys.
        var mine = FilterByPolicyId(measurements, "p-multi");
        Assert.Equal(2, mine.Count);
        var expectedKeys = new[] { "policy_id", "transform", "fire_reason" };
        Assert.All(mine, m =>
        {
            Assert.Equal(expectedKeys.OrderBy(k => k), m.Tags.Keys.OrderBy(k => k));
        });
        Assert.Contains(mine, m => (string?)m.Tags["transform"] == "latency");
        Assert.Contains(mine, m => (string?)m.Tags["transform"] == "error");
    }

    private static List<(long Value, Dictionary<string, object?> Tags)> FilterByPolicyId(
        List<(long Value, Dictionary<string, object?> Tags)> all, string policyId)
    {
        lock (all)
        {
            return all
                .Where(m => (string?)m.Tags["policy_id"] == policyId)
                .ToList();
        }
    }

    private static MeterListener SubscribeToChaosFires(
        List<(long Value, Dictionary<string, object?> Tags)> sink)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ChaosMeter.Name &&
                    instrument.Name == "chaos.proxy.fires")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var dict = new Dictionary<string, object?>();
            for (var i = 0; i < tags.Length; i++)
            {
                dict[tags[i].Key] = tags[i].Value;
            }

            lock (sink)
            {
                sink.Add((value, dict));
            }
        });
        listener.Start();
        return listener;
    }
}
