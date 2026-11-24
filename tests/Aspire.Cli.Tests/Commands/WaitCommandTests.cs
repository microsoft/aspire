// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Commands;

public class WaitCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task WaitCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_RequiresResourceArgument()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task WaitCommand_ResourceNamesWithAll_ReturnsInvalidCommand()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait api --all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task WaitCommand_AcceptsResourceArgument()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait myresource --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_AcceptsProjectOption()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait myresource --apphost /path/to/project.csproj --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_AcceptsStatusOption()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait myresource --status up --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_AcceptsTimeoutOption()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait myresource --timeout 60 --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("up")]
    [InlineData("down")]
    public async Task WaitCommand_AcceptsAllStatusValues(string status)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"wait myresource --status {status} --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Theory]
    [InlineData("Healthy")]
    [InlineData("UP")]
    [InlineData("Down")]
    public async Task WaitCommand_StatusIsCaseInsensitive(string status)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"wait myresource --status {status} --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_ResourceNotFound_ReturnsFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceResult = new WaitForResourceResponse
            {
                Success = false,
                ResourceNotFound = true,
                ErrorMessage = "Resource 'nonexistent' was not found."
            }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("/tmp/test.sock", backchannel);
        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait nonexistent --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.WaitResourceFailed, exitCode);
        Assert.Equal(
            [
                SharedCommandStrings.ScanningForRunningAppHosts,
                string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitingForResource, "nonexistent", "healthy")
            ],
            interactionService.ShownStatuses);
        Assert.Equal(
            [string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceNotFound, "nonexistent")],
            interactionService.DisplayedErrors);
        Assert.Equal([""], interactionService.DisplayedPlainText);
        Assert.Empty(interactionService.DisplayedSuccess);
    }

    [Fact]
    public async Task WaitCommand_ResourceRunning_WaitForUp_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var timeProvider = new FakeTimeProvider();
        var backchannel = new TestAppHostAuxiliaryBackchannel();
        backchannel.WaitForResourceHandler = (_, _, _, _) =>
        {
            timeProvider.Advance(TimeSpan.FromSeconds(2.5));
            return Task.FromResult(new WaitForResourceResponse { Success = true, State = "Running" });
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("/tmp/test.sock", backchannel);
        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.InteractionServiceFactory = _ => interactionService;
            options.TimeProvider = timeProvider;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait myapp --status up --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(
            [
                SharedCommandStrings.ScanningForRunningAppHosts,
                string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitingForResource, "myapp", "up (running)")
            ],
            interactionService.ShownStatuses);
        Assert.Empty(interactionService.DisplayedErrors);
        Assert.Equal([""], interactionService.DisplayedPlainText);
        Assert.Equal(
            [string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceReachedTargetStatus, "myapp", "up (running)", 2.5)],
            interactionService.DisplayedSuccess);
    }

    [Fact]
    public async Task WaitCommand_ResourceHealthy_WaitForHealthy_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceResult = new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait mydb --status healthy --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_Timeout_ReturnsTimeoutExitCode()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceResult = new WaitForResourceResponse
            {
                Success = false,
                TimedOut = true,
                ErrorMessage = "Timed out waiting for resource 'mydb'."
            }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("/tmp/test.sock", backchannel);
        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait mydb --status healthy --timeout 2");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.WaitTimeout, exitCode);
        Assert.Equal(
            [
                SharedCommandStrings.ScanningForRunningAppHosts,
                string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitingForResource, "mydb", "healthy")
            ],
            interactionService.ShownStatuses);
        Assert.Equal(
            [string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitTimedOut, "mydb", "healthy", 2)],
            interactionService.DisplayedErrors);
        Assert.Equal([""], interactionService.DisplayedPlainText);
        Assert.Empty(interactionService.DisplayedSuccess);
    }

    [Fact]
    public async Task WaitCommand_ResourceExited_WaitForDown_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceResult = new WaitForResourceResponse { Success = true, State = "Exited" }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait worker --status down --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task WaitCommand_ResourceFailedToStart_WaitForUp_ReturnsFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceResult = new WaitForResourceResponse
            {
                Success = false,
                State = "FailedToStart",
                ErrorMessage = "Resource 'myapp' failed to start."
            }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("/tmp/test.sock", backchannel);
        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait myapp --status up --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.WaitResourceFailed, exitCode);
        Assert.Equal(
            [
                SharedCommandStrings.ScanningForRunningAppHosts,
                string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitingForResource, "myapp", "up (running)")
            ],
            interactionService.ShownStatuses);
        Assert.Equal(
            [string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceEnteredFailedState, "myapp", "FailedToStart")],
            interactionService.DisplayedErrors);
        Assert.Equal([""], interactionService.DisplayedPlainText);
        Assert.Empty(interactionService.DisplayedSuccess);
    }

    [Fact]
    public async Task WaitCommand_ResourceFailedToStart_WaitForDown_ReturnsFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceResult = new WaitForResourceResponse
            {
                Success = true,
                State = "FailedToStart"
            }
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("/tmp/test.sock", backchannel);
        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.InteractionServiceFactory = _ => interactionService;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("wait myapp --status down --timeout 5");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(CliExitCodes.WaitResourceFailed, exitCode);
        Assert.Equal(
            [
                SharedCommandStrings.ScanningForRunningAppHosts,
                string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitingForResource, "myapp", "down")
            ],
            interactionService.ShownStatuses);
        Assert.Equal(
            [string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceEnteredFailedState, "myapp", "FailedToStart")],
            interactionService.DisplayedErrors);
        Assert.Equal([""], interactionService.DisplayedPlainText);
        Assert.Empty(interactionService.DisplayedSuccess);
    }

    [Fact]
    public async Task WaitCommand_SingleResourceExitedWithZero_ReturnsSuccess()
    {
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots = [new ResourceSnapshot { Name = "migrations", State = "Finished", ExitCode = 0 }],
            WaitForResourceResult = new WaitForResourceResponse
            {
                Success = false,
                ErrorMessage = "Stopped waiting for resource 'migrations' to become healthy because it failed to start."
            }
        };
        var interactionService = new TestInteractionService();

        var exitCode = await RunWaitAsync("wait migrations --timeout 5", backchannel, interactionService);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Empty(interactionService.DisplayedErrors);
        Assert.Equal(
            [string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceCompleted, "migrations", 0.0)],
            interactionService.DisplayedSuccess);
    }

    [Fact]
    public async Task WaitCommand_MultipleResources_ReportsEachResourceWithEndpoints()
    {
        var requests = new ConcurrentQueue<string>();
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = "api",
                    State = "Running",
                    HealthStatus = "Healthy",
                    Urls =
                    [
                        new ResourceSnapshotUrl { Name = "http", Url = "http://localhost:5000" },
                        new ResourceSnapshotUrl { Name = "https", Url = "https://localhost:5001" },
                        new ResourceSnapshotUrl { Name = "management", Url = "http://localhost:9000", IsInternal = true }
                    ]
                },
                new ResourceSnapshot { Name = "migrations", State = "Finished", ExitCode = 0 }
            ],
            WaitForResourceHandler = (resourceName, status, _, _) =>
            {
                requests.Enqueue($"{resourceName}:{status}");
                return Task.FromResult(resourceName == "api"
                    ? new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" }
                    : new WaitForResourceResponse
                    {
                        Success = false,
                        ErrorMessage = "Stopped waiting for resource 'migrations' to become healthy because it failed to start."
                    });
            }
        };
        var interactionService = new TestInteractionService();

        var exitCode = await RunWaitAsync("wait api migrations api --timeout 5", backchannel, interactionService);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(["api:healthy", "migrations:healthy"], requests.Order());
        Assert.Equal(
            [
                SharedCommandStrings.ScanningForRunningAppHosts,
                string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitingForResources, 2, "healthy")
            ],
            interactionService.ShownStatuses);
        Assert.Equal(
            [
                (KnownEmojis.CheckMarkButton, string.Format(
                    CultureInfo.CurrentCulture,
                    WaitCommandStrings.ResourceLineWithEndpoints,
                    string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceLineReady, "api", "healthy"),
                    "https://localhost:5001, http://localhost:5000")),
                (KnownEmojis.CheckMarkButton, string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceLineCompleted, "migrations"))
            ],
            interactionService.DisplayedMessages.Select(static m => (m.Emoji, m.Message)));
        Assert.Equal(
            [string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.AllResourcesDone, 2, 0.0)],
            interactionService.DisplayedSuccess);
        Assert.Empty(interactionService.DisplayedErrors);
    }

    [Fact]
    public async Task WaitCommand_MultipleResources_FailsFastWhenResourceExitsWithNonZeroCode()
    {
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot { Name = "api", State = "Starting" },
                new ResourceSnapshot { Name = "worker", State = "Exited", ExitCode = 1 }
            ],
            WaitForResourceHandler = async (resourceName, _, _, cancellationToken) =>
            {
                if (resourceName == "api")
                {
                    // Never becomes healthy on its own; only the worker failure can end this wait.
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                return new WaitForResourceResponse
                {
                    Success = false,
                    ErrorMessage = "Stopped waiting for resource 'worker' to become healthy because it failed to start."
                };
            }
        };
        var interactionService = new TestInteractionService();

        var exitCode = await RunWaitAsync("wait api worker", backchannel, interactionService);

        Assert.Equal(CliExitCodes.WaitResourceFailed, exitCode);
        Assert.Equal(
            [
                (KnownEmojis.StopSign, string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceLineCancelled, "api", "healthy", "Starting")),
                (KnownEmojis.CrossMark, string.Format(
                    CultureInfo.CurrentCulture,
                    WaitCommandStrings.ResourceLineFailed,
                    "worker",
                    string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.StateWithExitCode, "Exited", 1)))
            ],
            // Failing commands also get the shared "See logs at ..." message after the resource lines.
            interactionService.DisplayedMessages.Take(2).Select(static m => (m.Emoji, m.Message)));
        Assert.Equal(
            [string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourcesFailed, 1, 2)],
            interactionService.DisplayedErrors);
        Assert.Empty(interactionService.DisplayedSuccess);
    }

    [Theory]
    [InlineData("Exited", 3)]
    [InlineData("Terminated", null)]
    public async Task WaitCommand_MultipleResources_FailsWhenHealthyResourceLaterStops(string stoppedState, int? exitCode)
    {
        var workerHealthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var apiCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<ResourceSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ResourceSnapshot { Name = "api", State = "Starting" };
            yield return new ResourceSnapshot { Name = "worker", State = "Running", HealthStatus = "Healthy" };

            // Stop the worker only once its own healthy wait has succeeded, so only the watch can fail it.
            await workerHealthy.Task.WaitAsync(cancellationToken);
            yield return new ResourceSnapshot { Name = "worker", State = stoppedState, ExitCode = exitCode };

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WatchResourceSnapshotsHandler = (_, cancellationToken) => WatchAsync(cancellationToken),
            WaitForResourceHandler = async (resourceName, _, _, cancellationToken) =>
            {
                if (resourceName == "api")
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        apiCancelled.TrySetResult();
                        throw;
                    }
                }

                // Without health checks, "worker" is healthy while it runs, so its own wait succeeds.
                workerHealthy.TrySetResult();
                return new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" };
            }
        };
        var interactionService = new TestInteractionService();

        var exitCodeResult = await RunWaitAsync("wait api worker", backchannel, interactionService);

        Assert.Equal(CliExitCodes.WaitResourceFailed, exitCodeResult);
        Assert.True(apiCancelled.Task.IsCompleted);
        Assert.Equal(
            [
                (KnownEmojis.StopSign, string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceLineCancelled, "api", "healthy", "Starting")),
                (KnownEmojis.CrossMark, string.Format(
                    CultureInfo.CurrentCulture,
                    WaitCommandStrings.ResourceLineFailed,
                    "worker",
                    exitCode is int code
                        ? string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.StateWithExitCode, stoppedState, code)
                        : stoppedState))
            ],
            interactionService.DisplayedMessages.Take(2).Select(static m => (m.Emoji, m.Message)));
        Assert.Equal(
            [string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourcesFailed, 1, 2)],
            interactionService.DisplayedErrors);

        // The watched snapshots already describe both resources, so no extra read is needed to report them.
        Assert.Equal(0, backchannel.GetResourceSnapshotsCallCount);
    }

    [Fact]
    public async Task WaitCommand_SingleResourceAmbiguousReplicas_ShowsAppHostError()
    {
        const string ambiguousError = "Resource 'api' is ambiguous because it has multiple replicas. Specify the exact instance name.";
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot { Name = "api-abc", DisplayName = "api", State = "Running" },
                new ResourceSnapshot { Name = "api-def", DisplayName = "api", State = "Running" }
            ],
            WaitForResourceResult = new WaitForResourceResponse { Success = false, ErrorMessage = ambiguousError }
        };
        var interactionService = new TestInteractionService();

        var exitCode = await RunWaitAsync("wait api", backchannel, interactionService);

        Assert.Equal(CliExitCodes.WaitResourceFailed, exitCode);
        Assert.Equal(
            [string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceEnteredFailedState, "api", ambiguousError)],
            interactionService.DisplayedErrors);
    }

    [Fact]
    public async Task WaitCommand_MultipleResources_ReturnsTimeoutExitCode()
    {
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot { Name = "api", State = "Running", HealthStatus = "Healthy" },
                new ResourceSnapshot { Name = "cache", State = "Running", HealthStatus = "Unhealthy" }
            ],
            WaitForResourceHandler = (resourceName, _, _, _) => Task.FromResult(resourceName == "api"
                ? new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" }
                : new WaitForResourceResponse { Success = false, TimedOut = true })
        };
        var interactionService = new TestInteractionService();

        var exitCode = await RunWaitAsync("wait api cache --timeout 5", backchannel, interactionService);

        Assert.Equal(CliExitCodes.WaitTimeout, exitCode);
        Assert.Equal(
            [
                (KnownEmojis.CheckMarkButton, string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceLineReady, "api", "healthy")),
                (KnownEmojis.PoliceCarLight, string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceLineTimedOut, "cache", "healthy", "Running"))
            ],
            interactionService.DisplayedMessages.Take(2).Select(static m => (m.Emoji, m.Message)));
        Assert.Equal(
            [string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourcesTimedOut, 1, 2, "healthy", 5)],
            interactionService.DisplayedErrors);
    }

    [Fact]
    public async Task WaitCommand_All_SkipsHiddenAndExplicitStartResourcesAndWritesJson()
    {
        var requests = new ConcurrentQueue<string>();
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = "api-abc123",
                    DisplayName = "api",
                    State = "Running",
                    HealthStatus = "Healthy",
                    Urls = [new ResourceSnapshotUrl { Name = "http", Url = "http://localhost:5000" }]
                },
                new ResourceSnapshot { Name = "admin", DisplayName = "admin", State = "NotStarted" },
                new ResourceSnapshot { Name = "internal", DisplayName = "internal", State = "Running", IsHidden = true }
            ],
            WaitForResourceHandler = (resourceName, _, _, _) =>
            {
                requests.Enqueue(resourceName);
                return Task.FromResult(new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" });
            }
        };
        var interactionService = new TestInteractionService();

        var exitCode = await RunWaitAsync("wait --all --format json --timeout 5", backchannel, interactionService);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(["api-abc123"], requests);
        var (json, consoleOutput) = Assert.Single(interactionService.DisplayedRawText);
        Assert.Equal(ConsoleOutput.Standard, consoleOutput);
        Assert.Equal(
            """
            {
              "outcome": "success",
              "status": "healthy",
              "resources": [
                {
                  "name": "api-abc123",
                  "displayName": "api",
                  "outcome": "success",
                  "state": "Running",
                  "healthStatus": "Healthy",
                  "urls": [
                    {
                      "name": "http",
                      "url": "http://localhost:5000"
                    }
                  ]
                },
                {
                  "name": "admin",
                  "displayName": "admin",
                  "outcome": "skipped",
                  "state": "NotStarted",
                  "urls": []
                }
              ]
            }
            """.ReplaceLineEndings(),
            json.ReplaceLineEndings());
        Assert.Empty(interactionService.DisplayedMessages);
    }

    [Fact]
    public async Task WaitCommand_All_WaitsForResourceInventoryBeforeSelectingResources()
    {
        var requests = new ConcurrentQueue<string>();
        TestAppHostAuxiliaryBackchannel? backchannel = null;
        backchannel = new TestAppHostAuxiliaryBackchannel
        {
            SupportsResourceInventoryV1 = true,
            // Only "api" has published a state when the command connects; "cache" publishes before the
            // AppHost confirms its inventory.
            ResourceSnapshots = [new ResourceSnapshot { Name = "api", State = "Running", HealthStatus = "Healthy" }],
            WaitForResourcesInitializedHandler = _ =>
            {
                backchannel!.ResourceSnapshots =
                [
                    new ResourceSnapshot { Name = "api", State = "Running", HealthStatus = "Healthy" },
                    new ResourceSnapshot { Name = "cache", State = "Starting" }
                ];
                return Task.CompletedTask;
            },
            WaitForResourceHandler = (resourceName, _, _, _) =>
            {
                requests.Enqueue(resourceName);
                return Task.FromResult(new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" });
            }
        };
        var interactionService = new TestInteractionService();

        var exitCode = await RunWaitAsync("wait --all --format json --timeout 5", backchannel, interactionService);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(["api", "cache"], requests.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WaitCommand_All_SkipsOnlyExplicitStartResourcesThatHaveNotStarted()
    {
        var requests = new ConcurrentQueue<string>();
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            SupportsResourceInventoryV1 = true,
            ResourceSnapshots =
            [
                // Connection strings start NotStarted and initialize on their own, so they are waited on.
                new ResourceSnapshot { Name = "db-connection", State = "NotStarted" },
                new ResourceSnapshot { Name = "admin", State = "NotStarted", IsExplicitStart = true },
                new ResourceSnapshot { Name = "tool", IsExplicitStart = true },
                new ResourceSnapshot { Name = "worker", State = "Running", IsExplicitStart = true }
            ],
            WaitForResourceHandler = (resourceName, _, _, _) =>
            {
                requests.Enqueue(resourceName);
                return Task.FromResult(new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" });
            }
        };
        var interactionService = new TestInteractionService();

        var exitCode = await RunWaitAsync("wait --all --format json --timeout 5", backchannel, interactionService);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(["db-connection", "worker"], requests.Order(StringComparer.Ordinal));
        var (json, _) = Assert.Single(interactionService.DisplayedRawText);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            [("admin", "skipped"), ("db-connection", "success"), ("tool", "skipped"), ("worker", "success")],
            document.RootElement.GetProperty("resources").EnumerateArray()
                .Select(static r => (r.GetProperty("name").GetString(), r.GetProperty("outcome").GetString()))
                .OrderBy(static r => r.Item1, StringComparer.Ordinal));
    }

    [Fact]
    public async Task WaitCommand_All_ResourceInventoryTimeout_ReturnsTimeoutExitCode()
    {
        var timeProvider = new FakeTimeProvider();
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            SupportsResourceInventoryV1 = true,
            WaitForResourcesInitializedHandler = async cancellationToken =>
            {
                timeProvider.Advance(TimeSpan.FromSeconds(6));
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        var interactionService = new TestInteractionService();

        var exitCode = await RunWaitAsync("wait --all --timeout 5", backchannel, interactionService, timeProvider);

        Assert.Equal(CliExitCodes.WaitTimeout, exitCode);
        Assert.Equal(
            [string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceInventoryTimedOut, 5)],
            interactionService.DisplayedErrors);
    }

    private async Task<int> RunWaitAsync(string commandLine, TestAppHostAuxiliaryBackchannel backchannel, TestInteractionService interactionService, FakeTimeProvider? timeProvider = null)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("/tmp/test.sock", backchannel);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AuxiliaryBackchannelMonitorFactory = _ => monitor;
            options.InteractionServiceFactory = _ => interactionService;
            options.TimeProvider = timeProvider ?? new FakeTimeProvider();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        return await command.Parse(commandLine).InvokeAsync().DefaultTimeout();
    }
}
