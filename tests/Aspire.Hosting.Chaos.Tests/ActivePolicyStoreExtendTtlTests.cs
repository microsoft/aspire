// <copyright file="ActivePolicyStoreExtendTtlTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ActivePolicyStoreExtendTtlTests
{
    private static ActivePolicy Make(string id, DateTimeOffset? expiresAt = null) => new(
        Id: id, Matcher: null,
        Latency: new LatencyConfig(10, 20, 1.0, null),
        Error: null, ReplayDuplicate: null, DropResponse: null, RateLimit: null,
        HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
        ExpiresAt: expiresAt);

    [Fact]
    public void ExtendTtl_UnknownId_ReturnsFalse()
    {
        var store = new ActivePolicyStore();

        Assert.False(store.ExtendTtl("nope", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void ExtendTtl_ExpiredPolicy_ReturnsFalseAndDoesNotResurrect()
    {
        var store = new ActivePolicyStore();
        store.Add(Make("dead", DateTimeOffset.UtcNow.AddMilliseconds(-100)));

        var extended = store.ExtendTtl("dead", TimeSpan.FromMinutes(5));

        Assert.False(extended);
        // Expired policy must still be filtered out by GetActive/TryGet.
        Assert.Null(store.TryGet("dead"));
    }

    [Fact]
    public void ExtendTtl_AlivePolicy_BumpsExpiry()
    {
        var store = new ActivePolicyStore();
        var initialExpiry = DateTimeOffset.UtcNow.AddSeconds(30);
        store.Add(Make("alive", initialExpiry));

        var extended = store.ExtendTtl("alive", TimeSpan.FromMinutes(10));

        Assert.True(extended);
        var refreshed = store.TryGet("alive");
        Assert.NotNull(refreshed);
        Assert.NotNull(refreshed!.ExpiresAt);
        // New expiry should be well past the original 30s expiry.
        Assert.True(refreshed.ExpiresAt!.Value > initialExpiry.AddMinutes(1));
    }

    [Fact]
    public void ExtendTtl_PolicyWithNoExpiry_AppliesExpiry()
    {
        var store = new ActivePolicyStore();
        store.Add(Make("forever", expiresAt: null));

        var extended = store.ExtendTtl("forever", TimeSpan.FromMinutes(2));

        Assert.True(extended);
        var refreshed = store.TryGet("forever");
        Assert.NotNull(refreshed!.ExpiresAt);
    }

    [Fact]
    public void ExtendTtl_ZeroTimeSpan_ClearsExpiry()
    {
        var store = new ActivePolicyStore();
        store.Add(Make("had-expiry", DateTimeOffset.UtcNow.AddMinutes(5)));

        var extended = store.ExtendTtl("had-expiry", TimeSpan.Zero);

        Assert.True(extended);
        var refreshed = store.TryGet("had-expiry");
        Assert.NotNull(refreshed);
        Assert.Null(refreshed!.ExpiresAt);
    }

    [Fact]
    public void ExtendTtl_PreservesOtherFields()
    {
        var store = new ActivePolicyStore();
        store.Add(new ActivePolicy(
            Id: "p", Matcher: new RequestMatcher("GET", null, null),
            Latency: new LatencyConfig(50, 100, 0.5, 3),
            Error: null, ReplayDuplicate: null, DropResponse: null, RateLimit: null,
            HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(30)));

        store.ExtendTtl("p", TimeSpan.FromMinutes(10));

        var refreshed = store.TryGet("p")!;
        Assert.Equal("GET", refreshed.Matcher!.Method);
        Assert.Equal(50, refreshed.Latency!.MinMs);
        Assert.Equal(100, refreshed.Latency.MaxMs);
        Assert.Equal(0.5, refreshed.Latency.Probability);
        Assert.Equal(3, refreshed.Latency.FailFirst);
    }

    [Fact]
    public void ExtendTtl_NullId_Throws()
    {
        var store = new ActivePolicyStore();
        Assert.Throws<ArgumentNullException>(() => store.ExtendTtl(null!, TimeSpan.FromMinutes(5)));
    }
}
