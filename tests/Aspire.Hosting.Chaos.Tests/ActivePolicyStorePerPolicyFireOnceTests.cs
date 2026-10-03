// <copyright file="ActivePolicyStorePerPolicyFireOnceTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ActivePolicyStorePerPolicyFireOnceTests
{
    [Fact]
    public void ConsumeFireOnceForPolicy_NotArmed_ReturnsFalse()
    {
        var store = new ActivePolicyStore();

        Assert.False(store.ConsumeFireOnceForPolicy("p1", "latency"));
    }

    [Fact]
    public void SetFireOnceForPolicy_ConsumedOnce()
    {
        var store = new ActivePolicyStore();
        store.SetFireOnceForPolicy("p1", "latency");

        Assert.True(store.ConsumeFireOnceForPolicy("p1", "latency"));
        Assert.False(store.ConsumeFireOnceForPolicy("p1", "latency"));
    }

    [Fact]
    public void PerPolicyAndGlobal_AreIndependent()
    {
        var store = new ActivePolicyStore();
        store.SetFireOnce("latency");
        store.SetFireOnceForPolicy("p1", "latency");

        // Consuming per-policy does NOT consume global, and vice versa.
        Assert.True(store.ConsumeFireOnceForPolicy("p1", "latency"));
        Assert.True(store.ConsumeFireOnce("latency"));
    }

    [Fact]
    public void DifferentPolicies_IndependentTriggers()
    {
        var store = new ActivePolicyStore();
        store.SetFireOnceForPolicy("p1", "latency");

        Assert.False(store.ConsumeFireOnceForPolicy("p2", "latency"));
        Assert.True(store.ConsumeFireOnceForPolicy("p1", "latency"));
    }

    [Fact]
    public void DifferentTransforms_IndependentTriggers()
    {
        var store = new ActivePolicyStore();
        store.SetFireOnceForPolicy("p1", "latency");
        store.SetFireOnceForPolicy("p1", "error");

        Assert.True(store.ConsumeFireOnceForPolicy("p1", "latency"));
        Assert.True(store.ConsumeFireOnceForPolicy("p1", "error"));
    }

    [Fact]
    public void SetFireOnceForPolicy_NullPolicyId_Throws()
    {
        var store = new ActivePolicyStore();
        Assert.Throws<ArgumentNullException>(() => store.SetFireOnceForPolicy(null!, "latency"));
    }

    [Fact]
    public void SetFireOnceForPolicy_NullTransform_Throws()
    {
        var store = new ActivePolicyStore();
        Assert.Throws<ArgumentNullException>(() => store.SetFireOnceForPolicy("p1", null!));
    }
}

public class ActivePolicyStoreResetFireCountsTests
{
    [Fact]
    public void ResetFireCounts_NoCounters_NoOp()
    {
        var store = new ActivePolicyStore();
        store.ResetFireCounts("p1");
        Assert.Empty(store.GetFireCounts("p1"));
    }

    [Fact]
    public void ResetFireCounts_RemovesOnlyTargetPolicyCounters()
    {
        var store = new ActivePolicyStore();
        store.RecordFire("p1", "latency");
        store.RecordFire("p1", "error");
        store.RecordFire("p2", "latency");

        store.ResetFireCounts("p1");

        Assert.Empty(store.GetFireCounts("p1"));
        Assert.Equal(1, store.GetFireCounts("p2")["latency"]);
    }

    [Fact]
    public void ResetFireCounts_NullPolicyId_Throws()
    {
        var store = new ActivePolicyStore();
        Assert.Throws<ArgumentNullException>(() => store.ResetFireCounts(null!));
    }

    [Fact]
    public void ResetFireCounts_DoesNotAffectOtherChaosState()
    {
        var store = new ActivePolicyStore();
        store.RecordFire("p1", "latency");
        store.SetFireOnce("latency");
        store.SetFireOnceForPolicy("p1", "error");
        store.ConsumeFailFirstSlot("latency", "p1", "key", 1);

        store.ResetFireCounts("p1");

        // Fire-once triggers + failFirst counters untouched.
        Assert.True(store.ConsumeFireOnce("latency"));
        Assert.True(store.ConsumeFireOnceForPolicy("p1", "error"));
        Assert.False(store.ConsumeFailFirstSlot("latency", "p1", "key", 1)); // already consumed before reset
    }
}

/// <summary>
/// Regression tests for the re-arm gating reset (<see cref="ActivePolicyStore.Add"/> clearing a
/// policy id's failFirst budgets + rate-limit windows). Root cause of the run-to-green mesh
/// "fires 0×" repro: once the chaos proxy started surviving targeted resource rebuilds (instead
/// of being recreated per fix-loop iteration), a re-armed failFirst:N policy inherited the
/// outgoing arm's EXHAUSTED budget on the long-lived proxy, so the post-fix re-test never fired.
/// </summary>
public class ActivePolicyStoreReArmResetsGatingTests
{
    private static ActivePolicy NewPolicy(string id)
        => new(id, Matcher: null, null, null, null, null, null, null, null, null, null, null);

    [Fact]
    public void Add_ReArm_ResetsExhaustedFailFirstGate()
    {
        var store = new ActivePolicyStore();
        store.Add(NewPolicy("p1"));

        // Spend the single failFirst slot (fires once), then it's exhausted for this key.
        Assert.True(store.ConsumeFailFirstSlot("error", "p1", "anon:POST:/x", budget: 1));
        Assert.False(store.ConsumeFailFirstSlot("error", "p1", "anon:POST:/x", budget: 1));

        // Re-arming the SAME id must reset the gate so the next matching request fires again —
        // the "(re)install means start fresh" contract, applied to the gating state.
        store.Add(NewPolicy("p1"));

        Assert.True(store.ConsumeFailFirstSlot("error", "p1", "anon:POST:/x", budget: 1));
    }

    [Fact]
    public void Add_ReArm_ResetsFailFirstAcrossBucketsAndColonBearingRequestKeys()
    {
        var store = new ActivePolicyStore();
        store.Add(NewPolicy("p1"));

        // requestKey shapes that themselves contain ':' — the anon fallback + client-id forms —
        // must still be cleared (the reset matches the bounded :{policyId}: token, not a prefix).
        Assert.True(store.ConsumeFailFirstSlot("error", "p1", "anon:POST:/dbs/x/colls/y/docs", 1));
        Assert.False(store.ConsumeFailFirstSlot("error", "p1", "anon:POST:/dbs/x/colls/y/docs", 1));
        Assert.True(store.ConsumeFailFirstSlot("latency", "p1", "client:abc123", 1));
        Assert.False(store.ConsumeFailFirstSlot("latency", "p1", "client:abc123", 1));

        store.Add(NewPolicy("p1"));

        Assert.True(store.ConsumeFailFirstSlot("error", "p1", "anon:POST:/dbs/x/colls/y/docs", 1));
        Assert.True(store.ConsumeFailFirstSlot("latency", "p1", "client:abc123", 1));
    }

    [Fact]
    public void Add_ReArm_DoesNotResetOtherPoliciesGate()
    {
        var store = new ActivePolicyStore();
        store.Add(NewPolicy("p1"));
        store.Add(NewPolicy("p2"));

        Assert.True(store.ConsumeFailFirstSlot("error", "p2", "k", 1));
        Assert.False(store.ConsumeFailFirstSlot("error", "p2", "k", 1)); // p2 exhausted

        store.Add(NewPolicy("p1")); // re-arm p1 only

        // p1's re-arm must not touch p2's gate.
        Assert.False(store.ConsumeFailFirstSlot("error", "p2", "k", 1));
    }
}
