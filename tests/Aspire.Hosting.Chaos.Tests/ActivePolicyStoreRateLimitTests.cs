// <copyright file="ActivePolicyStoreRateLimitTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ActivePolicyStoreRateLimitTests
{
    [Fact]
    public void TryAdmitRateLimitedRequest_WithinBudget_AdmitsAll()
    {
        var store = new ActivePolicyStore();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(store.TryAdmitRateLimitedRequest("rl", "p1", "req-key", requestsPerWindow: 5, windowMs: 10_000));
        }
    }

    [Fact]
    public void TryAdmitRateLimitedRequest_ExceedsBudget_BlocksExtras()
    {
        var store = new ActivePolicyStore();
        store.TryAdmitRateLimitedRequest("rl", "p1", "key", 2, 10_000);
        store.TryAdmitRateLimitedRequest("rl", "p1", "key", 2, 10_000);

        Assert.False(store.TryAdmitRateLimitedRequest("rl", "p1", "key", 2, 10_000));
    }

    [Fact]
    public void TryAdmitRateLimitedRequest_DifferentPoliciesIndependent()
    {
        var store = new ActivePolicyStore();
        store.TryAdmitRateLimitedRequest("rl", "p1", "key", 1, 10_000);

        Assert.True(store.TryAdmitRateLimitedRequest("rl", "p2", "key", 1, 10_000));
    }

    [Fact]
    public void TryAdmitRateLimitedRequest_DifferentRequestKeysIndependent()
    {
        var store = new ActivePolicyStore();
        store.TryAdmitRateLimitedRequest("rl", "p1", "req-a", 1, 10_000);

        Assert.True(store.TryAdmitRateLimitedRequest("rl", "p1", "req-b", 1, 10_000));
    }

    [Fact]
    public async Task TryAdmitRateLimitedRequest_AfterWindowSlides_AdmitsAgain()
    {
        var store = new ActivePolicyStore();
        // Tight 50ms window; exceed it, then wait, then assert admission resumes.
        Assert.True(store.TryAdmitRateLimitedRequest("rl", "p1", "key", 1, 50));
        Assert.False(store.TryAdmitRateLimitedRequest("rl", "p1", "key", 1, 50));

        await Task.Delay(120);

        Assert.True(store.TryAdmitRateLimitedRequest("rl", "p1", "key", 1, 50));
    }
}
