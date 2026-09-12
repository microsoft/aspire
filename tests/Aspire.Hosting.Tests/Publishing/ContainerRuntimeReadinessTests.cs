// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001

using Aspire.Hosting.Publishing;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Tests.Publishing;

public class ContainerRuntimeReadinessTests
{
    [Fact]
    public async Task HealthyRuntimeDoesNotPrompt()
    {
        var interactions = new TestInteractionService();
        using var services = CreateServices(interactions, TimeProvider.System);
        var runtime = new FakeContainerRuntime(name: "Docker");

        await services.GetRequiredService<ContainerRuntimeReadiness>()
            .EnsureRunningAsync(runtime, TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(1, runtime.CheckIfRunningCallCount);
        Assert.False(interactions.Interactions.Reader.TryRead(out _));
    }

    [Fact]
    public async Task UnavailableInteractionFailsWithActionableError()
    {
        var interactions = new TestInteractionService { IsAvailable = false };
        using var services = CreateServices(interactions, TimeProvider.System);
        var runtime = new FakeContainerRuntime(isRunning: false, name: "Docker");

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            services.GetRequiredService<ContainerRuntimeReadiness>()
                .EnsureRunningAsync(runtime, TestContext.Current.CancellationToken)).DefaultTimeout();

        Assert.Equal("Docker is not running. Start Docker and try again.", exception.Message);
        Assert.Equal(1, runtime.CheckIfRunningCallCount);
        Assert.False(interactions.Interactions.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ConcurrentChecksShareRecoveryPrompt()
    {
        var interactions = new TestInteractionService();
        using var services = CreateServices(interactions, TimeProvider.System);
        var running = 0;
        var runtime = new FakeContainerRuntime(name: "Docker")
        {
            CheckIfRunningAsyncCallback = _ => Task.FromResult(Volatile.Read(ref running) != 0)
        };
        var readiness = services.GetRequiredService<ContainerRuntimeReadiness>();
        var first = readiness.EnsureRunningAsync(runtime, TestContext.Current.CancellationToken);
        var interaction = await interactions.Interactions.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().DefaultTimeout();
        var second = readiness.EnsureRunningAsync(runtime, TestContext.Current.CancellationToken);

        Assert.Equal(1, runtime.CheckIfRunningCallCount);
        Assert.Equal("Docker is not running", interaction.Title);
        Assert.Equal("Start Docker and confirm to continue.", interaction.Message);
        Assert.Equal(MessageIntent.Warning, Assert.IsType<NotificationInteractionOptions>(interaction.Options).Intent);
        Assert.False(interactions.Interactions.Reader.TryRead(out _));

        Interlocked.Exchange(ref running, 1);
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(true));
        await Task.WhenAll(first, second).DefaultTimeout();

        Assert.Equal(2, runtime.CheckIfRunningCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclinedRecoveryDoesNotCacheFailure(bool canceled)
    {
        var interactions = new TestInteractionService();
        using var services = CreateServices(interactions, TimeProvider.System);
        var running = 0;
        var runtime = new FakeContainerRuntime(name: "Docker")
        {
            CheckIfRunningAsyncCallback = _ => Task.FromResult(Volatile.Read(ref running) != 0)
        };
        var readiness = services.GetRequiredService<ContainerRuntimeReadiness>();
        var check = readiness.EnsureRunningAsync(runtime, TestContext.Current.CancellationToken);
        var interaction = await interactions.Interactions.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().DefaultTimeout();

        interaction.CompletionTcs.SetResult(canceled ? InteractionResult.Cancel<bool>() : InteractionResult.Ok(false));
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => check).DefaultTimeout();

        Assert.Equal("Docker is not running. Start Docker and try again.", exception.Message);
        Interlocked.Exchange(ref running, 1);
        await readiness.EnsureRunningAsync(runtime, TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.Equal(2, runtime.CheckIfRunningCallCount);
        Assert.False(interactions.Interactions.Reader.TryRead(out _));
    }

    [Fact]
    public async Task CompletedSuccessDoesNotHideStoppedRuntime()
    {
        var interactions = new TestInteractionService { IsAvailable = false };
        using var services = CreateServices(interactions, TimeProvider.System);
        var running = 1;
        var runtime = new FakeContainerRuntime(name: "Docker")
        {
            CheckIfRunningAsyncCallback = _ => Task.FromResult(Volatile.Read(ref running) != 0)
        };
        var readiness = services.GetRequiredService<ContainerRuntimeReadiness>();

        await readiness.EnsureRunningAsync(runtime, TestContext.Current.CancellationToken).DefaultTimeout();
        Interlocked.Exchange(ref running, 0);
        await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            readiness.EnsureRunningAsync(runtime, TestContext.Current.CancellationToken)).DefaultTimeout();

        Assert.Equal(2, runtime.CheckIfRunningCallCount);
    }

    [Fact]
    public async Task CancelingAdditionalWaiterDoesNotCancelSharedRecovery()
    {
        var interactions = new TestInteractionService();
        using var services = CreateServices(interactions, TimeProvider.System);
        var running = 0;
        var runtime = new FakeContainerRuntime(name: "Docker")
        {
            CheckIfRunningAsyncCallback = _ => Task.FromResult(Volatile.Read(ref running) != 0)
        };
        var readiness = services.GetRequiredService<ContainerRuntimeReadiness>();
        var first = readiness.EnsureRunningAsync(runtime, TestContext.Current.CancellationToken);
        var interaction = await interactions.Interactions.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().DefaultTimeout();
        using var cancellation = new CancellationTokenSource();
        var second = readiness.EnsureRunningAsync(runtime, cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second).DefaultTimeout();
        Assert.False(first.IsCompleted);
        Interlocked.Exchange(ref running, 1);
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(true));
        await first.DefaultTimeout();

        Assert.Equal(2, runtime.CheckIfRunningCallCount);
    }

    [Fact]
    public async Task RecoveryTimeoutReportsUnavailableRuntime()
    {
        var interactions = new TestInteractionService();
        var timeProvider = new FakeTimeProvider();
        using var services = CreateServices(interactions, timeProvider);
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeContainerRuntime(name: "Docker");
        runtime.CheckIfRunningAsyncCallback = _ =>
        {
            if (runtime.CheckIfRunningCallCount > 1)
            {
                polling.TrySetResult();
            }

            return Task.FromResult(false);
        };
        var check = services.GetRequiredService<ContainerRuntimeReadiness>()
            .EnsureRunningAsync(runtime, TestContext.Current.CancellationToken);
        var interaction = await interactions.Interactions.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().DefaultTimeout();

        interaction.CompletionTcs.SetResult(InteractionResult.Ok(true));
        await polling.Task.DefaultTimeout();
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => check).DefaultTimeout();

        Assert.Equal("Docker is not running. Start Docker and try again.", exception.Message);
    }

    private static ServiceProvider CreateServices(TestInteractionService interactions, TimeProvider timeProvider)
    {
        return new ServiceCollection()
            .AddSingleton<IInteractionService>(interactions)
            .AddSingleton(timeProvider)
            .AddSingleton<ContainerRuntimeReadiness>()
            .BuildServiceProvider();
    }
}
