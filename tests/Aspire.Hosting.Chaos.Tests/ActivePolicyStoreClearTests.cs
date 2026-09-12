// <copyright file="ActivePolicyStoreClearTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ActivePolicyStoreClearTests
{
    private static ActivePolicy Make(string id) => new(
        Id: id, Matcher: null,
        Latency: new LatencyConfig(10, 20, 1.0, null),
        Error: null, ReplayDuplicate: null, DropResponse: null, RateLimit: null,
        HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
        ExpiresAt: null);

    [Fact]
    public void Clear_EmptyStore_ReturnsZero()
    {
        var store = new ActivePolicyStore();

        Assert.Equal(0, store.Clear());
    }

    [Fact]
    public void Clear_RemovesAllPolicies_ReturnsRemovedCount()
    {
        var store = new ActivePolicyStore();
        store.Add(Make("a"));
        store.Add(Make("b"));
        store.Add(Make("c"));

        var removed = store.Clear();

        Assert.Equal(3, removed);
        Assert.Empty(store.GetActive());
    }

    [Fact]
    public void Clear_ResetsFireCounters()
    {
        var store = new ActivePolicyStore();
        store.Add(Make("a"));
        store.RecordFire("a", "latency");
        store.RecordFire("a", "latency");

        store.Clear();

        // Even before re-adding, the counters should be gone.
        Assert.Empty(store.GetFireCounts("a"));
    }

    [Fact]
    public void Clear_ResetsFireOnceTriggers()
    {
        var store = new ActivePolicyStore();
        store.SetFireOnce("latency");

        store.Clear();

        Assert.False(store.ConsumeFireOnce("latency"));
    }

    [Fact]
    public void Clear_ResetsFailFirstCounters()
    {
        var store = new ActivePolicyStore();
        // Burn through a 2-budget so further calls would return false.
        store.ConsumeFailFirstSlot("latency", "p", "key", 2);
        store.ConsumeFailFirstSlot("latency", "p", "key", 2);
        Assert.False(store.ConsumeFailFirstSlot("latency", "p", "key", 2));

        store.Clear();

        Assert.True(store.ConsumeFailFirstSlot("latency", "p", "key", 2));
    }

    [Fact]
    public void Clear_ResetsRateLimitWindows()
    {
        var store = new ActivePolicyStore();
        store.TryAdmitRateLimitedRequest("rl", "p", "key", 1, 60_000);
        Assert.False(store.TryAdmitRateLimitedRequest("rl", "p", "key", 1, 60_000));

        store.Clear();

        Assert.True(store.TryAdmitRateLimitedRequest("rl", "p", "key", 1, 60_000));
    }

    [Fact]
    public void Clear_ResetsIdempotencyKeyCache()
    {
        var store = new ActivePolicyStore();
        store.TryRecordIdempotencyKey("p", "key-a", 60_000);
        Assert.False(store.TryRecordIdempotencyKey("p", "key-a", 60_000));

        store.Clear();

        Assert.True(store.TryRecordIdempotencyKey("p", "key-a", 60_000));
    }

    [Fact]
    public void Clear_PreservesPauseFlag()
    {
        // Pause is a global toggle independent of any specific policy - we deliberately
        // do NOT reset it on Clear so harnesses can set up "pause + clear policies +
        // install fresh batch" workflows without re-pausing.
        var store = new ActivePolicyStore();
        store.Pause();
        store.Add(Make("a"));

        store.Clear();

        Assert.True(store.IsPaused);
    }
}
