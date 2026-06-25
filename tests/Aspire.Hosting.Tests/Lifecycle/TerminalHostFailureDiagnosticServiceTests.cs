// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Lifecycle;

public class TerminalHostFailureDiagnosticServiceTests
{
    [Fact]
    public async Task FailedToStart_UnhidesHostAndWritesDiagnostic()
    {
        // The end-to-end scenario this protects: a user calls .WithTerminal() on a
        // resource, but the resolved terminal host binary fails to start (most commonly
        // because their bundled aspire CLI is older than this AppHost and doesn't ship
        // the 'terminalhost' subcommand). Before this service existed, the failed host
        // was hidden and the user only saw the parent resource hanging on
        // WaitUntilStarted with no explanation.
        var (notifications, loggers, host, service, stopCts) = CreateHarness(
            terminalHostPath: "/usr/local/share/aspire/cli/managed/aspire-managed",
            terminalHostInvocationArgs: "terminalhost");

        try
        {
            await service.StartAsync(stopCts.Token).DefaultTimeout();

            await notifications.PublishUpdateAsync(host, s => s with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, null),
                IsHidden = true,
            }).DefaultTimeout();

            await WaitForUnhideAsync(notifications, host).DefaultTimeout();

            var logs = await ReadLogsAsync(loggers, host, expected: 2).DefaultTimeout();

