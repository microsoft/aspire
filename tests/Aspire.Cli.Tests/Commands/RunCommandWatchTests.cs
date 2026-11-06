// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.DotNet;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using StreamJsonRpc;

namespace Aspire.Cli.Tests.Commands;

public class RunCommandWatchTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("run --watch", true, 0)]
    [InlineData("run --watch -- --watch", true, 1)]
    [InlineData("run -- --watch", false, 1)]
    [InlineData("run", false, 0)]
    public void OnlyTheExplicitRunOptionSelectsExperimentalWatch(string arguments, bool expected, int forwardedCount)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        var parsed = provider.GetRequiredService<RootCommand>().Parse(arguments);

        Assert.Empty(parsed.Errors);
        Assert.Equal(expected, parsed.GetValue(RunCommand.s_watchOption));
        Assert.Equal(forwardedCount, parsed.UnmatchedTokens.Count);
    }

    [Theory]
    [InlineData("--no-build", null)]
    [InlineData("--wait-for-debugger", null)]
    [InlineData("", KnownConfigNames.ExtensionDebugSessionId)]
    [InlineData("", KnownConfigNames.DebugSessionPort)]
    [InlineData("", KnownConfigNames.DebugSessionInfo)]
    [InlineData("", KnownConfigNames.DebugSessionRunMode)]
    public async Task WatchRejectsIncompatibleOwnershipBeforeLocatingOrStoppingAnAppHost(string argument, string? ownershipKey)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                    throw new InvalidOperationException("Incompatible watch options must fail before project discovery.")
            };
            if (ownershipKey is not null)
            {
                options.ConfigurationCallback += values => values[ownershipKey] = "owned";
            }
        }).BuildServiceProvider();
        var parsed = provider.GetRequiredService<RootCommand>().Parse($"run --watch {argument}");

        var exitCode = await parsed.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Contains(argument == "--no-build"
            ? RunCommandStrings.NoBuildNotSupportedWithWatchMode
            : RunCommandStrings.WatchNotSupportedWithDebugger, interaction.DisplayedErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitWatchContextIsSeparateFromLegacyDefaultWatching(bool legacyDefault)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var appHost = new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHost.FullName, "<Project />");
        var started = new TaskCompletionSource<AppHostProjectContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interaction = new TestInteractionService();
        var project = new TestAppHostProjectFactory
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
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.AppHostProjectFactory = _ => project;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                    Task.FromResult(new AppHostProjectSearchResult(appHost, [appHost]))
            };
            options.ConfigurationCallback += values => values["features:defaultWatchEnabled"] = legacyDefault.ToString();
        }).BuildServiceProvider();

        var run = provider.GetRequiredService<RootCommand>().Parse("run --watch --non-interactive").InvokeAsync(cancellationToken: cancellation.Token);
        var context = await started.Task.DefaultTimeout();
        await cancellation.CancelAsync();
        Assert.Equal(CliExitCodes.Success, await run.DefaultTimeout());

        Assert.True(context.Watch);
        Assert.NotNull(context.WatchGeneration);
        Assert.Contains(interaction.DisplayedMessages, message => message.Message == RunCommandStrings.ExperimentalWatchNotice);
    }

    [Fact]
    public async Task DetachedWatchForwardsTheOptionWithoutConsumingAppHostArguments()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHost.FullName, "<Project />");
        var processes = new TestProcessExecutionFactory { DefaultExitCode = CliExitCodes.FailedToDotnetRunAppHost };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                    Task.FromResult(new AppHostProjectSearchResult(appHost, [appHost]))
            };
        });
        services.Replace(ServiceDescriptor.Singleton<IProcessExecutionFactory>(processes));
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var arguments = new[] { "run", "--watch", "--detach", "--apphost", appHost.FullName, "--", "--watch", "two words", "" };

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, await command.Parse(arguments).InvokeAsync().DefaultTimeout());
        var childArguments = Assert.IsType<string[]>(processes.LastArguments);
        var runIndex = Array.IndexOf(childArguments, "run");
        Assert.True(runIndex >= 0);
        var child = command.Parse(childArguments[runIndex..]);

        Assert.Empty(child.Errors);
        Assert.True(child.GetValue(RunCommand.s_watchOption));
        Assert.Equal(["--watch", "two words", ""], child.UnmatchedTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelInvalidationReplacesCompletionSourcesAndRefreshesBackchannelStartup(bool upstreamRestart)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var appHost = new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHost.FullName, "<Project />");
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resourcesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contexts = new ConcurrentQueue<AppHostProjectContext>();
        var stopped = 0;
        var project = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, token) =>
            {
                var previousStopped = Volatile.Read(ref stopped);
                contexts.Enqueue(context);
                Assert.Equal(contexts.Count - 1, previousStopped);
                var generation = Assert.IsType<Aspire.Cli.Watch.DotnetWatchAppHostGeneration>(context.WatchGeneration);
                generation.ApplyEnvironment(context.EnvironmentVariables);
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(new TestAppHostBackchannel
                {
                    NotifyAppHostReadyAsyncCalled = contexts.Count == 1 ? firstReady : secondReady,
                    GetResourceStatesAsyncCalled = contexts.Count == 1 ? resourcesStarted : null,
                    GetResourceStatesAsyncCallback = contexts.Count == 1 && upstreamRestart
                        ? token => DisconnectResourceStreamAsync(disconnect.Task, token)
                        : null
                });
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    Interlocked.Increment(ref stopped);
                }
                return 0;
            }
        };
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AppHostProjectFactory = _ => project;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                    Task.FromResult(new AppHostProjectSearchResult(appHost, [appHost]))
            };
        }).BuildServiceProvider();
        var run = provider.GetRequiredService<RootCommand>().Parse("run --watch --non-interactive").InvokeAsync(cancellationToken: cancellation.Token);
        try
        {
            await firstReady.Task.DefaultTimeout();
            var first = Assert.Single(contexts);
            if (upstreamRestart)
            {
                await resourcesStarted.Task.DefaultTimeout();
                await using var started = new NamedPipeClientStream(".", first.EnvironmentVariables[DotnetWatchConfiguration.HostControlPipe],
                    PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await started.ConnectAsync(TestContext.Current.CancellationToken).DefaultTimeout();
                await DotnetWatchHostProtocol.WriteAsync(started, new()
                {
                    Session = first.EnvironmentVariables[DotnetWatchConfiguration.HostSession],
                    Kind = DotnetWatchHostProtocol.HostStarted,
                    Instance = "first-process"
                }, TestContext.Current.CancellationToken);
                disconnect.TrySetResult();
            }
            await using var control = new NamedPipeClientStream(".", first.EnvironmentVariables[DotnetWatchConfiguration.HostControlPipe],
                PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await control.ConnectAsync(TestContext.Current.CancellationToken).DefaultTimeout();
            await DotnetWatchHostProtocol.WriteAsync(control, new()
            {
                Session = first.EnvironmentVariables[DotnetWatchConfiguration.HostSession],
                Kind = upstreamRestart ? DotnetWatchHostProtocol.HostStarted : DotnetWatchHostProtocol.ModelInvalidated,
                Instance = upstreamRestart ? "replacement-process" : null
            }, TestContext.Current.CancellationToken);
            await secondReady.Task.DefaultTimeout();
        }
        finally
        {
            await cancellation.CancelAsync();
            Assert.Equal(CliExitCodes.Success, await run.DefaultTimeout());
        }

        var generations = contexts.ToArray();
        Assert.Equal(2, generations.Length);
        Assert.NotSame(generations[0].BuildCompletionSource, generations[1].BuildCompletionSource);
        Assert.NotSame(generations[0].BackchannelCompletionSource, generations[1].BackchannelCompletionSource);
        Assert.Equal(2, stopped);
    }

    [Fact]
    public async Task DetachedWatchDoesNotRearmExitedLauncherAfterInitialBackchannelEstablished()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var launcher = TestProcesses.StartLongRunning();
        var appHost = new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHost.FullName, "<Project />", cancellation.Token);
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPreparing = new TaskCompletionSource<AppHostProjectContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSecondStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstContext = new TaskCompletionSource<AppHostProjectContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generationCount = 0;
        var logger = new FakeLogger<RunCommand>();
        var project = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, token) =>
            {
                var index = Interlocked.Increment(ref generationCount);
                var generation = Assert.IsType<Aspire.Cli.Watch.DotnetWatchAppHostGeneration>(context.WatchGeneration);
                generation.ApplyEnvironment(context.EnvironmentVariables);
                if (index == 1)
                {
                    firstContext.TrySetResult(context);
                }
                else
                {
                    secondPreparing.TrySetResult(context);
                    await allowSecondStartup.Task.WaitAsync(token);
                }

                generation.MarkBackchannelEstablished();
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(new TestAppHostBackchannel
                {
                    NotifyAppHostReadyAsyncCalled = index == 1 ? firstReady : secondReady
                });
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.AppHostProjectFactory = _ => project;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                    Task.FromResult(new AppHostProjectSearchResult(appHost, [appHost]))
            };
            options.ConfigurationCallback += values =>
            {
                values[KnownConfigNames.CliRunDetached] = "true";
                values[KnownConfigNames.CliLauncherProcessId] = launcher.Id.ToString(CultureInfo.InvariantCulture);
                values[KnownConfigNames.CliLauncherProcessStarted] =
                    ProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(launcher.Id)?.ToString(CultureInfo.InvariantCulture);
            };
        });
        services.AddSingleton<ILogger<RunCommand>>(logger);
        using var provider = services.BuildServiceProvider();
        var run = provider.GetRequiredService<RootCommand>().Parse("run --watch --non-interactive").InvokeAsync(cancellationToken: cancellation.Token);
        try
        {
            await firstReady.Task.DefaultTimeout();
            launcher.Kill(entireProcessTree: true);
            await launcher.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
            var first = await firstContext.Task.DefaultTimeout();
            await using var control = new NamedPipeClientStream(".", first.EnvironmentVariables[DotnetWatchConfiguration.HostControlPipe],
                PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await control.ConnectAsync(TestContext.Current.CancellationToken).DefaultTimeout();
            await DotnetWatchHostProtocol.WriteAsync(control, new()
            {
                Session = first.EnvironmentVariables[DotnetWatchConfiguration.HostSession]
            }, TestContext.Current.CancellationToken);

            var next = await secondPreparing.Task.DefaultTimeout();
            Assert.False(next.WatchGeneration!.RequiresLauncherMonitoring);
            // Monitor creation logs synchronously before project preparation begins. Checking here,
            // while preparation is held, catches rearming without racing its asynchronous PID probe.
            Assert.Single(logger.Collector.GetSnapshot(), record =>
                record.Message.StartsWith("Detached child: watching launcher process", StringComparison.Ordinal));
            allowSecondStartup.TrySetResult();
            await secondReady.Task.DefaultTimeout();
        }
        finally
        {
            allowSecondStartup.TrySetResult();
            await cancellation.CancelAsync();
            if (!launcher.HasExited)
            {
                launcher.Kill(entireProcessTree: true);
                await launcher.WaitForExitAsync(TestContext.Current.CancellationToken).DefaultTimeout();
            }
            await run.DefaultTimeout();
        }

        Assert.Equal(CliExitCodes.Success, await run);
        Assert.Equal(2, generationCount);
    }

    private static async IAsyncEnumerable<RpcResourceState> DisconnectResourceStreamAsync(Task disconnect, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new RpcResourceState { Resource = "old-resource", Type = "Executable", State = "Running", Health = "Healthy", Endpoints = [] };
        await disconnect.WaitAsync(cancellationToken);
        throw new ConnectionLostException();
    }
}
