// <copyright file="ActivePolicyStoreFireCountTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ActivePolicyStoreFireCountTests
{
    [Fact]
    public void GetFireCounts_NeverRecorded_ReturnsEmpty()
    {
        var store = new ActivePolicyStore();

        Assert.Empty(store.GetFireCounts("p1"));
    }

    [Fact]
    public void RecordFire_OneTransform_CounterIs1()
    {
        var store = new ActivePolicyStore();
        store.RecordFire("p1", "latency");

        var counts = store.GetFireCounts("p1");

        Assert.Single(counts);
        Assert.Equal(1, counts["latency"]);
    }

    [Fact]
    public void RecordFire_MultipleTimes_CounterIncrements()
    {
        var store = new ActivePolicyStore();
        store.RecordFire("p1", "latency");
        store.RecordFire("p1", "latency");
        store.RecordFire("p1", "latency");

        Assert.Equal(3, store.GetFireCounts("p1")["latency"]);
    }

    [Fact]
    public void RecordFire_DifferentTransforms_TrackedSeparately()
    {
        var store = new ActivePolicyStore();
        store.RecordFire("p1", "latency");
        store.RecordFire("p1", "latency");
        store.RecordFire("p1", "error");

        var counts = store.GetFireCounts("p1");

        Assert.Equal(2, counts["latency"]);
        Assert.Equal(1, counts["error"]);
        Assert.False(counts.ContainsKey("replay-duplicate"));
    }

    [Fact]
    public void RecordFire_DifferentPolicies_TrackedSeparately()
    {
        var store = new ActivePolicyStore();
        store.RecordFire("p1", "latency");
        store.RecordFire("p2", "latency");

        Assert.Equal(1, store.GetFireCounts("p1")["latency"]);
        Assert.Equal(1, store.GetFireCounts("p2")["latency"]);
    }

    [Fact]
    public void RecordFire_OnlyReturnsThisPolicysCounts()
    {
        var store = new ActivePolicyStore();
        store.RecordFire("p1", "latency");
        store.RecordFire("p2", "error");
        store.RecordFire("p3", "replay-duplicate");

        var counts = store.GetFireCounts("p2");

        Assert.Single(counts);
        Assert.Equal(1, counts["error"]);
    }

    [Fact]
    public void HasFireRecord_NeverFired_ReturnsFalse()
    {
        var store = new ActivePolicyStore();

        Assert.False(store.HasFireRecord("p1"));
    }

    [Fact]
    public void HasFireRecord_AfterFire_ReturnsTrueForThatPolicyOnly()
    {
        var store = new ActivePolicyStore();
        store.RecordFire("p1", "error");

        Assert.True(store.HasFireRecord("p1"));
        Assert.False(store.HasFireRecord("p2"));
    }

    [Fact]
    public void GetFireCounts_SurvivesSweepExpired_RetainsTally()
    {
        // A long-running test arms a short-TTL fault, lets it expire (so a downstream
        // recovery can succeed), then asserts fire counts AFTER the wait — by which point
        // PolicyExpirationService has swept the policy from the active set. The retained
        // fire tally must survive the sweep so the assertion still sees the fires.
        var store = new ActivePolicyStore();
        store.Add(Make("expiring", DateTimeOffset.UtcNow.AddMilliseconds(-100)));
        store.RecordFire("expiring", "error");

        var swept = store.SweepExpired();

        Assert.Equal(1, swept);                        // removed from the active set
        Assert.Null(store.TryGet("expiring"));         // ... no longer active
        Assert.True(store.HasFireRecord("expiring"));  // ... but the fire record is retained
        Assert.Equal(1, store.GetFireCounts("expiring")["error"]);
    }

    [Fact]
    public void Remove_ExplicitDelete_RetainsFireCounts()
    {
        var store = new ActivePolicyStore();
        store.Add(Make("p1"));
        store.RecordFire("p1", "latency");
        store.RecordFire("p1", "latency");

        var removed = store.Remove("p1");

        Assert.True(removed);
        Assert.Null(store.TryGet("p1"));
        Assert.True(store.HasFireRecord("p1"));
        Assert.Equal(2, store.GetFireCounts("p1")["latency"]);
    }

    [Fact]
    public void Add_ReinstallSamePolicyId_ResetsFireCounts()
    {
        // Re-arming a policy id starts a fresh tally — counters persist independently of the
        // policy entry, so without the reset a re-armed id would accumulate across arms.
        var store = new ActivePolicyStore();
        store.Add(Make("p1"));
        store.RecordFire("p1", "error");
        store.RecordFire("p1", "error");
        Assert.Equal(2, store.GetFireCounts("p1")["error"]);

        store.Add(Make("p1")); // re-install same id

        Assert.False(store.HasFireRecord("p1"));
        Assert.Empty(store.GetFireCounts("p1"));
    }

    private static ActivePolicy Make(string id, DateTimeOffset? expiresAt = null) => new(
        Id: id, Matcher: null,
        Latency: new LatencyConfig(10, 20, 1.0, null),
        Error: null, ReplayDuplicate: null, DropResponse: null, RateLimit: null,
        HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
        ExpiresAt: expiresAt);
}
