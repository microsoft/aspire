// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Client;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeProviderActivationTests
{
    [Fact]
    public async Task StalledAuthorityAbandonmentUsesAnIndependentConfigurableDeadline()
    {
        await using var host = new NativeActivationRpcTestHost();
        await using var client = await NativeClient.ConnectAsync(host.Endpoint, "test-token",
            TimeSpan.FromSeconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        var clock = new AdjustableFakeTimeProvider();
        var options = new NativeProviderActivationOptions { CleanupTimeout = TimeSpan.FromSeconds(2) };
        var directory = Directory.CreateTempSubdirectory("aspire-native-stalled-abandonment-");
        try
        {
            var activation = NativeProviderActivation.StartAsync(client, "example.failure",
                Path.Combine(directory.FullName, "missing-provider"), [], options, clock,
                NullLogger.Instance, TestContext.Current.CancellationToken);
            await host.AbandonmentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMilliseconds(1999));
            Assert.False(activation.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            var failure = await Assert.ThrowsAsync<AggregateException>(() =>
                activation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Collection(failure.InnerExceptions,
                error => Assert.IsType<System.ComponentModel.Win32Exception>(error),
                error => Assert.IsAssignableFrom<OperationCanceledException>(error));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedSpawnFailuresDoNotConsumePendingProviderCapacity()
    {
        await using var host = await NativeClientTestHost.StartAsync();
        await using var client = await NativeClient.ConnectAsync(host.Endpoint, host.Token,
            TimeSpan.FromSeconds(15), TimeProvider.System, TestContext.Current.CancellationToken);
        var directory = Directory.CreateTempSubdirectory("aspire-native-provider-failure-");
        try
        {
            var missing = Path.Combine(directory.FullName, "missing-provider");
            for (var index = 0; index < 80; index++)
            {
                await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
                    NativeProviderActivation.StartAsync(client, "example.failure", missing, [],
                        new NativeProviderActivationOptions(), TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken));
            }
            var authority = await client.ExpectIntegrationHostAsync("example.next", TestContext.Current.CancellationToken);
            Assert.True(await client.AbandonIntegrationHostRegistrationAsync(authority, TestContext.Current.CancellationToken));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExitedProviderDisposesProcessAfterDrainingOutput()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("This process fixture uses a Unix shell.");
        }
        await using var host = await NativeProviderProcessTestHost.StartAsync(retainOutput: false);
        var handle = host.Process.SafeHandle;
        var activation = new NativeProviderActivation(host.Process, new NativeProviderActivationOptions(), TimeProvider.System);
        await activation.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(handle.IsClosed);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(15)]
    public async Task DescendantHeldOutputCannotOutliveProviderShutdownBudget(int timeoutSeconds)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("This process fixture uses a Unix shell.");
        }
        await using var host = await NativeProviderProcessTestHost.StartAsync(retainOutput: true);
        var handle = host.Process.SafeHandle;
        var timerCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new AdjustableFakeTimeProvider
        {
            TimerCreated = dueTime =>
            {
                if (dueTime == TimeSpan.FromSeconds(timeoutSeconds))
                {
                    timerCreated.TrySetResult();
                }
            }
        };
        var activation = new NativeProviderActivation(host.Process,
            new NativeProviderActivationOptions { ShutdownTimeout = TimeSpan.FromSeconds(timeoutSeconds) }, clock);
        var disposal = activation.DisposeAsync().AsTask();
        await timerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMilliseconds(timeoutSeconds * 1000 - 1));
        Assert.False(disposal.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            disposal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(handle.IsClosed);
        Assert.False(host.Descendant!.HasExited);
    }
}
