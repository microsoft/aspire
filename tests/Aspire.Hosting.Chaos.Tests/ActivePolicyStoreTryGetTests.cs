// <copyright file="ActivePolicyStoreTryGetTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ActivePolicyStoreTryGetTests
{
    private static ActivePolicy Make(string id, DateTimeOffset? expiresAt = null) => new(
        Id: id, Matcher: null,
        Latency: new LatencyConfig(10, 20, 1.0, null),
        Error: null, ReplayDuplicate: null, DropResponse: null, RateLimit: null,
        HeaderTamper: null, PartialResponse: null, IdempotencyCollision: null, SlowResponse: null,
        ExpiresAt: expiresAt);

    [Fact]
    public void TryGet_EmptyStore_ReturnsNull()
    {
        var store = new ActivePolicyStore();
        Assert.Null(store.TryGet("any"));
    }

    [Fact]
    public void TryGet_KnownId_ReturnsPolicy()
    {
        var store = new ActivePolicyStore();
        store.Add(Make("a"));
        store.Add(Make("b"));

        var got = store.TryGet("b");

        Assert.NotNull(got);
        Assert.Equal("b", got!.Id);
    }

    [Fact]
    public void TryGet_UnknownId_ReturnsNull()
    {
        var store = new ActivePolicyStore();
        store.Add(Make("a"));

        Assert.Null(store.TryGet("nonexistent"));
    }

    [Fact]
    public void TryGet_ExpiredPolicy_ReturnsNull()
    {
        var store = new ActivePolicyStore();
        store.Add(Make("expired", DateTimeOffset.UtcNow.AddMilliseconds(-100)));

        Assert.Null(store.TryGet("expired"));
    }

    [Fact]
    public void TryGet_FutureExpiry_ReturnsPolicy()
    {
        var store = new ActivePolicyStore();
        store.Add(Make("alive", DateTimeOffset.UtcNow.AddMinutes(5)));

        var got = store.TryGet("alive");

        Assert.NotNull(got);
        Assert.Equal("alive", got!.Id);
    }

    [Fact]
    public void TryGet_CaseSensitiveIdLookup()
    {
        // Policy ids are case-sensitive across the system (the dictionary key
        // composition logic for fire counters / failFirst / fire-once all uses
        // ordinal comparison). TryGet should match that.
        var store = new ActivePolicyStore();
        store.Add(Make("MyPolicy"));

        Assert.NotNull(store.TryGet("MyPolicy"));
        Assert.Null(store.TryGet("mypolicy"));
    }

    [Fact]
    public void TryGet_NullId_Throws()
    {
        var store = new ActivePolicyStore();
        Assert.Throws<ArgumentNullException>(() => store.TryGet(null!));
    }
}
