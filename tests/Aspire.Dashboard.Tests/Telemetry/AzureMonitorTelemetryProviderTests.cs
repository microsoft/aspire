// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using Aspire.Shared.Telemetry;
using Aspire.Tests.Shared.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class AzureMonitorTelemetryProviderTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Lifecycle_CombinesSignalResultsAndDisposesBothProviders(bool traceResult, bool logResult)
    {
        var calls = new ConcurrentQueue<(string Signal, string Operation, int Timeout)>();
        var traceProcessor = new TestTelemetryProcessor<Activity>
        {
            ForceFlushCallback = timeout =>
            {
                calls.Enqueue(("trace", "flush", timeout));
                return traceResult;
            },
            ShutdownCallback = timeout =>
            {
                calls.Enqueue(("trace", "shutdown", timeout));
                return traceResult;
            }
        };
        var logProcessor = new TestTelemetryProcessor<LogRecord>
        {
            ForceFlushCallback = timeout =>
            {
                calls.Enqueue(("log", "flush", timeout));
                return logResult;
            },
            ShutdownCallback = timeout =>
            {
                calls.Enqueue(("log", "shutdown", timeout));
                return logResult;
            }
        };
        using var source = new ActivitySource($"Test.Product.Provider.{Guid.NewGuid():N}");
        using var provider = AzureMonitorTelemetryProvider.Create(new ServiceCollection(), ResourceBuilder.CreateEmpty(), "Test.Product.Events",
            () => Sdk.CreateTracerProviderBuilder().AddSource(source.Name).AddProcessor(traceProcessor).Build(),
            logs => logs.AddProcessor(logProcessor));
        Assert.True(source.HasListeners());
        Assert.Equal(traceResult && logResult, await provider.ForceFlushAsync(2345));
        var shutdown = provider.ShutdownAsync(3456);
        Assert.Same(shutdown, provider.ShutdownAsync(4567));
        Assert.Equal(traceResult && logResult, await shutdown);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ForceFlushAsync(2345));

        provider.Dispose();
        provider.Dispose();
        Assert.Equal(1, traceProcessor.DisposeCount);
        Assert.Equal(1, logProcessor.DisposeCount);
        Assert.False(source.HasListeners());
        Assert.Equal(
            [("log", "flush", 2345), ("log", "shutdown", 3456), ("trace", "flush", 2345), ("trace", "shutdown", 3456)],
            calls.OrderBy(call => call.Signal).ThenBy(call => call.Operation).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_DrainsBothSignalsConcurrently(bool shutdown)
    {
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        bool Drain(int timeout)
        {
            entered.Signal();
            return release.Wait(timeout);
        }

        var traceProcessor = new TestTelemetryProcessor<Activity>();
        var logProcessor = new TestTelemetryProcessor<LogRecord>();
        if (shutdown)
        {
            traceProcessor.ShutdownCallback = Drain;
            logProcessor.ShutdownCallback = Drain;
        }
        else
        {
            traceProcessor.ForceFlushCallback = Drain;
            logProcessor.ForceFlushCallback = Drain;
        }
        using var provider = AzureMonitorTelemetryProvider.Create(new ServiceCollection(), ResourceBuilder.CreateEmpty(), "Test.Product.Events",
            () => Sdk.CreateTracerProviderBuilder().AddProcessor(traceProcessor).Build(),
            logs => logs.AddProcessor(logProcessor));
        var drain = shutdown ? provider.ShutdownAsync(10_000) : provider.ForceFlushAsync(10_000);
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
        }
        finally
        {
            release.Set();
        }
        Assert.True(await drain);
    }

    [Fact]
    public async Task Shutdown_WaitsForPendingFlushesBeforeDraining()
    {
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var completedFlushes = 0;
        var shutdownObservations = new ConcurrentQueue<int>();
        bool Flush(int timeout)
        {
            entered.Signal();
            var result = release.Wait(timeout);
            Interlocked.Increment(ref completedFlushes);
            return result;
        }
        bool Shutdown(int timeout)
        {
            shutdownObservations.Enqueue(Volatile.Read(ref completedFlushes));
            return true;
        }
        var traceProcessor = new TestTelemetryProcessor<Activity> { ForceFlushCallback = Flush, ShutdownCallback = Shutdown };
        var logProcessor = new TestTelemetryProcessor<LogRecord> { ForceFlushCallback = Flush, ShutdownCallback = Shutdown };
        using var provider = AzureMonitorTelemetryProvider.Create(new ServiceCollection(), ResourceBuilder.CreateEmpty(), "Test.Product.Events",
            () => Sdk.CreateTracerProviderBuilder().AddProcessor(traceProcessor).Build(),
            logs => logs.AddProcessor(logProcessor));
        var flush = provider.ForceFlushAsync(10_000);
        Task<bool>? shutdown = null;
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            shutdown = provider.ShutdownAsync(10_000);
            Assert.False(shutdown.IsCompleted);
            Assert.Empty(shutdownObservations);
        }
        finally
        {
            release.Set();
        }
        Assert.True(await flush);
        Assert.NotNull(shutdown);
        Assert.True(await shutdown);
        Assert.Equal([2, 2], shutdownObservations.ToArray());
    }
}
