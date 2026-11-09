// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared.DotnetWatch;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Commands;

public class RunCommandWatchTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false, false, CliExitCodes.Success)]
    [InlineData(true, false, CliExitCodes.Success)]
    [InlineData(true, true, CliExitCodes.FailedToDotnetRunAppHost)]
    public async Task ProfileCaptureStopsThePackageWatcherAfterStoppingItsAppHost(
        bool throwCancellation, bool useUnrelatedToken, int expectedExitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        // Keep the actual private generation socket within macOS's AF_UNIX path limit.
        using var home = new TemporaryWorkspace(outputHelper, Directory.CreateTempSubdirectory("ah-"));
        using var unrelatedCancellation = new CancellationTokenSource();
        var stopped = false;
        var stopRequests = 0;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(
                workspace.WorkspaceRoot, homeDirectory: home.WorkspaceRoot);
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                RunAsyncCallback = async (context, token) =>
                {
                    var generation = context.WatchGeneration!;
                    generation.ApplyEnvironment(new Dictionary<string, string>());
                    await DotnetWatchAppHostTestChannel.SendAsync(generation.Endpoint, new()
                    {
                        Version = DotnetWatchAppHostProtocol.Version,
                        Session = generation.Session,
                        Kind = DotnetWatchAppHostProtocol.HostStarted,
                        Instance = "123:456"
                    }, token);
                    await generation.HostStarted.WaitAsync(token);
                    context.BuildCompletionSource!.TrySetResult(true);
                    context.BackchannelCompletionSource!.TrySetResult(new TestAppHostBackchannel
                    {
                        RequestStopAsyncCallback = () =>
                        {
                            Interlocked.Increment(ref stopRequests);
                            return Task.CompletedTask;
                        }
                    });
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return 0;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        stopped = true;
                        if (throwCancellation)
                        {
                            throw new OperationCanceledException(useUnrelatedToken ? unrelatedCancellation.Token : token);
                        }

                        return CliExitCodes.Cancelled;
                    }
                }
            };
        });
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse("run --watch true --capture-profile --capture-profile-delay 0")
            .InvokeAsync().DefaultTimeout();

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(1, stopRequests);
        Assert.True(stopped);
    }

    [Theory]
    [InlineData("run", true, true, 1)]
    [InlineData("run", false, false, 1)]
    [InlineData("run --watch", false, true, 0)]
    [InlineData("run --watch=false", true, false, 0)]
    [InlineData("run -- --watch=false", true, true, 1)]
    public async Task AppHostWatchIsResolvedOnceAndOnlyEffectiveWatchCreatesAGeneration(
        string commandLine, bool defaultWatch, bool expectedWatch, int expectedDefaultReads)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<AppHostProjectContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var defaultReads = 0;
        var features = new TestFeatures
        {
            IsFeatureEnabledCallback = (name, defaultValue) =>
            {
                if (name != KnownFeatures.DefaultWatchEnabled)
                {
                    return defaultValue;
                }
                Interlocked.Increment(ref defaultReads);
                return defaultWatch;
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.FeatureFlagsFactory = _ => features;
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                RunAsyncCallback = async (context, token) =>
                {
                    started.TrySetResult(context);
                    context.BuildCompletionSource!.TrySetResult(true);
                    context.BackchannelCompletionSource!.TrySetResult(new TestAppHostBackchannel());
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 0;
                }
            };
        });
        using var provider = services.BuildServiceProvider();
        var run = provider.GetRequiredService<RootCommand>().Parse(commandLine).InvokeAsync(cancellationToken: cancellation.Token);
        var context = await started.Task.DefaultTimeout();
        await cancellation.CancelAsync();
        var exitCode = await run.DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(expectedWatch, context.Watch);
        Assert.Equal(expectedWatch, context.WatchGeneration is not null);
        Assert.Equal(expectedDefaultReads, defaultReads);
        Assert.False(context.WatchGeneration?.IsEnabled ?? false);
        if (commandLine.Contains(" -- --watch", StringComparison.Ordinal))
        {
            Assert.Equal(["--watch=false"], context.UnmatchedTokens);
            Assert.False(context.WatchExplicitlySet);
        }
    }

    [Theory]
    [InlineData("run --watch", false)]
    [InlineData("run", true)]
    public async Task AspireTerminalWithoutAnIdeSessionKeepsAppHostWatchCliOwned(string commandLine, bool defaultWatch)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<AppHostProjectContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delegated = false;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.FeatureFlagsFactory = _ => new TestFeatures().SetFeature(KnownFeatures.DefaultWatchEnabled, defaultWatch);
            options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            options.InteractionServiceFactory = sp => new TestExtensionInteractionService(sp)
            {
                StartDebugSessionCallback = (_, _, _, _) => delegated = true
            };
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                RunAsyncCallback = async (context, token) =>
                {
                    started.TrySetResult(context);
                    context.BuildCompletionSource!.TrySetResult(true);
                    context.BackchannelCompletionSource!.TrySetResult(new TestAppHostBackchannel());
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 0;
                }
            };
        });
        using var provider = services.BuildServiceProvider();
        var run = provider.GetRequiredService<RootCommand>().Parse(commandLine).InvokeAsync(cancellationToken: cancellation.Token);
        var context = await started.Task.DefaultTimeout();
        await cancellation.CancelAsync();
        var exitCode = await run.DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(context.Watch);
        Assert.NotNull(context.WatchGeneration);
        Assert.False(delegated);
    }

    [Theory]
    [InlineData(null, null, null, null, false)]
    [InlineData(null, "localhost:12345", null, null, true)]
    [InlineData(null, null, "{}", null, true)]
    [InlineData(null, null, null, "NoDebug", true)]
    [InlineData("cli", "localhost:12345", "{}", "NoDebug", false)]
    public void UpstreamExecutionServiceSettingsAreNotMistakenForRealIdeOwnership(
        string? owner, string? port, string? info, string? mode, bool expectedIde)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DotnetWatchOwnership.OwnerEnvironmentVariable] = owner,
            [KnownConfigNames.DebugSessionPort] = port,
            [KnownConfigNames.DebugSessionInfo] = info,
            [KnownConfigNames.DebugSessionRunMode] = mode
        }).Build();

        Assert.Equal(expectedIde, RunCommand.IsIdeOwnedRun(configuration));
        configuration[KnownConfigNames.ExtensionDebugSessionId] = "real-ide-session";
        Assert.True(RunCommand.IsIdeOwnedRun(configuration));
    }

    [Fact]
    public void LocalizedHelpSeparatesAppHostEditsFromResourcePolicy()
    {
        Assert.Contains("C# AppHosts", RunCommandStrings.WatchOptionDescription, StringComparison.Ordinal);
        Assert.Contains("reconstruct", RunCommandStrings.WatchOptionDescription, StringComparison.Ordinal);
        Assert.Contains("polyglot", RunCommandStrings.WatchOptionDescription, StringComparison.Ordinal);
        Assert.Contains("--watch false", RunCommandStrings.WatchOptionDescription, StringComparison.Ordinal);
        Assert.Contains("ResolveAspireDotnetWatch", RunCommandStrings.WatchBootstrapUnavailable, StringComparison.Ordinal);
        Assert.Contains("AppHost controls resource watching", RunCommandStrings.WatchOptionDescription, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CoverageSummaryUsesActualStatesInsteadOfTheRequestedDefault(bool defaultEnabled)
    {
        var status = new ResourceWatchStatus
        {
            GenerationId = Guid.NewGuid(),
            DefaultEnabled = defaultEnabled,
            Resources =
            [
                new() { Resource = "api", State = "watching" },
                new() { Resource = "worker", State = "pending" },
                new() { Resource = "build", State = "build-failed" },
                new() { Resource = "ordinary", State = "excluded" },
                new() { Resource = "broken", State = "failed" },
                new() { Resource = "future", State = "new-protocol-state" }
            ]
        };

        Assert.Equal($"Resource watching (default {(defaultEnabled ? "on" : "off")}): 1 watching, 1 pending, 1 build failed, 1 excluded, 1 infrastructure failed, 1 unknown.",
            RunCommand.FormatResourceWatchStatus(status));
        Assert.Equal(RunCommandStrings.ResourceWatchUnknownSummary, RunCommand.FormatResourceWatchStatus(null));
    }

    [Fact]
    public async Task CoverageCaptureDeduplicatesWithinAGenerationButNeverReplaysAnOldGeneration()
    {
        var first = new ResourceWatchStatus
        {
            GenerationId = Guid.NewGuid(),
            DefaultEnabled = false,
            Resources = [new() { Resource = "selected", State = "watching" }]
        };
        var replacement = new ResourceWatchStatus
        {
            GenerationId = Guid.NewGuid(),
            DefaultEnabled = false,
            Resources = []
        };
        var backchannel = new TestAppHostBackchannel
        {
            WatchResourceWatchStatusAsyncCallback = _ => new ResourceWatchStatus?[] { first, first, replacement, null }.ToAsyncEnumerable()
        };
        var interaction = new TestInteractionService();

        await RunCommand.CaptureResourceWatchStatusAsync(backchannel, interaction, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            RunCommand.FormatResourceWatchStatus(first),
            RunCommand.FormatResourceWatchStatus(replacement),
            RunCommandStrings.ResourceWatchUnknownSummary
        ], interaction.DisplayedMarkup);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("exit")]
    [InlineData("zero-exit")]
    [InlineData("run-fault")]
    [InlineData("backchannel-fault")]
    [InlineData("rpc-fault")]
    public async Task WatcherOwnedFailuresKeepRawStreamsAndExceptionMessagesOutOfTheTerminal(string failure)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var collector = new OutputCollector();
        const string raw = "RAW WATCHER OUTPUT\nProgram.cs: compiler diagnostic [red]\u001b[31m";
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.InteractionServiceFactory = _ => interaction;
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                RunAsyncCallback = async (context, token) =>
                {
                    context.OutputCollector = collector;
                    collector.MarkWatcherOutput();
                    collector.AppendOutput(raw);
                    collector.AppendError(raw);
                    context.BuildCompletionSource!.TrySetResult(failure != "build");
                    if (failure is "build" or "exit")
                    {
                        return 17;
                    }
                    if (failure == "zero-exit")
                    {
                        return 0;
                    }
                    if (failure == "run-fault")
                    {
                        throw new InvalidOperationException(raw);
                    }
                    if (failure == "backchannel-fault")
                    {
                        context.BackchannelCompletionSource!.TrySetException(new InvalidOperationException(raw));
                    }
                    else
                    {
                        context.BackchannelCompletionSource!.TrySetResult(new TestAppHostBackchannel
                        {
                            GetDashboardUrlsAsyncCallback = _ => Task.FromException<DashboardUrlsState>(new InvalidOperationException(raw))
                        });
                    }
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 17;
                }
            };
        });
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<RootCommand>().Parse("run --watch false").InvokeAsync().DefaultTimeout();

        Assert.NotEqual(0, result);
        Assert.Empty(interaction.DisplayedLines);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture,
            RunCommandStrings.ProjectCouldNotBeRun, provider.GetRequiredService<FileLoggerProvider>().LogFilePath),
            Assert.Single(interaction.DisplayedErrors));
        Assert.Collection(collector.GetLines(),
            line => Assert.Equal((OutputLineStream.StdOut, raw), line),
            line => Assert.Equal((OutputLineStream.StdErr, raw), line));
    }

    [Fact]
    public async Task AWatchRequestDoesNotSuppressAnOrdinaryLaunchFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.InteractionServiceFactory = _ => interaction;
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                RunAsyncCallback = (context, _) =>
                {
                    context.OutputCollector = new OutputCollector();
                    context.BuildCompletionSource!.TrySetResult(true);
                    return Task.FromException<int>(new InvalidOperationException("Ordinary AppHost startup diagnostic"));
                }
            };
        });
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<RootCommand>().Parse("run --watch").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, result);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture,
            InteractionServiceStrings.UnexpectedErrorOccurred, "Ordinary AppHost startup diagnostic"),
            Assert.Single(interaction.DisplayedErrors));
    }
}
