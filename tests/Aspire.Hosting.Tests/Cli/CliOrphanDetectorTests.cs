// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Threading.Channels;
using Aspire.Hosting.Cli;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "4")]
public class CliOrphanDetectorTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task CliOrphanDetectorCompletesWhenNoPidEnvironmentVariablePresent()
    {
        var configuration = new ConfigurationBuilder().Build();

        var lifetime = new HostLifetimeStub(() => { });
        var loggerFactory = CreateLoggerFactory(testOutputHelper);
        var detector = CreateCliOrphanDetector(loggerFactory, configuration, lifetime);

        // The detector should complete almost immediately because there is no
        // environment variable present that indicates that it is hitched to
        // Aspire lifetime.
        await detector.StartAsync(CancellationToken.None).DefaultTimeout();
    }

    [Fact]
    public async Task CliOrphanDetectorCallsStopIfEnvironmentVariablePresentAndProcessNotRunning()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "ASPIRE_CLI_PID", "1111" } })
            .Build();

        var stopSignalTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new HostLifetimeStub(() => stopSignalTcs.TrySetResult());

        var loggerFactory = CreateLoggerFactory(testOutputHelper);
        var detector = CreateCliOrphanDetector(loggerFactory, configuration, lifetime);
        detector.IsProcessRunning = _ => false;

        // The detector should complete almost immediately because there is no
        // environment variable present that indicates that it is hitched to
        // Aspire lifetime.
        await detector.StartAsync(CancellationToken.None).DefaultTimeout();
        await stopSignalTcs.Task.DefaultTimeout();
    }

    [Fact]
    public async Task CliOrphanDetectorUsesTimestampDetectionWhenStartTimeProvided()
    {
        var expectedStartTime = DateTime.Now.AddMinutes(-5);
        var expectedStartTimeUnixSeconds = ((DateTimeOffset)expectedStartTime).ToUnixTimeSeconds();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "ASPIRE_CLI_PID", "1111" },
                { "ASPIRE_CLI_STARTED", expectedStartTimeUnixSeconds.ToString() }
            })
            .Build();

        var stopSignalTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new HostLifetimeStub(() => stopSignalTcs.TrySetResult());

        var loggerFactory = CreateLoggerFactory(testOutputHelper);
        var detector = CreateCliOrphanDetector(loggerFactory, configuration, lifetime);
        detector.IsProcessRunningWithStartTime = (pid, startTime) => false;

        await detector.StartAsync(CancellationToken.None).DefaultTimeout();
        await stopSignalTcs.Task.DefaultTimeout();
    }

    [Fact]
    public async Task CliOrphanDetectorFallsBackToPidOnlyWhenStartTimeInvalid()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "ASPIRE_CLI_PID", "1111" },
                { "ASPIRE_CLI_STARTED", "invalid_start_time" }
            })
            .Build();

        var stopSignalTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new HostLifetimeStub(() => stopSignalTcs.TrySetResult());

        var loggerFactory = CreateLoggerFactory(testOutputHelper);
        var detector = CreateCliOrphanDetector(loggerFactory, configuration, lifetime);
        detector.IsProcessRunning = _ => false;

        await detector.StartAsync(CancellationToken.None).DefaultTimeout();
        await stopSignalTcs.Task.DefaultTimeout();
    }

    [Fact]
    public async Task CliOrphanDetectorContinuesRunningWhenProcessAliveWithCorrectStartTime()
    {
        var expectedStartTime = DateTime.Now.AddMinutes(-5);
        var expectedStartTimeUnix = ((DateTimeOffset)expectedStartTime).ToUnixTimeSeconds();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "ASPIRE_CLI_PID", "1111" },
                { "ASPIRE_CLI_STARTED", expectedStartTimeUnix.ToString() }
            })
            .Build();
        var fakeTimeProvider = new FakeTimeProvider(DateTimeOffset.Now);

        var stopSignalTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processRunningChannel = Channel.CreateUnbounded<int>();

        var lifetime = new HostLifetimeStub(() => stopSignalTcs.TrySetResult());
        var loggerFactory = CreateLoggerFactory(testOutputHelper);
        var detector = CreateCliOrphanDetector(loggerFactory, configuration, lifetime, fakeTimeProvider);

        var processRunningCallCounter = 0;
        detector.IsProcessRunningWithStartTime = (pid, startTime) =>
        {
            Assert.True(processRunningChannel.Writer.TryWrite(++processRunningCallCounter));
            return processRunningCallCounter < 3; // Process dies after 3 checks
        };

        await detector.StartAsync(CancellationToken.None).DefaultTimeout();

        // Verify process is checked first time
        Assert.Equal(1, await processRunningChannel.Reader.ReadAsync().DefaultTimeout());
        fakeTimeProvider.Advance(TimeSpan.FromSeconds(1));

        // Second check
        Assert.Equal(2, await processRunningChannel.Reader.ReadAsync().DefaultTimeout());
        fakeTimeProvider.Advance(TimeSpan.FromSeconds(1));

        // Third check (process dies)
        Assert.Equal(3, await processRunningChannel.Reader.ReadAsync().DefaultTimeout());

        // Should have exited.
        await detector.ExecuteTask!.DefaultTimeout();
        await stopSignalTcs.Task.DefaultTimeout();
    }

    [Fact]
    public async Task CliOrphanDetectorStopsWhenProcessHasDifferentStartTime()
    {
        var expectedStartTime = DateTime.Now.AddMinutes(-5);
        var expectedStartTimeUnixString = ((DateTimeOffset)expectedStartTime).ToUnixTimeSeconds().ToString();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "ASPIRE_CLI_PID", "1111" },
                { "ASPIRE_CLI_STARTED", expectedStartTimeUnixString }
            })
            .Build();

        var stopSignalTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new HostLifetimeStub(() => stopSignalTcs.TrySetResult());

        var loggerFactory = CreateLoggerFactory(testOutputHelper);
        var detector = CreateCliOrphanDetector(loggerFactory, configuration, lifetime);

        // Simulate process with different start time (PID reuse scenario)
        detector.IsProcessRunningWithStartTime = (pid, startTime) =>
        {
            // Process exists but has different start time - indicates PID reuse
            return false;
        };

        await detector.StartAsync(CancellationToken.None).DefaultTimeout();
        await stopSignalTcs.Task.DefaultTimeout();
    }

    [Fact]
    public async Task CliOrphanDetectorAfterTheProcessWasRunningForAWhileThenStops()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "ASPIRE_CLI_PID", "1111" } })
            .Build();
        var fakeTimeProvider = new FakeTimeProvider(DateTimeOffset.Now);

        var stopSignalTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processRunningChannel = Channel.CreateUnbounded<int>();

        var lifetime = new HostLifetimeStub(() => stopSignalTcs.TrySetResult());
        var loggerFactory = CreateLoggerFactory(testOutputHelper);
        var testLogger = loggerFactory.CreateLogger<CliOrphanDetectorTests>();
        var detector = CreateCliOrphanDetector(loggerFactory, configuration, lifetime, fakeTimeProvider);

        var processRunningCallCounter = 0;
        detector.IsProcessRunning = pid =>
        {
            Assert.True(processRunningChannel.Writer.TryWrite(++processRunningCallCounter));

            var isProcessRunning = processRunningCallCounter < 5;
            testLogger.LogDebug($"IsProcessRunning called. Running count: {processRunningCallCounter}. IsProcessRunning: {isProcessRunning}");
            return isProcessRunning;
        };

        // The detector should complete after about 5 seconds

        await detector.StartAsync(CancellationToken.None).DefaultTimeout();

        Assert.Equal(1, await processRunningChannel.Reader.ReadAsync().DefaultTimeout());
        fakeTimeProvider.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(2, await processRunningChannel.Reader.ReadAsync().DefaultTimeout());
        fakeTimeProvider.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(3, await processRunningChannel.Reader.ReadAsync().DefaultTimeout());
        fakeTimeProvider.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(4, await processRunningChannel.Reader.ReadAsync().DefaultTimeout());
        fakeTimeProvider.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(5, await processRunningChannel.Reader.ReadAsync().DefaultTimeout());

        // Should have exited.
        await detector.ExecuteTask!.DefaultTimeout();
        await stopSignalTcs.Task.DefaultTimeout();
    }

    [Fact]
    public async Task AppHostExitsWhenCliProcessPidDies()
    {
        // Start a long-running process that will stay alive until killed
        // These are system utilities on their respective platforms and don't require any additional dependencies.
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-t localhost") { CreateNoWindow = true }
            : new ProcessStartInfo("tail", "-f /dev/null");

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var fakeCliProcess = Process.Start(psi);
        Assert.NotNull(fakeCliProcess);

        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);
        builder.Configuration["ASPIRE_CLI_PID"] = fakeCliProcess.Id.ToString();

        var resourcesCreatedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Eventing.Subscribe<AfterResourcesCreatedEvent>((e, ct) =>
        {
            resourcesCreatedTcs.SetResult();
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var pendingRun = app.RunAsync();

        // Wait until the apphost is spun up and then kill off the stub
        // process so everything is torn down.
        await resourcesCreatedTcs.Task.DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        fakeCliProcess.Kill();

        await pendingRun.DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
    }

    private static CliOrphanDetector CreateCliOrphanDetector(
        ILoggerFactory loggerFactory,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        TimeProvider? timeProvider = null)
    {
        timeProvider ??= new FakeTimeProvider(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var logger = loggerFactory.CreateLogger<CliOrphanDetector>();
        return new CliOrphanDetector(configuration, lifetime, timeProvider, logger);
    }

    private static ILoggerFactory CreateLoggerFactory(ITestOutputHelper testOutputHelper)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddXunit(testOutputHelper);
        });
    }
}

file sealed class HostLifetimeStub(Action stopImplementation) : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => throw new NotImplementedException();

    public CancellationToken ApplicationStopped => throw new NotImplementedException();

    public CancellationToken ApplicationStopping => throw new NotImplementedException();

    public void StopApplication() => stopImplementation();
}
