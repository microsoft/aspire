// <copyright file="ActivePolicyStoreStateProbeTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// Tests the cross-policy aggregations used by GET /chaos/state:
/// <see cref="ActivePolicyStore.GetAllFireCounts"/> and
/// <see cref="ActivePolicyStore.GetArmedFireOnceTriggers"/>.
/// </summary>
public class ActivePolicyStoreStateProbeTests
{
    [Fact]
    public void GetAllFireCounts_EmptyStore_ReturnsEmpty()
    {
        var store = new ActivePolicyStore();
        Assert.Empty(store.GetAllFireCounts());
    }

    [Fact]
    public void GetAllFireCounts_SinglePolicy_SumsByTransform()
    {
        var store = new ActivePolicyStore();
        store.RecordFire("p1", "latency");
        store.RecordFire("p1", "latency");
        store.RecordFire("p1", "error");

        var counts = store.GetAllFireCounts();

        Assert.Equal(2, counts["latency"]);
        Assert.Equal(1, counts["error"]);
    }

    [Fact]
    public void GetAllFireCounts_MultiplePolicies_AggregatesAcross()
    {
        var store = new ActivePolicyStore();
        store.RecordFire("p1", "latency");
        store.RecordFire("p2", "latency");
        store.RecordFire("p3", "latency");
        store.RecordFire("p1", "error");

        var counts = store.GetAllFireCounts();

        Assert.Equal(3, counts["latency"]);
        Assert.Equal(1, counts["error"]);
    }

    [Fact]
    public void GetArmedFireOnceTriggers_EmptyStore_ReturnsEmpty()
    {
        var store = new ActivePolicyStore();
        Assert.Empty(store.GetArmedFireOnceTriggers());
    }

    [Fact]
    public void GetArmedFireOnceTriggers_GlobalAndPerPolicy_BothListed()
    {
        var store = new ActivePolicyStore();
        store.SetFireOnce("latency");
        store.SetFireOnce("error");
        store.SetFireOnceForPolicy("p1", "error");

        var triggers = store.GetArmedFireOnceTriggers();

        Assert.Contains("latency", triggers);
        Assert.Contains("error", triggers);
        Assert.Contains("p1:error", triggers);
        Assert.Equal(3, triggers.Count);
    }

    [Fact]
    public void GetArmedFireOnceTriggers_AfterConsume_TriggerDisappears()
    {
        var store = new ActivePolicyStore();
        store.SetFireOnce("latency");
        Assert.Contains("latency", store.GetArmedFireOnceTriggers());

        store.ConsumeFireOnce("latency");

        Assert.DoesNotContain("latency", store.GetArmedFireOnceTriggers());
    }

    [Fact]
    public void GetArmedFireOnceTriggers_SortedAlphabetically_ForStableDiff()
    {
        var store = new ActivePolicyStore();
        store.SetFireOnceForPolicy("zoo", "error");
        store.SetFireOnceForPolicy("alpha", "latency");
        store.SetFireOnce("middle");

        var triggers = store.GetArmedFireOnceTriggers();

        Assert.Equal(new[] { "alpha:latency", "middle", "zoo:error" }, triggers);
    }
}
