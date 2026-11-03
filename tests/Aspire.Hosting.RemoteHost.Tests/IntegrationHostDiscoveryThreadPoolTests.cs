// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.Hosting.RemoteHost.Language;
using Aspire.Hosting.RemoteHost.Tests.TestServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

[CollectionDefinition(nameof(ThreadPoolSaturationCollection), DisableParallelization = true)]
public sealed class ThreadPoolSaturationCollection;

// Changes process-wide thread pool limits, so it must not run alongside other tests.
[Collection(nameof(ThreadPoolSaturationCollection))]
public class IntegrationHostDiscoveryThreadPoolTests
{
    [Fact]
    public async Task InitializeHostsAsync_CompletesWhileSynchronousTestsOccupyEverySchedulerSlot()
    {
        // Run on a dedicated thread so waiting here never consumes a pool worker.
        var (slotsOccupied, discovered) = await Task.Factory.StartNew(
            RunDiscoveryWhilePoolIsSaturated,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Assert.True(slotsOccupied && discovered,
            $"Capability discovery could not complete while {TestThreadPoolHeadroom.SynchronousTestSlots} synchronous tests " +
            $"occupied the thread pool (minimum workers: {GetMinWorkerThreads()}, slots occupied: {slotsOccupied}, discovered: {discovered}).");
    }

    private static (bool SlotsOccupied, bool Discovered) RunDiscoveryWhilePoolIsSaturated()
    {
        ThreadPool.GetMaxThreads(out var maxWorkerThreads, out var maxCompletionPortThreads);
        var minWorkerThreads = GetMinWorkerThreads();
        var slots = TestThreadPoolHeadroom.SynchronousTestSlots;

        // Windows CI showed the pool staying at its floor while CPU-bound synchronous tests kept
        // completing, so hill climbing never injected workers. Cap the pool at the floor to model
        // that deterministically instead of depending on host-specific injection timing.
        Assert.True(ThreadPool.SetMaxThreads(Math.Max(minWorkerThreads, Environment.ProcessorCount), maxCompletionPortThreads));
        var releaseSlots = new ManualResetEventSlim();
        var slotsOccupied = new CountdownEvent(slots);
        var occupiedSlots = new Task[slots];
        var discovery = Task.CompletedTask;
        try
        {
            for (var i = 0; i < slots; i++)
            {
                // Queued from a dedicated (non-pool) thread, so these land in the global pool queue.
                occupiedSlots[i] = Task.Factory.StartNew(() =>
                {
                    slotsOccupied.Signal();
                    releaseSlots.Wait(TimeSpan.FromSeconds(30));
                }, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            }
            // Without headroom some slots cannot start because test infrastructure already holds a
            // worker; any slots still queued then sit ahead of the discovery continuations.
            var occupied = slotsOccupied.Wait(TimeSpan.FromSeconds(5));

            discovery = RunDiscovery();
            return (occupied, discovery.Wait(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            releaseSlots.Set();
            ThreadPool.SetMaxThreads(maxWorkerThreads, maxCompletionPortThreads);
            // Only dispose once every slot has finished using the events.
            if (Task.WaitAll(occupiedSlots, TimeSpan.FromSeconds(30)))
            {
                releaseSlots.Dispose();
                slotsOccupied.Dispose();
            }
            // A timed-out wait does not stop discovery; drain it before other tests can run.
            discovery.GetAwaiter().GetResult();
        }
    }

    private static async Task RunDiscovery()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        await using var launcher = IntegrationHostTestServices.CreateLauncher(
            new LanguageSupportResolver(services, () => [], NullLogger<LanguageSupportResolver>.Instance),
            registry, new ConfigurationBuilder().Build(),
            new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance), NullLogger<IntegrationHostLauncher>.Instance);
        using var firstConnection = new IntegrationHostTestConnection(JsonSerializer.SerializeToElement(new[]
        {
            new { id = "test/first", method = "first", returnType = new { typeId = "void", category = "Primitive" } }
        }));
        using var secondConnection = new IntegrationHostTestConnection(JsonSerializer.SerializeToElement(new[]
        {
            new { id = "test/second", method = "second", returnType = new { typeId = "void", category = "Primitive" } }
        }));
        registry.AddIntegrationHost(firstConnection.ServerRpc);
        registry.AddIntegrationHost(secondConnection.ServerRpc);

        await launcher.InitializeHostsAsync(2, Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(registry.IsRegistered("test/first"));
        Assert.True(registry.IsRegistered("test/second"));
    }

    private static int GetMinWorkerThreads()
    {
        ThreadPool.GetMinThreads(out var workerThreads, out _);
        return workerThreads;
    }
}
