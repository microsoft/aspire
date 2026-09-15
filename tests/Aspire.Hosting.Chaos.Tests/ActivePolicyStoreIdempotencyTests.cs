// <copyright file="ActivePolicyStoreIdempotencyTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ActivePolicyStoreIdempotencyTests
{
    [Fact]
    public void TryRecordIdempotencyKey_FirstSight_ReturnsTrue()
    {
        var store = new ActivePolicyStore();

        Assert.True(store.TryRecordIdempotencyKey("p1", "key-a", windowMs: 60_000));
    }

    [Fact]
    public void TryRecordIdempotencyKey_SecondSightSameKey_ReturnsFalse()
    {
        var store = new ActivePolicyStore();
        store.TryRecordIdempotencyKey("p1", "key-a", 60_000);

        Assert.False(store.TryRecordIdempotencyKey("p1", "key-a", 60_000));
    }

    [Fact]
    public void TryRecordIdempotencyKey_DifferentPoliciesAreIndependent()
    {
        var store = new ActivePolicyStore();
        store.TryRecordIdempotencyKey("p1", "key-a", 60_000);

        Assert.True(store.TryRecordIdempotencyKey("p2", "key-a", 60_000));
    }

    [Fact]
    public void TryRecordIdempotencyKey_DifferentKeysAreIndependent()
    {
        var store = new ActivePolicyStore();
        store.TryRecordIdempotencyKey("p1", "key-a", 60_000);

        Assert.True(store.TryRecordIdempotencyKey("p1", "key-b", 60_000));
    }

    [Fact]
    public async Task TryRecordIdempotencyKey_AfterWindowExpires_ReturnsTrueAgain()
    {
        var store = new ActivePolicyStore();
        store.TryRecordIdempotencyKey("p1", "key-a", windowMs: 50);

        await Task.Delay(120);

        Assert.True(store.TryRecordIdempotencyKey("p1", "key-a", 50));
    }
}
