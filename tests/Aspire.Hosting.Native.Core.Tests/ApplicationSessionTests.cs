// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Sessions;

namespace Aspire.Hosting.Native.Core.Tests;

public class ApplicationSessionTests
{
    [Fact]
    public void RejectedGenerationDoesNotDisturbItsOwner()
    {
        using var session = new ApplicationSession();
        var model = session.StartGeneration();
        var cache = model.AddResource("cache", "example.redis/Redis");

        Assert.Throws<InvalidOperationException>(session.StartGeneration);
        Assert.Equal(cache, Assert.Single(model.Inspect().Resources).Handle);
    }

    [Fact]
    public void GuestReplacementRetiresPreviousDeclarations()
    {
        using var session = new ApplicationSession();
        var previous = session.StartGeneration();
        var oldCache = previous.AddResource("cache", "example.redis/Redis");
        session.RetireGeneration(previous.GenerationId);
        var next = session.StartGeneration();
        var newCache = next.AddResource("cache", "example.redis/Redis");

        Assert.NotEqual(previous.GenerationId, next.GenerationId);
        Assert.NotEqual(oldCache, newCache);
        Assert.Throws<ObjectDisposedException>(() => previous.ReadResource(oldCache));
        Assert.Throws<ArgumentException>(() => next.ReadResource(oldCache));
        Assert.Equal(newCache, Assert.Single(next.Inspect().Resources).Handle);
    }

    [Fact]
    public void LateRetirementCannotDisposeReplacement()
    {
        using var session = new ApplicationSession();
        var previous = session.StartGeneration();
        session.RetireGeneration(previous.GenerationId);
        var next = session.StartGeneration();
        var cache = next.AddResource("cache", "example.redis/Redis");

        Assert.Throws<InvalidOperationException>(() => session.RetireGeneration(previous.GenerationId));
        Assert.Throws<InvalidOperationException>(() => session.RetireGeneration(Guid.NewGuid()));
        Assert.Equal(cache, Assert.Single(next.Inspect().Resources).Handle);
        session.RetireGeneration(next.GenerationId);
    }

    [Fact]
    public void SessionDisposalRevokesCurrentGeneration()
    {
        var session = new ApplicationSession();
        var model = session.StartGeneration();

        session.Dispose();
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(model.Inspect);
        Assert.Throws<ObjectDisposedException>(session.StartGeneration);
        Assert.Throws<ObjectDisposedException>(() => session.RetireGeneration(model.GenerationId));
    }

    [Fact]
    public async Task ConcurrentGuestsCannotAcquireTwoGenerations()
    {
        using var session = new ApplicationSession();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            Record.Exception(() => session.StartGeneration()))));

        Assert.Single(outcomes, outcome => outcome is null);
        Assert.All(outcomes.Where(outcome => outcome is not null), outcome => Assert.IsType<InvalidOperationException>(outcome));
    }
}
