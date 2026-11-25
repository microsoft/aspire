// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Aspire.Cli.Commands;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Cli.Watch;
using Aspire.Hosting;
using Aspire.Shared;
using Aspire.Shared.DotnetWatch;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Tests.Commands;

public class RunCommandWatchTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CtrlRReplacesAnActiveAppHostInsteadOfReloadingServiceGroups()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var directory = new DotnetWatchTestDirectory();
        using var shutdown = new TestGracefulShutdownWindow();
        using var cancellation = new CancellationTokenSource();
        await using var generation = new DotnetWatchAppHostGeneration(
            shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, NullLogger.Instance);
        var input = new TestConsoleInput();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serviceReloads = 0;
        var backchannel = new TestAppHostBackchannel
        {
            NotifyAppHostReadyAsyncCalled = ready,
            GetCapabilitiesAsyncCallback = _ => Task.FromResult(new[] { ResourceWatch.ReloadCapability }),
            WatchResourceWatchStatusAsyncCallback = _ => new ResourceWatchStatus?[]
            {
                new()
                {
                    GenerationId = Guid.NewGuid(),
                    DefaultEnabled = true,
                    Resources = [new() { Resource = "api", State = "watching", LogResource = "dotnet-watch" }]
                }
            }.ToAsyncEnumerable(),
            ReloadWatchedResourcesAsyncCallback = _ =>
            {
                Interlocked.Increment(ref serviceReloads);
                return Task.FromResult(new ResourceWatchReloadResult { Groups = [] });
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.ConsoleInputFactory = _ => input;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => new TestInteractionService();
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                RunAsyncCallback = async (context, token) =>
                {
                    generation.ApplyEnvironment(new Dictionary<string, string>());
                    await AnnounceAsync(generation, "123:456", token);
                    context.BuildCompletionSource!.TrySetResult(true);
                    context.BackchannelCompletionSource!.TrySetResult(backchannel);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 0;
                }
            };
        });
        using var provider = services.BuildServiceProvider();
        var run = provider.GetRequiredService<RunCommand>().ExecuteGenerationAsync(
            provider.GetRequiredService<RootCommand>().Parse("run --watch"),
            watch: true, watchExplicit: true, generation, cancellation.Token);
        try
        {
            await ready.Task.DefaultTimeout();
            input.PressReload();
            await generation.Invalidated.DefaultTimeout();

            Assert.True(generation.ReloadRequested);
            Assert.Equal(0, serviceReloads);
        }
        finally
        {
            await cancellation.CancelAsync();
            Assert.Equal(CliExitCodes.Success, (await run.DefaultTimeout()).ExitCode);
        }
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task OnlyInteractiveReplacementPrebuildFailuresWaitForCtrlR(
        bool replacement, bool interactive, bool startedByReload)
    {
        using var fixture = new TelemetryFixture();
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var shutdown = new TestGracefulShutdownWindow();
        using var cancellation = new CancellationTokenSource();
        await using var generation = new DotnetWatchAppHostGeneration(
            shutdown, TimeSpan.FromSeconds(5), ".", NullLogger.Instance)
        {
            IsReplacement = replacement,
            StartedByReload = startedByReload,
            PendingReload = startedByReload ? RunReloadActivity.Start(fixture.Telemetry, parent: null, RunReloadMode.AppHost) : null
        };
        var input = new TestConsoleInput { IsInputRedirected = !interactive };
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interaction = new TestInteractionService
        {
            DisplayErrorCallback = text =>
            {
                if (text == RunCommandStrings.AppHostBuildFailedWaitingForReload)
                {
                    waiting.TrySetResult();
                }
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.ConsoleInputFactory = _ => input;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interaction;
            options.TelemetryFactory = _ => fixture.Telemetry;
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                RunAsyncCallback = (context, _) =>
                {
                    context.OutputCollector = new OutputCollector();
                    context.OutputCollector.AppendError("Program.cs(1,1): error CS1002: ; expected");
                    context.BuildCompletionSource!.TrySetResult(false);
                    return Task.FromResult(CliExitCodes.FailedToBuildArtifacts);
                }
            };
        });
        using var provider = services.BuildServiceProvider();
        var run = provider.GetRequiredService<RunCommand>().ExecuteGenerationAsync(
            provider.GetRequiredService<RootCommand>().Parse("run --watch"), true, true, generation, cancellation.Token);
        CommandResult result;
        try
        {
            if (replacement && interactive)
            {
                await waiting.Task.DefaultTimeout();
                Assert.False(run.IsCompleted);
                Assert.Equal([RunCommandStrings.AppHostBuildFailedWaitingForReload], interaction.DisplayedErrors);
                input.PressReload();
                await generation.Invalidated.DefaultTimeout();
                await cancellation.CancelAsync();
            }
            result = await run.DefaultTimeout();
        }
        finally
        {
            await cancellation.CancelAsync();
            await run.DefaultTimeout();
        }

        Assert.Equal(replacement && interactive ? CliExitCodes.Success : CliExitCodes.FailedToBuildArtifacts, result.ExitCode);
        Assert.Equal([(OutputLineStream.StdErr, "Program.cs(1,1): error CS1002: ; expected")], interaction.DisplayedLines);
        var runActivity = Assert.Single(fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHost));
        Assert.Equal("build_failed", runActivity.GetTagItem(TelemetryConstants.Tags.ErrorType));
        // The press while waiting for a fix starts a reload that this test's missing session never completes.
        var reloads = fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHostReload);
        if (startedByReload)
        {
            var reload = Assert.Single(reloads);
            Assert.Equal(TelemetryConstants.RunReloadOutcomes.BuildFailed, reload.GetTagItem(TelemetryConstants.Tags.RunReloadOutcome));
            Assert.False((bool?)reload.GetTagItem(TelemetryConstants.Tags.RunReloadSuccess));
        }
        else
        {
            Assert.Empty(reloads);
        }
        Assert.Equal(replacement && interactive, generation.RequestedReload is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementStartupHasNoDeadlineAndInteractiveCtrlRCanRecoverAHang(bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var directory = new DotnetWatchTestDirectory();
        using var shutdown = new TestGracefulShutdownWindow();
        using var cancellation = new CancellationTokenSource();
        await using var generation = new DotnetWatchAppHostGeneration(
            shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, NullLogger.Instance) { IsReplacement = true };
        var time = new FakeTimeProvider();
        var input = new TestConsoleInput { IsInputRedirected = !interactive };
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interaction = new TestInteractionService
        {
            ShowDynamicStatusCallback = _ => connecting.TrySetResult()
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.ConsoleInputFactory = _ => input;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interaction;
            options.TimeProvider = time;
            options.ConfigurationCallback += values => values[CliConfigNames.AppHostStartupTimeout] = "1";
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                RunAsyncCallback = async (context, token) =>
                {
                    // As the AppHost project does: build completion is signaled before the generation channel opens.
                    // Open it only once the command waits for startup, after it observed the build.
                    context.BuildCompletionSource!.TrySetResult(true);
                    await connecting.Task.WaitAsync(token);
                    generation.ApplyEnvironment(new Dictionary<string, string>());
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 0;
                }
            };
        });
        using var provider = services.BuildServiceProvider();
        var run = provider.GetRequiredService<RunCommand>().ExecuteGenerationAsync(
            provider.GetRequiredService<RootCommand>().Parse("run --watch"), true, true, generation, cancellation.Token);
        try
        {
            await connecting.Task.DefaultTimeout();
            time.Advance(TimeSpan.FromHours(1));
            Assert.False(run.IsCompleted);
            await generation.Enabled.DefaultTimeout();
            if (interactive)
            {
                // The shortcut polls the console on the fake clock, so keep the clock ticking. Enabling the shortcut
                // first discards the keys buffered during startup and only then starts polling, so a key pressed after
                // the first poll is read as a request.
                await AsyncTestHelpers.AssertIsTrueRetryAsync(() =>
                {
                    time.Advance(TimeSpan.FromMilliseconds(50));
                    return input.KeyAvailableChecks > 1;
                }, "The reload shortcut polled the console.");
                input.PressReload();
                await AsyncTestHelpers.AssertIsTrueRetryAsync(() =>
                {
                    time.Advance(TimeSpan.FromMilliseconds(50));
                    return generation.Invalidated.IsCompleted;
                }, "Ctrl+R requested a reload.");
                Assert.True(generation.ReloadRequested);
            }
            else
            {
                Assert.Equal(0, input.ReadCount);
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            Assert.Equal(CliExitCodes.Success, (await run.DefaultTimeout()).ExitCode);
        }
        Assert.Empty(interaction.DisplayedErrors);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task FailedReplacementWaitsForASavedChildOrManualReload(bool interactive, bool manualRetry)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var directory = new DotnetWatchTestDirectory();
        using var shutdown = new TestGracefulShutdownWindow();
        using var cancellation = new CancellationTokenSource();
        await using var generation = new DotnetWatchAppHostGeneration(
            shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, NullLogger.Instance) { IsReplacement = true };
        var input = new TestConsoleInput { IsInputRedirected = !interactive };
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interaction = new TestInteractionService
        {
            ShowDynamicStatusCallback = text =>
            {
                if (text == (interactive ? RunCommandStrings.WatchAppHostWaitingForFix : RunCommandStrings.WatchAppHostWaitingForFixWithoutInput))
                {
                    waiting.TrySetResult();
                }
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.ConsoleInputFactory = _ => input;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interaction;
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                RunAsyncCallback = async (context, token) =>
                {
                    generation.ApplyEnvironment(new Dictionary<string, string>());
                    await AnnounceAsync(generation, "123:456", token);
                    context.OutputCollector = new OutputCollector();
                    context.OutputCollector.MarkWatcherOutput();
                    context.OutputCollector.AppendError("RAW WATCHER STARTUP ERROR");
                    context.BuildCompletionSource!.TrySetResult(true);
                    generation.MarkStartupFailed();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 0;
                }
            };
        });
        using var provider = services.BuildServiceProvider();
        var run = provider.GetRequiredService<RunCommand>().ExecuteGenerationAsync(
            provider.GetRequiredService<RootCommand>().Parse("run --watch"), true, true, generation, cancellation.Token);
        try
        {
            await waiting.Task.DefaultTimeout();
            Assert.False(run.IsCompleted);
            Assert.Equal([string.Format(System.Globalization.CultureInfo.CurrentCulture, RunCommandStrings.ProjectCouldNotBeRun,
                provider.GetRequiredService<FileLoggerProvider>().LogFilePath)], interaction.DisplayedErrors);
            Assert.Empty(interaction.DisplayedLines);
            if (manualRetry)
            {
                input.PressReload();
            }
            else
            {
                await AnnounceAsync(generation, "123:789", TestContext.Current.CancellationToken);
            }
            await generation.Invalidated.DefaultTimeout();
            Assert.Equal(manualRetry, generation.ReloadRequested);
        }
        finally
        {
            await cancellation.CancelAsync();
            Assert.Equal(CliExitCodes.Success, (await run.DefaultTimeout()).ExitCode);
        }
    }

    private static Task AnnounceAsync(DotnetWatchAppHostGeneration generation, string instance, CancellationToken token) =>
        DotnetWatchAppHostTestChannel.SendAsync(generation.Endpoint, new()
        {
            Version = DotnetWatchAppHostProtocol.Version,
            Session = generation.Session,
            Kind = DotnetWatchAppHostProtocol.HostStarted,
            Instance = instance
        }, token);

    [Fact]
    public async Task ServicesThatAppearAfterStartupAddOneReloadHintAndUseTheOwnerRpc()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var input = new TestConsoleInput();
        var statuses = Channel.CreateUnbounded<ResourceWatchStatus?>();
        var initialHint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reloadHint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rebuilt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renderables = 0;
        var interaction = new TestInteractionService
        {
            DisplayRenderableCallback = _ =>
            {
                var count = Interlocked.Increment(ref renderables);
                if (count == 2)
                {
                    initialHint.TrySetResult();
                }
                else if (count == 3)
                {
                    reloadHint.TrySetResult();
                }
            },
            DisplaySuccessCallback = _ => rebuilt.TrySetResult()
        };
        var requests = 0;
        var backchannel = new TestAppHostBackchannel
        {
            GetDashboardUrlsAsyncCallback = _ => Task.FromResult(new DashboardUrlsState
            {
                DashboardHealthy = true,
                CodespacesUrlWithLoginToken = null,
                BaseUrlWithLoginToken = null
            }),
            GetCapabilitiesAsyncCallback = _ => Task.FromResult(new[] { ResourceWatch.ReloadCapability }),
            WatchResourceWatchStatusAsyncCallback = token => statuses.Reader.ReadAllAsync(token),
            ReloadWatchedResourcesAsyncCallback = _ =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(new ResourceWatchReloadResult
                {
                    Groups = [new() { LogResource = "dotnet-watch", Resources = ["api"], Success = true, Message = "Rebuilt 'api'." }]
                });
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.ConsoleInputFactory = _ => input;
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interaction;
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                RunAsyncCallback = async (context, token) =>
                {
                    context.BuildCompletionSource!.TrySetResult(true);
                    context.BackchannelCompletionSource!.TrySetResult(backchannel);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 0;
                }
            };
        });
        using var provider = services.BuildServiceProvider();
        var run = provider.GetRequiredService<RootCommand>().Parse("run --watch false").InvokeAsync(cancellationToken: cancellation.Token);
        try
        {
            await initialHint.Task.DefaultTimeout();
            var status = new ResourceWatchStatus
            {
                GenerationId = Guid.NewGuid(),
                DefaultEnabled = true,
                Resources = [new() { Resource = "api", State = "pending", LogResource = "dotnet-watch" }]
            };
            await statuses.Writer.WriteAsync(status, TestContext.Current.CancellationToken);
            await reloadHint.Task.DefaultTimeout();
            await statuses.Writer.WriteAsync(status, TestContext.Current.CancellationToken);
            statuses.Writer.TryComplete();
            input.PressReload();
            await rebuilt.Task.DefaultTimeout();
        }
        finally
        {
            await cancellation.CancelAsync();
            Assert.Equal(CliExitCodes.Success, await run.DefaultTimeout());
        }

        Assert.Equal(3, renderables);
        Assert.Equal(1, requests);
        Assert.Equal(["Rebuilt 'api'."], interaction.DisplayedSuccess);
    }

    [Fact]
    public async Task LateWatchGroupsUpdateTheRemoteLiveHintWithoutAppendingADuplicate()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var statuses = Channel.CreateUnbounded<ResourceWatchStatus?>();
        var resources = Channel.CreateUnbounded<RpcResourceState>();
        var firstHint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reloadHint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endpointRendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveOutput = new ConcurrentQueue<string>();
        var interaction = new TestInteractionService
        {
            DisplayLiveRenderableCallback = renderable =>
            {
                var text = Render(renderable);
                liveOutput.Enqueue(text);
                firstHint.TrySetResult();
                if (text.Contains("CTRL+R", StringComparison.Ordinal))
                {
                    reloadHint.TrySetResult();
                }
                if (text.Contains("api has endpoint", StringComparison.Ordinal))
                {
                    endpointRendered.TrySetResult();
                }
            }
        };
        var backchannel = new TestAppHostBackchannel
        {
            GetDashboardUrlsAsyncCallback = _ => Task.FromResult(new DashboardUrlsState
            {
                DashboardHealthy = true,
                CodespacesUrlWithLoginToken = null,
                BaseUrlWithLoginToken = null
            }),
            GetCapabilitiesAsyncCallback = _ => Task.FromResult(new[] { ResourceWatch.ReloadCapability }),
            WatchResourceWatchStatusAsyncCallback = token => statuses.Reader.ReadAllAsync(token),
            GetResourceStatesAsyncCallback = token => resources.Reader.ReadAllAsync(token)
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.ConsoleInputFactory = _ => new TestConsoleInput();
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interaction;
            options.ConfigurationCallback += values => values["REMOTE_CONTAINERS"] = "true";
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                RunAsyncCallback = async (context, token) =>
                {
                    context.BuildCompletionSource!.TrySetResult(true);
                    context.BackchannelCompletionSource!.TrySetResult(backchannel);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 0;
                }
            };
        });
        using var provider = services.BuildServiceProvider();
        var run = provider.GetRequiredService<RootCommand>().Parse("run --watch false").InvokeAsync(cancellationToken: cancellation.Token);
        try
        {
            await firstHint.Task.DefaultTimeout();
            await statuses.Writer.WriteAsync(new ResourceWatchStatus
            {
                GenerationId = Guid.NewGuid(),
                DefaultEnabled = true,
                Resources = [new() { Resource = "api", State = "pending", LogResource = "dotnet-watch" }]
            }, TestContext.Current.CancellationToken);
            await reloadHint.Task.DefaultTimeout();
            await resources.Writer.WriteAsync(new RpcResourceState
            {
                Resource = "api",
                Type = "Project",
                State = "Running",
                Health = "Healthy",
                Endpoints = ["http://localhost:5000"]
            }, TestContext.Current.CancellationToken);
            await endpointRendered.Task.DefaultTimeout();
        }
        finally
        {
            statuses.Writer.TryComplete();
            resources.Writer.TryComplete();
            await cancellation.CancelAsync();
            Assert.Equal(CliExitCodes.Success, await run.DefaultTimeout());
        }

        Assert.Single(interaction.DisplayedRenderables);
        await Verify(liveOutput.Distinct(StringComparer.Ordinal).ToArray())
            .ScrubLinesWithReplace(line => line.TrimEnd());

        static string Render(IRenderable renderable)
        {
            using var output = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                Interactive = InteractionSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(output),
                Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
            });
            console.Profile.Width = 200;
            console.Profile.Capabilities.Links = false;
            console.Write(renderable);
            return output.ToString().Trim();
        }
    }

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