            Assert.Equal(2, logs.Count);
            Assert.True(logs[0].IsErrorMessage);
            Assert.Contains("Terminal host for 'target' (replica 0) failed to start", logs[0].Content);
            Assert.Contains("aspire-managed", logs[0].Content);
            Assert.True(logs[1].IsErrorMessage);
            Assert.Contains("aspire update --self", logs[1].Content);
        }
        finally
        {
            await stopCts.CancelAsync();
            await service.StopAsync(CancellationToken.None).DefaultTimeout();
        }
    }

    [Fact]
    public async Task ExitedWithNonZeroExitCode_UnhidesHostAndWritesDiagnostic()
    {
        // Not all terminal-host failures surface as "FailedToStart": a host that launches
        // successfully but then exits non-zero (e.g. the binary printed usage and exited
        // because it didn't recognise the 'terminalhost' subcommand) lands in "Exited"
        // with a non-zero ExitCode. Treat it the same way.
        var (notifications, loggers, host, service, stopCts) = CreateHarness(
            terminalHostPath: "/usr/local/share/aspire/cli/managed/aspire-managed",
            terminalHostInvocationArgs: "terminalhost");

        try
        {
            await service.StartAsync(stopCts.Token).DefaultTimeout();

            await notifications.PublishUpdateAsync(host, s => s with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.Exited, null),
                ExitCode = 1,
                IsHidden = true,
            }).DefaultTimeout();

            await WaitForUnhideAsync(notifications, host).DefaultTimeout();

            var logs = await ReadLogsAsync(loggers, host, expected: 2).DefaultTimeout();
            Assert.Contains("exit code 1", logs[0].Content);
        }
        finally
        {
            await stopCts.CancelAsync();
            await service.StopAsync(CancellationToken.None).DefaultTimeout();
        }
    }

    [Fact]
    public async Task ExitedWithZeroExitCode_DoesNotSurfaceAsFailure()
    {
        // Clean shutdown during AppHost stop: host exits with code 0. We must not
        // unhide it or write a diagnostic — otherwise every successful run would
        // light up the dashboard with phantom errors on shutdown.
        var (notifications, loggers, host, service, stopCts) = CreateHarness();

        try
        {
            await service.StartAsync(stopCts.Token).DefaultTimeout();

            await notifications.PublishUpdateAsync(host, s => s with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.Exited, null),
                ExitCode = 0,
                IsHidden = true,
            }).DefaultTimeout();

            // No way to deterministically prove "nothing happens" — give the service a
            // moment to observe the event, then assert the host is still hidden and no
            // logs were written.
            await Task.Delay(200, stopCts.Token);

            var current = await ReadCurrentSnapshotAsync(notifications, host).DefaultTimeout();
            Assert.True(current.IsHidden);

            var hasLogs = await TryReadAnyLogAsync(loggers, host);
            Assert.False(hasLogs);
        }
        finally
        {
            await stopCts.CancelAsync();
            await service.StopAsync(CancellationToken.None).DefaultTimeout();
        }
    }

    [Fact]
    public async Task NonBundleHostPath_DoesNotSuggestAspireUpdate()
    {
        // The "run `aspire update --self`" hint is only correct when the host we tried
        // to launch is the bundled aspire-managed binary (via the 'terminalhost'
        // dispatcher subcommand). For any other path — e.g. a side-loaded development
        // binary or a per-RID NuGet host — we still surface the failure but skip the
        // bundle-specific hint to avoid misleading the user.
        var (notifications, loggers, host, service, stopCts) = CreateHarness(
            terminalHostPath: "/opt/custom/terminalhost",
            terminalHostInvocationArgs: null);

        try
        {
            await service.StartAsync(stopCts.Token).DefaultTimeout();

            await notifications.PublishUpdateAsync(host, s => s with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, null),
                IsHidden = true,
            }).DefaultTimeout();

            await WaitForUnhideAsync(notifications, host).DefaultTimeout();

            // Read just the one line we know is unconditional. ReadLogsAsync with
            // expected: 1 will return as soon as that line arrives; the bundle hint
            // would be line 2, which we don't expect here.
            var logs = await ReadLogsAsync(loggers, host, expected: 1).DefaultTimeout();
            Assert.Single(logs);
            Assert.Contains("failed to start", logs[0].Content);
            Assert.DoesNotContain("aspire update --self", logs[0].Content);
        }
        finally
        {
            await stopCts.CancelAsync();
            await service.StopAsync(CancellationToken.None).DefaultTimeout();
        }
    }

    [Fact]
    public async Task RepeatedFailureEvents_OnlyDiagnoseOnce()
    {
        // DCP can re-emit FailedToStart events during recycle attempts and shutdown.
        // We must only write the diagnostic once per host instance to avoid spamming
        // the resource log.
        var (notifications, loggers, host, service, stopCts) = CreateHarness(
            terminalHostPath: "/usr/local/share/aspire/cli/managed/aspire-managed",
            terminalHostInvocationArgs: "terminalhost");

        try
        {
            await service.StartAsync(stopCts.Token).DefaultTimeout();

            // Publish the first failure event that triggers the diagnostic.
            await notifications.PublishUpdateAsync(host, s => s with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, null),
                IsHidden = true,
            }).DefaultTimeout();

            // Wait for the service to process the first event and unhide the host.
            // This ensures the service has added the host to its diagnosed set before
            // we publish repeated events that should be de-duped.
            await WaitForUnhideAsync(notifications, host).DefaultTimeout();
            await ReadLogsAsync(loggers, host, expected: 2).DefaultTimeout();

            // Now publish repeated failures — these must be de-duped.
            for (var i = 0; i < 2; i++)
            {
                await notifications.PublishUpdateAsync(host, s => s with
                {
                    State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, null),
                    IsHidden = true,
                }).DefaultTimeout();
            }

            // Give the service a tick to spuriously write more lines if the dedupe
            // logic is broken.
            await Task.Delay(150, stopCts.Token);

            // Open a fresh subscriber and replay: the buffer should still contain
            // exactly 2 lines (a third would prove the dedupe failed).
            var replay = await ReadLogsAsync(loggers, host, expected: 3, timeoutMs: 300);
            Assert.Equal(2, replay.Count);
        }
        finally
        {
            await stopCts.CancelAsync();
            await service.StopAsync(CancellationToken.None).DefaultTimeout();
        }
    }

    private static (ResourceNotificationService Notifications, ResourceLoggerService Loggers, TerminalHostResource Host, TerminalHostFailureDiagnosticService Service, CancellationTokenSource StopCts) CreateHarness(
        string? terminalHostPath = null,
        string? terminalHostInvocationArgs = null)
    {
        var loggers = new ResourceLoggerService();
        var notifications = ResourceNotificationServiceTestHelpers.Create(resourceLoggerService: loggers);
        var parent = new TestResource("target");
        var layout = new TerminalHostLayout(
            replicaId: "abcdefghijk",
            parentReplicaIndex: 0,
            producerUdsPath: "/tmp/abcdefghijk.dcp.sock",
            consumerUdsPath: "/tmp/abcdefghijk.host.sock",
            controlUdsPath: "/tmp/abcdefghijk.ctrl.sock",
            metadataPath: "/tmp/abcdefghijk.meta.json");
        var host = new TerminalHostResource("target-terminalhost-0", parent, layout);
        var dcpOptions = Options.Create(new DcpOptions
        {
            TerminalHostPath = terminalHostPath,
            TerminalHostInvocationArgs = terminalHostInvocationArgs,
        });
        var service = new TerminalHostFailureDiagnosticService(
            notifications,
            loggers,
            dcpOptions,
            NullLogger<TerminalHostFailureDiagnosticService>.Instance);
        var stopCts = new CancellationTokenSource();
        return (notifications, loggers, host, service, stopCts);
    }

    private static async Task WaitForUnhideAsync(ResourceNotificationService notifications, IResource host)
    {
        await foreach (var evt in notifications.WatchAsync(CancellationToken.None))
        {
            if (evt.Resource == host && !evt.Snapshot.IsHidden)
            {
                return;
            }
        }
    }

    private static async Task<CustomResourceSnapshot> ReadCurrentSnapshotAsync(ResourceNotificationService notifications, IResource host)
    {
        await foreach (var evt in notifications.WatchAsync(CancellationToken.None))
        {
            if (evt.Resource == host)
            {
                return evt.Snapshot;
            }
        }
        throw new InvalidOperationException("No snapshot observed.");
    }

    private static async Task<IReadOnlyList<LogLine>> ReadLogsAsync(ResourceLoggerService loggers, IResource host, int expected, int? timeoutMs = null)
    {
        var collected = new List<LogLine>();
        using var cts = timeoutMs is { } ms ? new CancellationTokenSource(ms) : null;
        try
        {
            await foreach (var batch in loggers.WatchAsync(host).WithCancellation(cts?.Token ?? CancellationToken.None))
            {
                foreach (var line in batch)
                {
                    collected.Add(line);
                    if (collected.Count >= expected)
                    {
                        return collected;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        return collected;
    }

    private static async Task<bool> TryReadAnyLogAsync(ResourceLoggerService loggers, IResource host)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try
        {
            await foreach (var batch in loggers.WatchAsync(host).WithCancellation(timeoutCts.Token))
            {
                if (batch.Count > 0)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        return false;
    }

    private sealed class TestResource(string name) : Resource(name)
    {
    }
}
