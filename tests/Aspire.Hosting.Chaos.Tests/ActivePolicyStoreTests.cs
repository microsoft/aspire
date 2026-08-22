// <copyright file="ActivePolicyStoreTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ActivePolicyStoreTests
{
    private static ActivePolicy NewPolicy(
        string id = "p1",
        LatencyConfig? latency = null,
        ErrorConfig? error = null,
        ReplayDuplicateConfig? replay = null,
        DropResponseConfig? drop = null,
        RateLimitConfig? rateLimit = null,
        HeaderTamperConfig? headerTamper = null,
        PartialResponseConfig? partial = null,
        IdempotencyCollisionConfig? idempotency = null,
        SlowResponseConfig? slow = null,
        DateTimeOffset? expiresAt = null)
        => new(id, Matcher: null, latency, error, replay, drop, rateLimit, headerTamper, partial, idempotency, slow, expiresAt);

    [Fact]
    public void Add_NewPolicy_AppearsInGetActive()
    {
        var store = new ActivePolicyStore();
        var policy = NewPolicy("p1", latency: new LatencyConfig(100, 200, 1.0, null));

        store.Add(policy);

        var active = store.GetActive();
        Assert.Single(active);
        Assert.Equal("p1", active[0].Id);
    }

    [Fact]
    public void Add_DuplicateId_ReplacesPriorPolicy()
    {
        var store = new ActivePolicyStore();
        store.Add(NewPolicy("p1", latency: new LatencyConfig(100, 200, 1.0, null)));
        store.Add(NewPolicy("p1", error: new ErrorConfig(503, null, null, null, 1.0, null)));

        var active = store.GetActive();
        Assert.Single(active);
        Assert.Null(active[0].Latency);
        Assert.NotNull(active[0].Error);
        Assert.Equal(503, active[0].Error!.Status);
    }

    [Fact]
    public void Add_PreservesInstallOrder()
    {
        var store = new ActivePolicyStore();
        store.Add(NewPolicy("first"));
        store.Add(NewPolicy("second"));
        store.Add(NewPolicy("third"));

        var active = store.GetActive();
        Assert.Equal(new[] { "first", "second", "third" }, active.Select(p => p.Id));
    }

    [Fact]
    public void Remove_ExistingId_RemovesPolicyAndReturnsTrue()
    {
        var store = new ActivePolicyStore();
        store.Add(NewPolicy("p1"));
        store.Add(NewPolicy("p2"));

        var removed = store.Remove("p1");

        Assert.True(removed);
        Assert.Single(store.GetActive());
        Assert.Equal("p2", store.GetActive()[0].Id);
    }

    [Fact]
    public void Remove_UnknownId_ReturnsFalse()
    {
        var store = new ActivePolicyStore();
        store.Add(NewPolicy("p1"));

        var removed = store.Remove("nonexistent");

        Assert.False(removed);
        Assert.Single(store.GetActive());
    }

    [Fact]
    public void GetActive_ExpiredPolicy_NotReturned()
    {
        var store = new ActivePolicyStore();
        store.Add(NewPolicy("alive", expiresAt: DateTimeOffset.UtcNow.AddMinutes(5)));
        store.Add(NewPolicy("expired", expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(-1)));

        var active = store.GetActive();

        Assert.Single(active);
        Assert.Equal("alive", active[0].Id);
    }

    [Fact]
    public void GetActive_NullExpiresAt_AlwaysReturned()
    {
        var store = new ActivePolicyStore();
        store.Add(NewPolicy("bootstrap", expiresAt: null));

        var active = store.GetActive();

        Assert.Single(active);
    }

    [Fact]
    public void SweepExpired_RemovesExpiredPoliciesAndReturnsCount()
    {
        var store = new ActivePolicyStore();
        store.Add(NewPolicy("a", expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(-100)));
        store.Add(NewPolicy("b", expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(-100)));
        store.Add(NewPolicy("c", expiresAt: DateTimeOffset.UtcNow.AddMinutes(5)));

        var removed = store.SweepExpired();

        Assert.Equal(2, removed);
        Assert.Single(store.GetActive());
        Assert.Equal("c", store.GetActive()[0].Id);
    }

    [Fact]
    public void IsPaused_NewStore_ReturnsFalse()
    {
        var store = new ActivePolicyStore();
        Assert.False(store.IsPaused);
    }

    [Fact]
    public void Pause_FlipsIsPaused()
    {
        var store = new ActivePolicyStore();
        store.Pause();
        Assert.True(store.IsPaused);
    }

    [Fact]
    public void Resume_AfterPause_FlipsBack()
    {
        var store = new ActivePolicyStore();
        store.Pause();
        store.Resume();
        Assert.False(store.IsPaused);
    }

    [Fact]
    public void Pause_CalledTwice_StaysPaused()
    {
        var store = new ActivePolicyStore();
        store.Pause();
        store.Pause();
        Assert.True(store.IsPaused);
    }

    [Fact]
    public void ConsumeFireOnce_NotArmed_ReturnsFalse()
    {
        var store = new ActivePolicyStore();
        Assert.False(store.ConsumeFireOnce("latency"));
    }

    [Fact]
    public void ConsumeFireOnce_AfterSetFireOnce_ReturnsTrueOnce()
    {
        var store = new ActivePolicyStore();
        store.SetFireOnce("latency");

        Assert.True(store.ConsumeFireOnce("latency"));
        Assert.False(store.ConsumeFireOnce("latency"));
    }

    [Fact]
    public void SetFireOnce_DifferentBuckets_IndependentTriggers()
    {
        var store = new ActivePolicyStore();
        store.SetFireOnce("latency");
        store.SetFireOnce("error");

        Assert.True(store.ConsumeFireOnce("latency"));
        Assert.True(store.ConsumeFireOnce("error"));
        Assert.False(store.ConsumeFireOnce("latency"));
        Assert.False(store.ConsumeFireOnce("error"));
    }

    [Fact]
    public void SetFireOnce_ArmedTwice_StillConsumedOnce()
    {
        var store = new ActivePolicyStore();
        store.SetFireOnce("latency");
        store.SetFireOnce("latency"); // idempotent

        Assert.True(store.ConsumeFireOnce("latency"));
        Assert.False(store.ConsumeFireOnce("latency"));
    }

    [Fact]
    public void SetFireOnce_NullBucket_Throws()
    {
        var store = new ActivePolicyStore();
        Assert.Throws<ArgumentNullException>(() => store.SetFireOnce(null!));
    }

    [Fact]
    public void SetFireOnce_EmptyBucket_Throws()
    {
        var store = new ActivePolicyStore();
        Assert.Throws<ArgumentException>(() => store.SetFireOnce(string.Empty));
    }

    [Fact]
    public void ConsumeFailFirstSlot_WithinBudget_ReturnsTrue()
    {
        var store = new ActivePolicyStore();

        Assert.True(store.ConsumeFailFirstSlot("latency", "p1", "req-key", budget: 3));
        Assert.True(store.ConsumeFailFirstSlot("latency", "p1", "req-key", budget: 3));
        Assert.True(store.ConsumeFailFirstSlot("latency", "p1", "req-key", budget: 3));
    }

    [Fact]
    public void ConsumeFailFirstSlot_ExceedsBudget_ReturnsFalse()
    {
        var store = new ActivePolicyStore();
        store.ConsumeFailFirstSlot("latency", "p1", "req-key", budget: 2);
        store.ConsumeFailFirstSlot("latency", "p1", "req-key", budget: 2);

        Assert.False(store.ConsumeFailFirstSlot("latency", "p1", "req-key", budget: 2));
    }

    [Fact]
    public void ConsumeFailFirstSlot_DifferentBucketsAreIndependent()
    {
        var store = new ActivePolicyStore();
        store.ConsumeFailFirstSlot("latency", "p1", "key", budget: 1);

        Assert.True(store.ConsumeFailFirstSlot("error", "p1", "key", budget: 1));
    }

    [Fact]
    public void ConsumeFailFirstSlot_DifferentPoliciesAreIndependent()
    {
        var store = new ActivePolicyStore();
        store.ConsumeFailFirstSlot("latency", "p1", "key", budget: 1);

        Assert.True(store.ConsumeFailFirstSlot("latency", "p2", "key", budget: 1));
    }

    [Fact]
    public void ConsumeFailFirstSlot_DifferentRequestKeysAreIndependent()
    {
        var store = new ActivePolicyStore();
        store.ConsumeFailFirstSlot("latency", "p1", "req-a", budget: 1);

        Assert.True(store.ConsumeFailFirstSlot("latency", "p1", "req-b", budget: 1));
    }
}
