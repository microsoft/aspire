// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Watch;
using Aspire.Shared;
using Aspire.Shared.DotnetWatch;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;

namespace Aspire.Cli.Tests.Commands;

public class RunCommandReloadTests
{
    [Theory]
    [InlineData("terminal", true)]
    [InlineData("redirected-input", false)]
    [InlineData("non-interactive", false)]
    [InlineData("non-interactive-environment", false)]
    [InlineData("non-interactive-environment-one", false)]
    [InlineData("ci", false)]
    [InlineData("github-actions", false)]
    [InlineData("azure-pipelines", false)]
    [InlineData("extension", false)]
    [InlineData("detach", false)]
    [InlineData("json", false)]
    [InlineData("unsupported-input", false)]
    [InlineData("ci-false", true)]
    public void ShortcutsRequireAnInteractiveTerminal(string environment, bool expected)
    {
        var values = new Dictionary<string, string?>();
        switch (environment)
        {
            case "non-interactive-environment":
                values["ASPIRE_NON_INTERACTIVE"] = "TRUE";
                break;
            case "non-interactive-environment-one":
                values["ASPIRE_NON_INTERACTIVE"] = "1";
                break;
            case "ci":
                values["CI"] = "true";
                break;
            case "github-actions":
                values["GITHUB_ACTIONS"] = "true";
                break;
            case "azure-pipelines":
                values["TF_BUILD"] = "True";
                break;
            case "ci-false":
                values["CI"] = "false";
                break;
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var input = new TestConsoleInput { IsInputRedirected = environment == "redirected-input" };

        var available = RunCommand.CanListenForReload(
            input,
            environment == "unsupported-input"
                ? TestHelpers.CreateNonInteractiveHostEnvironment()
                : TestHelpers.CreateInteractiveHostEnvironment(),
            configuration,
            nonInteractive: environment == "non-interactive",
            isExtensionHost: environment == "extension",
            detach: environment == "detach",
            format: environment == "json" ? OutputFormat.Json : default);

        Assert.Equal(expected, available);
        Assert.Equal(0, input.KeyAvailableChecks);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("AppHost")]
    [InlineData("Services")]
    public async Task HintDescribesTheScopeOfTheOfferedReload(string mode)
    {
        await Verify(RenderHint(mode, 200)).UseParameters(mode);
    }

    [Theory]
    [InlineData("AppHost")]
    [InlineData("Services")]
    public async Task NarrowTerminalsShowBothReloadAndStopShortcuts(string mode)
    {
        await Verify(RenderHint(mode, 80)).UseParameters(mode)
            .ScrubLinesWithReplace(line => line.TrimEnd());
    }

    [Theory]
    [InlineData(false, "watching", "dotnet-watch")]
    [InlineData(true, "watching", null)]
    [InlineData(true, "watching", "")]
    [InlineData(true, "excluded", "dotnet-watch")]
    [InlineData(true, "unknown", "dotnet-watch")]
    public async Task UnsupportedOrUnwatchedRunsDoNotReadConsoleInput(bool capability, string state, string? logResource)
    {
        using var fixture = new TelemetryFixture();
        var input = new TestConsoleInput();
        var requests = 0;
        var backchannel = new TestAppHostBackchannel
        {
            GetCapabilitiesAsyncCallback = _ => Task.FromResult(capability ? new[] { ResourceWatch.ReloadCapability } : []),
            ReloadWatchedResourcesAsyncCallback = _ =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(new ResourceWatchReloadResult { Groups = [] });
            }
        };
        await using var reload = CreateReload(input, new TestInteractionService(), new ConsoleLogBufferContext(), fixture);
        input.PressReload();

        await reload.BackchannelEstablishedAsync(backchannel, TestContext.Current.CancellationToken);
        reload.UpdateWatchStatus(CreateWatchStatus(state, logResource));

        Assert.Equal(RunReloadMode.None, reload.Mode);
        Assert.Equal(0, requests);
        Assert.Equal(0, input.ReadCount);
        Assert.Equal(0, input.KeyAvailableChecks);
        await reload.DisposeAsync();
        Assert.Empty(fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHostReloadSummary));
        Assert.Empty(fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHostReload));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task OnlyCtrlRIsAcceptedAndStartupInputIsDiscarded(int startupPresses)
    {
        using var fixture = new TelemetryFixture();
        var input = new TestConsoleInput();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var backchannel = CreateBackchannel(_ =>
        {
            Interlocked.Increment(ref requests);
            requested.TrySetResult();
            return Task.FromResult(new ResourceWatchReloadResult { Groups = [] });
        });
        await using var reload = CreateReload(input, new TestInteractionService(), new ConsoleLogBufferContext(), fixture);
        for (var index = 0; index < startupPresses; index++)
        {
            input.PressReload();
        }
        reload.UpdateWatchStatus(CreateWatchStatus("watching", "dotnet-watch"));
        Assert.Equal(0, input.ReadCount);

        await reload.BackchannelEstablishedAsync(backchannel, TestContext.Current.CancellationToken);
        Assert.Equal(startupPresses, input.ReadCount);
        Assert.Equal(0, requests);
        input.Enqueue(new ConsoleKeyInfo('r', ConsoleKey.R, shift: false, alt: false, control: false));
        input.Enqueue(new ConsoleKeyInfo('R', ConsoleKey.R, shift: true, alt: false, control: true));
        input.Enqueue(new ConsoleKeyInfo('r', ConsoleKey.R, shift: false, alt: true, control: false));
        input.Enqueue(new ConsoleKeyInfo('\u0003', ConsoleKey.C, shift: false, alt: false, control: true));
        input.PressReload();
        await requested.Task.DefaultTimeout();

        Assert.Equal(1, requests);
        Assert.Equal(startupPresses + 5, input.ReadCount);
        Assert.All(input.InterceptedReads, Assert.True);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("watching")]
    [InlineData("build-failed")]
    [InlineData("failed")]
    public async Task LateWatchGroupsPrintTheReloadHintOnlyOnce(string state)
    {
        using var fixture = new TelemetryFixture();
        var hints = 0;
        await using var reload = CreateReload(new TestConsoleInput(), new TestInteractionService(),
            new ConsoleLogBufferContext(), fixture);
        await reload.BackchannelEstablishedAsync(CreateBackchannel(_ =>
            Task.FromResult(new ResourceWatchReloadResult { Groups = [] })), TestContext.Current.CancellationToken);
        reload.RegisterHint(_ => { }, _ => Interlocked.Increment(ref hints));

        reload.UpdateWatchStatus(CreateWatchStatus(state, "dotnet-watch"));
        reload.UpdateWatchStatus(CreateWatchStatus(state, "dotnet-watch"));
        reload.UpdateWatchStatus(null);
        reload.UpdateWatchStatus(CreateWatchStatus(state, "dotnet-watch"));

        Assert.Equal(RunReloadMode.Services, reload.Mode);
        Assert.Equal(1, hints);
    }

    [Fact]
    public void NestedPromptScopesPreventShortcutsFromStealingInput()
    {
        var context = new ConsoleLogBufferContext();
        var input = new TestConsoleInput();
        input.PressReload();

        using (context.BeginInteractivePromptScope())
        {
            using (context.BeginInteractivePromptScope())
            {
                Assert.True(context.IsInteractivePromptActive);
                Assert.False(context.TryReadKey(input, out _));
                Assert.False(context.TryRunWithoutInteractivePrompt(() => throw new InvalidOperationException("A reload must not start during a prompt.")));
                Assert.Equal(0, input.KeyAvailableChecks);
            }

            Assert.True(context.IsInteractivePromptActive);
            Assert.False(context.TryReadKey(input, out _));
            Assert.Equal(0, input.ReadCount);
        }

        Assert.False(context.IsInteractivePromptActive);
        Assert.True(context.TryReadKey(input, out var key));
        Assert.Equal(ConsoleKey.R, key.Key);
        Assert.Equal(ConsoleModifiers.Control, key.Modifiers);
        Assert.Equal([true], input.InterceptedReads);
        var resumed = false;
        Assert.True(context.TryRunWithoutInteractivePrompt(() => resumed = true));
        Assert.True(resumed);
    }

    [Fact]
    public async Task PressesDuringAServiceReloadCoalesceIntoOneQueuedRequest()
    {
        using var fixture = new TelemetryFixture();
        using var activity = fixture.Telemetry.StartReportedActivity(TelemetryConstants.Activities.RunAppHost);
        var input = new TestConsoleInput();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirst = new TaskCompletionSource<ResourceWatchReloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSecond = new TaskCompletionSource<ResourceWatchReloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var activeRequests = 0;
        var maximumActiveRequests = 0;
        var backchannel = CreateBackchannel(async token =>
        {
            var request = Interlocked.Increment(ref requests);
            var active = Interlocked.Increment(ref activeRequests);
            maximumActiveRequests = Math.Max(maximumActiveRequests, active);
            try
            {
                (request == 1 ? firstStarted : secondStarted).TrySetResult();
                return await (request == 1 ? finishFirst : finishSecond).Task.WaitAsync(token);
            }
            finally
            {
                Interlocked.Decrement(ref activeRequests);
            }
        });
        var reload = new RunCommandReload(true, input, new ConsoleLogBufferContext(), new TestInteractionService(),
            generation: null, fixture.Telemetry, activity, TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken);
        try
        {
            await reload.BackchannelEstablishedAsync(backchannel, TestContext.Current.CancellationToken);
            reload.UpdateWatchStatus(CreateWatchStatus("watching", "dotnet-watch"));
            input.PressReload();
            await firstStarted.Task.DefaultTimeout();

            for (var index = 0; index < 10; index++)
            {
                input.PressReload();
            }
            await input.WaitForReadCountAsync(11, TestContext.Current.CancellationToken).DefaultTimeout();
            Assert.Equal(1, requests);
            finishFirst.TrySetResult(CreateRebuiltResult());
            await secondStarted.Task.DefaultTimeout();
            finishSecond.TrySetResult(CreateRebuiltResult());
            await WaitForReloadActivitiesAsync(fixture, 2);
        }
        finally
        {
            await reload.DisposeAsync();
        }

        Assert.Equal(2, requests);
        Assert.Equal(1, maximumActiveRequests);
        Assert.NotNull(activity);
        Assert.Null(activity.GetTagItem(TelemetryConstants.Tags.RunReloadCount));
        var summary = Assert.Single(fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHostReloadSummary));
        Assert.Equal(activity.SpanId, summary.ParentSpanId);
        Assert.Equal("services", summary.GetTagItem(TelemetryConstants.Tags.RunReloadMode));
        Assert.Equal(2, summary.GetTagItem(TelemetryConstants.Tags.RunReloadCount));
        Assert.Collection(fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHostReload),
            first => AssertReload(first, summary, "services", TelemetryConstants.RunReloadOutcomes.Succeeded, success: true),
            second => AssertReload(second, summary, "services", TelemetryConstants.RunReloadOutcomes.Succeeded, success: true));
    }

    [Fact]
    public async Task ShutdownCancelsAnInFlightReloadAndRecordsItAsCanceled()
    {
        using var fixture = new TelemetryFixture();
        using var activity = fixture.Telemetry.StartReportedActivity(TelemetryConstants.Activities.RunAppHost);
        var input = new TestConsoleInput();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannel = CreateBackchannel(async token =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new ResourceWatchReloadResult { Groups = [] };
            }
            finally
            {
                cancelled.TrySetResult();
            }
        });
        var reload = new RunCommandReload(true, input, new ConsoleLogBufferContext(), new TestInteractionService(),
            generation: null, fixture.Telemetry, activity, TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken);
        try
        {
            await reload.BackchannelEstablishedAsync(backchannel, TestContext.Current.CancellationToken);
            reload.UpdateWatchStatus(CreateWatchStatus("watching", "dotnet-watch"));
            input.PressReload();
            await started.Task.DefaultTimeout();
        }
        finally
        {
            await reload.DisposeAsync();
        }

        await cancelled.Task.DefaultTimeout();
        var summary = Assert.Single(fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHostReloadSummary));
        Assert.Equal(1, summary.GetTagItem(TelemetryConstants.Tags.RunReloadCount));
        AssertReload(Assert.Single(fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHostReload)),
            summary, "services", TelemetryConstants.RunReloadOutcomes.Canceled, success: true);
    }

    [Theory]
    [InlineData("rpc-failed")]
    [InlineData("connection-lost-on-shutdown")]
    [InlineData("no-groups")]
    public async Task ServiceReloadRecordsWhyItDidNotRebuildAnything(string scenario)
    {
        using var fixture = new TelemetryFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var input = new TestConsoleInput();
        var interaction = new TestInteractionService();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannel = CreateBackchannel(async token =>
        {
            requested.TrySetResult();
            switch (scenario)
            {
                case "rpc-failed":
                    throw new InvalidOperationException("RAW RPC DETAILS");
                case "connection-lost-on-shutdown":
                    // Shutdown can close the connection before the RPC observes cancellation.
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                    throw new InvalidOperationException("The connection was lost.");
                default:
                    return new ResourceWatchReloadResult { Groups = [] };
            }
        });
        var reload = new RunCommandReload(true, input, new ConsoleLogBufferContext(), interaction,
            generation: null, fixture.Telemetry, runActivity: null, TimeProvider.System, NullLogger.Instance, cancellation.Token);
        try
        {
            await reload.BackchannelEstablishedAsync(backchannel, TestContext.Current.CancellationToken);
            reload.UpdateWatchStatus(CreateWatchStatus("watching", "dotnet-watch"));
            input.PressReload();
            await requested.Task.DefaultTimeout();
            if (scenario == "connection-lost-on-shutdown")
            {
                await cancellation.CancelAsync();
            }
            else
            {
                await WaitForReloadActivitiesAsync(fixture, 1);
            }
        }
        finally
        {
            await reload.DisposeAsync();
        }

        var summary = Assert.Single(fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHostReloadSummary));
        var (outcome, success, messages, errors) = scenario switch
        {
            "rpc-failed" => (TelemetryConstants.RunReloadOutcomes.RpcFailed, false,
                new[] { RunCommandStrings.RebuildingWatchedServices }, new[] { RunCommandStrings.CouldNotReloadWatchedServices }),
            "connection-lost-on-shutdown" => (TelemetryConstants.RunReloadOutcomes.Canceled, true,
                [RunCommandStrings.RebuildingWatchedServices], [RunCommandStrings.CouldNotReloadWatchedServices]),
            _ => (TelemetryConstants.RunReloadOutcomes.NoWatchedServices, true,
                [RunCommandStrings.RebuildingWatchedServices, RunCommandStrings.NoWatchedServicesToRebuild], Array.Empty<string>())
        };
        AssertReload(Assert.Single(fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHostReload)),
            summary, "services", outcome, success);
        Assert.Equal(messages, interaction.DisplayedMessages.Select(message => message.Message));
        Assert.Equal(errors, interaction.DisplayedErrors);
    }

    [Theory]
    [InlineData(new string[0], new string?[0], TelemetryConstants.RunReloadOutcomes.NoWatchedServices)]
    [InlineData(new[] { "ok", "ok" }, new string?[] { "watching", "watching" }, TelemetryConstants.RunReloadOutcomes.Succeeded)]
    [InlineData(new[] { "fail" }, new string?[] { "build-failed" }, TelemetryConstants.RunReloadOutcomes.BuildFailed)]
    [InlineData(new[] { "fail" }, new string?[] { "failed" }, TelemetryConstants.RunReloadOutcomes.GroupFailed)]
    [InlineData(new[] { "fail" }, new string?[] { "watching" }, TelemetryConstants.RunReloadOutcomes.StartFailed)]
    [InlineData(new[] { "fail" }, new string?[] { null }, TelemetryConstants.RunReloadOutcomes.StartFailed)]
    [InlineData(new[] { "ok", "fail" }, new string?[] { "failed", "build-failed" }, TelemetryConstants.RunReloadOutcomes.BuildFailed)]
    [InlineData(new[] { "fail", "fail" }, new string?[] { "build-failed", "watching" }, TelemetryConstants.RunReloadOutcomes.StartFailed)]
    [InlineData(new[] { "fail", "fail", "fail" }, new string?[] { "build-failed", "watching", "failed" }, TelemetryConstants.RunReloadOutcomes.GroupFailed)]
    public void ServiceReloadOutcomeComesFromTheWatchStateOfFailedGroups(string[] results, string?[] states, string expected)
    {
        var result = new ResourceWatchReloadResult
        {
            Groups = [.. results.Select((outcome, index) => new ResourceWatchReloadGroupResult
            {
                LogResource = $"dotnet-watch-{index}",
                Resources = [$"api-{index}"],
                Success = outcome == "ok",
                State = states[index]
            })]
        };

        Assert.Equal(expected, RunCommandReload.GetOutcome(result));
        Assert.Equal(expected is TelemetryConstants.RunReloadOutcomes.Succeeded or TelemetryConstants.RunReloadOutcomes.NoWatchedServices,
            TelemetryConstants.RunReloadOutcomes.IsSuccess(expected));
    }

    [Fact]
    public async Task AnAppHostReloadAcknowledgesTheKeyPressBeforeTheAppHostStops()
    {
        using var fixture = new TelemetryFixture();
        using var shutdown = new TestGracefulShutdownWindow();
        await using var generation = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromSeconds(5), ".", NullLogger.Instance);
        var input = new TestConsoleInput();
        var interaction = new TestInteractionService();
        var reload = new RunCommandReload(true, input, new ConsoleLogBufferContext(), interaction,
            generation, fixture.Telemetry, runActivity: null, TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken);
        try
        {
            Assert.True(reload.EnableAppHost());
            input.PressReload();
            await generation.Invalidated.DefaultTimeout();
        }
        finally
        {
            // Drains the listener, so the press has been handled.
            await reload.DisposeAsync();
        }

        // The restart loop shows the reloading message once the AppHost has stopped.
        Assert.Equal([RunCommandStrings.StoppingAppHostForReload], interaction.DisplayedMessages.Select(message => message.Message));
    }

    [Theory]
    [InlineData("started", TelemetryConstants.RunReloadOutcomes.Succeeded, true)]
    [InlineData("build-failed", TelemetryConstants.RunReloadOutcomes.BuildFailed, false)]
    [InlineData("restarted", TelemetryConstants.RunReloadOutcomes.Superseded, true)]
    [InlineData("reloaded-again", TelemetryConstants.RunReloadOutcomes.SupersededByReload, true)]
    [InlineData("canceled", TelemetryConstants.RunReloadOutcomes.Canceled, true)]
    public async Task AnAppHostReloadIsCompletedByTheReplacementAppHost(string ending, string outcome, bool success)
    {
        using var fixture = new TelemetryFixture();
        using var shutdown = new TestGracefulShutdownWindow();
        using var directory = new DotnetWatchTestDirectory();
        await using var first = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromSeconds(5), ".", NullLogger.Instance);
        var input = new TestConsoleInput();
        var reload = new RunCommandReload(true, input, new ConsoleLogBufferContext(), new TestInteractionService(),
            first, fixture.Telemetry, runActivity: null, TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken);
        try
        {
            Assert.True(reload.EnableAppHost());
            input.PressReload();
            await first.Invalidated.DefaultTimeout();
        }
        finally
        {
            // Drains the listener, so the reload that the press started is visible on the generation.
            await reload.DisposeAsync();
        }
        Assert.True(first.ReloadRequested);
        var summary = Assert.Single(fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHostReloadSummary));
        Assert.Equal("apphost", summary.GetTagItem(TelemetryConstants.Tags.RunReloadMode));
        Assert.Equal(1, summary.GetTagItem(TelemetryConstants.Tags.RunReloadCount));
        Assert.NotNull(first.RequestedReload);
        Assert.Empty(fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHostReload));

        await using var replacement = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, NullLogger.Instance)
        {
            IsReplacement = true,
            StartedByReload = true,
            PendingReload = first.RequestedReload
        };
        var replacementReload = new RunCommandReload(true, new TestConsoleInput(), new ConsoleLogBufferContext(), new TestInteractionService(),
            replacement, fixture.Telemetry, runActivity: null, TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken);
        try
        {
            switch (ending)
            {
                case "started":
                    replacementReload.RecordAppHostStarted();
                    break;
                case "build-failed":
                    replacementReload.RecordAppHostFailure(TelemetryConstants.RunReloadOutcomes.BuildFailed);
                    // The generic startup failure recorded when the run ends doesn't replace the specific one.
                    replacementReload.RecordAppHostFailure(TelemetryConstants.RunReloadOutcomes.StartFailed);
                    break;
                case "restarted":
                    // dotnet watch restarts the replacement AppHost, for example after a rude edit.
                    replacement.ApplyEnvironment(new Dictionary<string, string>());
                    await NotifyAsync(replacement, DotnetWatchAppHostProtocol.HostStarted);
                    await NotifyAsync(replacement, DotnetWatchAppHostProtocol.ModelInvalidated);
                    await replacement.Invalidated.DefaultTimeout();
                    Assert.False(replacement.ReloadRequested);
                    break;
                case "reloaded-again":
                    Assert.True(replacement.RequestReload());
                    break;
            }
        }
        finally
        {
            await replacementReload.DisposeAsync();
        }

        AssertReload(Assert.Single(fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHostReload)),
            summary, "apphost", outcome, success);
    }

    private static void AssertReload(Activity reload, Activity summary, string mode, string outcome, bool success)
    {
        Assert.Equal(summary.SpanId, reload.ParentSpanId);
        Assert.Equal(mode, reload.GetTagItem(TelemetryConstants.Tags.RunReloadMode));
        Assert.Equal(outcome, reload.GetTagItem(TelemetryConstants.Tags.RunReloadOutcome));
        Assert.Equal(success, reload.GetTagItem(TelemetryConstants.Tags.RunReloadSuccess));
    }

    private static Task WaitForReloadActivitiesAsync(TelemetryFixture fixture, int count) =>
        AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHostReload).Count >= count,
            $"Expected {count} completed reload activities.");

    private static Task NotifyAsync(DotnetWatchAppHostGeneration generation, int kind) =>
        DotnetWatchAppHostTestChannel.SendAsync(generation.Endpoint, new()
        {
            Version = DotnetWatchAppHostProtocol.Version,
            Session = generation.Session,
            Kind = kind,
            Instance = "123:456"
        }, TestContext.Current.CancellationToken).DefaultTimeout();

    private static ResourceWatchReloadResult CreateRebuiltResult() => new()
    {
        Groups = [new() { LogResource = "dotnet-watch", Resources = ["api"], Success = true, State = "watching" }]
    };

    private static string RenderHint(string mode, int width)
    {
        using var output = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        console.Profile.Width = width;
        console.Write(RunCommand.BuildCtrlCRenderable(0, Enum.Parse<RunReloadMode>(mode)));
        return output.ToString().Trim();
    }

    private static RunCommandReload CreateReload(
        TestConsoleInput input, TestInteractionService interaction, ConsoleLogBufferContext promptContext, TelemetryFixture fixture) =>
        new(true, input, promptContext, interaction, generation: null, fixture.Telemetry, runActivity: null,
            TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken);

    private static TestAppHostBackchannel CreateBackchannel(Func<CancellationToken, Task<ResourceWatchReloadResult>> callback) => new()
    {
        GetCapabilitiesAsyncCallback = _ => Task.FromResult(new[] { ResourceWatch.ReloadCapability }),
        ReloadWatchedResourcesAsyncCallback = callback
    };

    private static ResourceWatchStatus CreateWatchStatus(string state, string? logResource) => new()
    {
        GenerationId = Guid.NewGuid(),
        DefaultEnabled = true,
        Resources = [new() { Resource = "api", State = state, LogResource = logResource }]
    };
}
