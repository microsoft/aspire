// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Aspire.Cli.Interaction;
using Aspire.Cli.Utils;
using System.Text;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using StreamJsonRpc;

namespace Aspire.Cli.Tests.Commands;

public class TestCommandTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task JsonReportsLivePublishedResultsAndWaitsForCollection(bool keepAlive, bool cancelDuringCollection)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        using var cancellation = new CancellationTokenSource();
        var resourceFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCollection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inspecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? summaryPath = null;
        var stopRequested = false;
        async IAsyncEnumerable<TestRunEvent> Watch([EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TestRunEvent { Resource = new TestResourceState { ResourceName = "tests" } };
            yield return new TestRunEvent
            {
                Collection = new TestCollectionState { ResourceName = "tests", CollectorCount = 1, Status = "running" }
            };
            yield return new TestRunEvent
            {
                Result = new PublishedTestResult
                {
                    ResourceName = "tests", Name = "failed fixture", Outcome = "failed", DurationMilliseconds = 25,
                    Message = "fixture assertion"
                }
            };
            yield return new TestRunEvent
            {
                Resource = new TestResourceState { ResourceName = "tests", ResourceId = "tests", State = "Finished", ExitCode = 0, Outcome = "passed" }
            };
            resourceFinished.TrySetResult();
            await releaseCollection.Task.WaitAsync(ct);
            yield return new TestRunEvent
            {
                Collection = new TestCollectionState { ResourceName = "tests", CollectorCount = 1, Status = "completed" }
            };
            yield return new TestRunEvent { Passed = false };
        }
        var backchannel = new TestAppHostBackchannel
        {
            WatchTestRunAsyncCallback = Watch,
            WaitForDisconnectAsyncCalled = inspecting,
            RequestStopAsyncCallback = () =>
            {
                stopRequested = true;
                processStopped.TrySetResult();
                return Task.CompletedTask;
            }
        };
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = (context, ct) =>
            {
                summaryPath = Path.Combine(context.EnvironmentVariables[KnownConfigNames.TestOutputPath], "summary.json");
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                return AwaitStopAsync(ct);
            }
        };
        async Task<int> AwaitStopAsync(CancellationToken ct)
        {
            await processStopped.Task.WaitAsync(ct);
            return 0;
        }
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<IInteractionService>(CreateConsoleInteraction(workspace, stdout, stderr));
        using var provider = services.BuildServiceProvider();
        var arguments = new List<string> { "test", "--apphost", appHostFile.FullName, "--format", "json" };
        if (keepAlive)
        {
            arguments.Add("--keep-alive");
        }
        var execution = provider.GetRequiredService<RootCommand>().Parse(arguments.ToArray()).InvokeAsync(cancellationToken: cancellation.Token);

        await resourceFinished.Task.DefaultTimeout();
        Assert.False(execution.IsCompleted);
        Assert.False(stopRequested);
        Assert.Equal("", stdout.ToString());
        var live = JsonNode.Parse(File.ReadAllText(summaryPath!))!;
        Assert.Equal("running", live["status"]!.GetValue<string>());
        Assert.Equal("failed", live["testResults"]![0]!["outcome"]!.GetValue<string>());
        if (cancelDuringCollection)
        {
            cancellation.Cancel();
            Assert.Equal(CliExitCodes.Cancelled, await execution.DefaultTimeout());
        }
        else
        {
            releaseCollection.TrySetResult();
            if (keepAlive)
            {
                await inspecting.Task.DefaultTimeout();
                Assert.False(execution.IsCompleted);
                Assert.False(stopRequested);
                var completed = JsonNode.Parse(stdout.ToString())!;
                Assert.Equal("failed", completed["status"]!.GetValue<string>());
                Assert.Null(completed["cliExitCode"]);
                cancellation.Cancel();
                Assert.Equal(CliExitCodes.Cancelled, await execution.DefaultTimeout());
            }
            else
            {
                Assert.Equal(CliExitCodes.TestRunFailed, await execution.DefaultTimeout());
            }
        }
        var output = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal("passed", output["resources"]![0]!["outcome"]!.GetValue<string>());
        Assert.Equal((1, 0, 0), (output["publishedResults"]!["failed"]!.GetValue<int>(),
            output["publishedResults"]!["passed"]!.GetValue<int>(), output["publishedResults"]!["skipped"]!.GetValue<int>()));
        Assert.Equal("failed fixture", output["testResults"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(25, output["testResults"]![0]!["durationMilliseconds"]!.GetValue<int>());
        Assert.Equal(cancelDuringCollection ? "incomplete" : "completed", output["collections"]![0]!["status"]!.GetValue<string>());
        if (!keepAlive)
        {
            Assert.Equal(File.ReadAllText(summaryPath!), stdout.ToString().TrimEnd());
        }
    }

    [Theory]
    [InlineData(true, 0, 0, false)]
    [InlineData(false, 0, 23, false)]
    [InlineData(true, 130, 0, false)]
    [InlineData(true, 143, 0, false)]
    [InlineData(true, 137, 23, false)]
    [InlineData(true, 42, 23, false)]
    [InlineData(true, 0, 23, true)]
    [InlineData(false, 0, 23, true)]
    public async Task CompletionUsesTestOutcomeAndOrderlyShutdown(bool passed, int appHostExitCode, int expectedExitCode, bool artifactFailure)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopRequested = false;
        string? summaryPath = null;
        var backchannel = new TestAppHostBackchannel
        {
            WaitForTestCompletionAsyncCallback = ct =>
            {
                waiting.TrySetResult();
                return completion.Task.WaitAsync(ct);
            },
            RequestStopAsyncCallback = () =>
            {
                stopRequested = true;
                if (artifactFailure && !Directory.Exists(summaryPath!))
                {
                    File.Delete(summaryPath!);
                    Directory.CreateDirectory(summaryPath!);
                }
                stopped.TrySetResult(appHostExitCode);
                return Task.CompletedTask;
            }
        };
        AppHostProjectContext? launched = null;
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                launched = context;
                var outputPath = context.EnvironmentVariables[KnownConfigNames.TestOutputPath];
                summaryPath = Path.Combine(outputPath, "summary.json");
                Assert.True(Directory.Exists(outputPath));
                var summary = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(outputPath, "summary.json")))!;
                Assert.Equal("running", summary["status"]!.GetValue<string>());
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                return await stopped.Task.WaitAsync(ct);
            }
        };
        var services = CreateServices(workspace, appHostFile, factory);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var execution = command.Parse(["test", "--apphost", appHostFile.FullName, "--no-build"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        await waiting.Task.DefaultTimeout();
        Assert.False(stopRequested);
        Assert.False(execution.IsCompleted);
        completion.SetResult(passed);

        Assert.Equal(expectedExitCode, await execution.DefaultTimeout());
        Assert.True(stopRequested);
        Assert.NotNull(launched);
        Assert.True(launched.IsTestRun);
        Assert.True(launched.NoBuild);
        if (artifactFailure)
        {
            Assert.True(Directory.Exists(summaryPath));
        }
        else
        {
            var completedSummary = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(summaryPath!))!;
            Assert.Equal(expectedExitCode, completedSummary["cliExitCode"]!.GetValue<int>());
            Assert.Equal(expectedExitCode == 0 ? "passed" : "failed", completedSummary["status"]!.GetValue<string>());
        }
    }

    [Theory]
    [InlineData("old-host", 9, false)]
    [InlineData("no-tests", 2, false)]
    [InlineData("connection-lost", 2, false)]
    [InlineData("old-host", 9, true)]
    [InlineData("no-tests", 2, true)]
    [InlineData("connection-lost", 2, true)]
    public async Task FailureBeforeCompletionTearsDownTheAppHost(string failure, int expectedExitCode, bool json)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannel = new TestAppHostBackchannel
        {
            GetCapabilitiesAsyncCallback = _ => Task.FromResult(failure == "old-host" ? new[] { "baseline.v2" } : new[] { "test-reporting.v1" }),
            WaitForTestCompletionAsyncCallback = _ => failure == "no-tests"
                ? Task.FromException<bool>(new InvalidOperationException("No test resources were found."))
                : Task.FromException<bool>(new ConnectionLostException("AppHost disconnected."))
        };
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return 0;
                }
                finally
                {
                    tornDown.TrySetResult();
                }
            }
        };
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<IInteractionService>(interaction);
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse(["test", "--apphost", appHostFile.FullName, "--format", json ? "json" : "table"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(expectedExitCode, exitCode);
        await tornDown.Task.DefaultTimeout();
        var summaryPath = Assert.Single(Directory.GetFiles(workspace.CreateExecutionContext().AspireHomeDirectory.FullName,
            "summary.json", SearchOption.AllDirectories));
        var summary = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(summaryPath))!;
        Assert.Equal("failed", summary["status"]!.GetValue<string>());
        Assert.Equal(expectedExitCode, summary["cliExitCode"]!.GetValue<int>());
        if (json)
        {
            Assert.Equal(File.ReadAllText(summaryPath), Assert.Single(interaction.DisplayedRawText).Text);
        }
    }

    [Theory]
    [InlineData("test", 130, false, false)]
    [InlineData("test", 130, true, false)]
    [InlineData("test", 130, false, true)]
    [InlineData("test", 130, true, true)]
    [InlineData("run", 0, false, false)]
    public async Task CancellationPreservesNormalRunButDoesNotPassAnIncompleteTest(string commandName, int expectedExitCode, bool artifactFailure, bool json)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        using var cts = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannel = new TestAppHostBackchannel
        {
            WaitForTestCompletionAsyncCallback = async ct =>
            {
                ready.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            },
            GetDashboardUrlsAsyncCallback = _ =>
            {
                if (commandName == "run")
                {
                    ready.TrySetResult();
                }
                return Task.FromResult(new DashboardUrlsState { DashboardHealthy = false });
            }
        };
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                Assert.Equal(commandName == "test", context.IsTestRun);
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return 0;
                }
                finally
                {
                    if (artifactFailure)
                    {
                        var summaryPath = Path.Combine(context.EnvironmentVariables[KnownConfigNames.TestOutputPath], "summary.json");
                        File.Delete(summaryPath);
                        Directory.CreateDirectory(summaryPath);
                    }
                    tornDown.TrySetResult();
                }
            }
        };
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<IInteractionService>(interaction);
        using var provider = services.BuildServiceProvider();
        var execution = provider.GetRequiredService<RootCommand>().Parse([commandName, "--apphost", appHostFile.FullName, "--format", json ? "json" : "table"])
            .InvokeAsync(cancellationToken: cts.Token);

        await ready.Task.DefaultTimeout();
        cts.Cancel();

        Assert.Equal(expectedExitCode, await execution.DefaultTimeout());
        await tornDown.Task.DefaultTimeout();
        if (json)
        {
            var output = JsonNode.Parse(Assert.Single(interaction.DisplayedRawText).Text)!;
            Assert.Equal("cancelled", output["status"]!.GetValue<string>());
            Assert.Equal(expectedExitCode, output["cliExitCode"]!.GetValue<int>());
        }
        if (commandName == "test" && !artifactFailure)
        {
            var summaryPath = Assert.Single(Directory.GetFiles(workspace.CreateExecutionContext().AspireHomeDirectory.FullName,
                "summary.json", SearchOption.AllDirectories));
            var summary = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(summaryPath))!;
            Assert.Equal("cancelled", summary["status"]!.GetValue<string>());
            Assert.Equal(130, summary["cliExitCode"]!.GetValue<int>());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    public async Task AppHostExitingBeforeTestsCompleteFailsEvenWithZeroExitCode(int appHostExitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannel = new TestAppHostBackchannel
        {
            WaitForTestCompletionAsyncCallback = async ct =>
            {
                waiting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            }
        };
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                return await exit.Task.WaitAsync(ct);
            }
        };
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<Aspire.Cli.Interaction.IInteractionService>(interaction);
        using var provider = services.BuildServiceProvider();
        var execution = provider.GetRequiredService<RootCommand>().Parse(["test", "--apphost", appHostFile.FullName])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        await waiting.Task.DefaultTimeout();
        exit.SetResult(appHostExitCode);

        Assert.Equal(CliExitCodes.TestRunFailed, await execution.DefaultTimeout());
        Assert.Contains(TestCommandStrings.AppHostExitedEarly, interaction.DisplayedErrors);
    }

    [Theory]
    [InlineData("--detach")]
    [InlineData("--capture-profile")]
    public async Task IncompatibleOptionsFailBeforeLaunching(string options)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = (_, _) => throw new InvalidOperationException("Must not launch.")
        };
        using var provider = CreateServices(workspace, CreateAppHost(workspace), factory).BuildServiceProvider();

        Assert.Equal(CliExitCodes.InvalidCommand,
            await provider.GetRequiredService<RootCommand>().Parse($"test {options}").InvokeAsync().DefaultTimeout());
        Assert.False(Directory.Exists(Path.Combine(workspace.CreateExecutionContext().AspireHomeDirectory.FullName, "testruns")));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 23)]
    public async Task JsonOutputIsOneSummaryObjectAndMatchesThePersistedFile(bool passed, int expectedExitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var stopped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannel = new TestAppHostBackchannel
        {
            WaitForTestCompletionAsyncCallback = _ => Task.FromResult(passed),
            RequestStopAsyncCallback = () => { stopped.TrySetResult(0); return Task.CompletedTask; }
        };
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                return await stopped.Task.WaitAsync(ct);
            }
        };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var interaction = CreateConsoleInteraction(workspace, stdout, stderr);
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<IInteractionService>(interaction);
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse(["test", "--apphost", appHostFile.FullName, "--format", "json"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(expectedExitCode, exitCode);
        var json = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal(passed ? "passed" : "failed", json["status"]!.GetValue<string>());
        Assert.Equal(expectedExitCode, json["cliExitCode"]!.GetValue<int>());
        var summaryPath = json["summaryPath"]!.GetValue<string>();
        Assert.Equal(Path.Combine(json["outputPath"]!.GetValue<string>(), "summary.json"), summaryPath);
        Assert.Equal(File.ReadAllText(summaryPath), stdout.ToString().TrimEnd());
        var resource = Assert.Single(json["resources"]!.AsArray())!;
        Assert.Equal(passed ? 0 : 1, resource["exitCode"]!.GetValue<int>());
        Assert.Equal(passed ? "passed" : "failed", resource["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnsupportedJsonOptionsProduceAnErrorObjectWithoutCreatingTestArtifacts()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var factory = new TestAppHostProjectFactory { RunAsyncCallback = (_, _) => throw new InvalidOperationException("Must not launch.") };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<IInteractionService>(CreateConsoleInteraction(workspace, stdout, stderr));
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse(["test", "--format", "json", "--detach"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        var json = JsonNode.Parse(stdout.ToString())!;
        Assert.Equal(CliExitCodes.InvalidCommand, json["cliExitCode"]!.GetValue<int>());
        Assert.Equal(TestCommandStrings.IncompatibleOptions, json["error"]!.GetValue<string>());
        Assert.Null(json["outputPath"]);
        Assert.Null(json["summaryPath"]);
        Assert.False(Directory.Exists(Path.Combine(workspace.CreateExecutionContext().AspireHomeDirectory.FullName, "testruns")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task KeepAliveReportsCompletionOnceAndHoldsBothProcessesUntilCancellation(bool passed, bool json)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        using var cancellation = new CancellationTokenSource();
        var summaryShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionQueries = 0;
        var stopRequested = false;
        string? summaryPath = null;
        var backchannel = new TestAppHostBackchannel
        {
            WaitForDisconnectAsyncCalled = json ? summaryShown : null,
            WaitForTestCompletionAsyncCallback = _ => { completionQueries++; return Task.FromResult(passed); },
            RequestStopAsyncCallback = () => { stopRequested = true; return Task.CompletedTask; }
        };
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                summaryPath = Path.Combine(context.EnvironmentVariables[KnownConfigNames.TestOutputPath], "summary.json");
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return 0;
                }
                finally
                {
                    tornDown.TrySetResult();
                }
            }
        };
        var summaryMessages = 0;
        var interaction = new TestInteractionService
        {
            DisplaySubtleMessageCallback = message =>
            {
                if (!json && message.StartsWith(TestCommandStrings.SummaryFile.Replace("{0}", "", StringComparison.Ordinal), StringComparison.Ordinal))
                {
                    summaryMessages++;
                    summaryShown.TrySetResult();
                }
            },
            DisplayRawTextCallback = _ => { summaryMessages++; summaryShown.TrySetResult(); }
        };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<IInteractionService>(json ? CreateConsoleInteraction(workspace, stdout, stderr) : interaction);
        services.AddSingleton<ICliHostEnvironment>(TestHelpers.CreateInteractiveHostEnvironment());
        using var provider = services.BuildServiceProvider();
        var execution = provider.GetRequiredService<RootCommand>()
            .Parse(["test", "--apphost", appHostFile.FullName, "--keep-alive", "--format", json ? "json" : "table"])
            .InvokeAsync(cancellationToken: cancellation.Token);

        await summaryShown.Task.DefaultTimeout();
        Assert.False(execution.IsCompleted);
        Assert.False(tornDown.Task.IsCompleted);
        Assert.False(stopRequested);
        Assert.Equal(1, completionQueries);
        var beforeStop = JsonNode.Parse(File.ReadAllText(summaryPath!))!;
        Assert.Equal(passed ? "passed" : "failed", beforeStop["status"]!.GetValue<string>());
        Assert.NotNull(beforeStop["completedAt"]);
        Assert.Null(beforeStop["cliExitCode"]);
        if (json)
        {
            Assert.Equal(File.ReadAllText(summaryPath!), stdout.ToString().TrimEnd());
            Assert.Equal(passed ? "passed" : "failed", JsonNode.Parse(stdout.ToString())!["status"]!.GetValue<string>());
        }
        else
        {
            Assert.Single(interaction.DisplayedRenderables);
        }
        cancellation.Cancel();
        Assert.Equal(CliExitCodes.Cancelled, await execution.DefaultTimeout());
        await tornDown.Task.DefaultTimeout();
        if (json)
        {
            Assert.Equal(beforeStop.ToJsonString(), JsonNode.Parse(stdout.ToString())!.ToJsonString());
        }
        else
        {
            Assert.Equal(1, summaryMessages);
        }
        Assert.Equal(1, completionQueries);
        var afterStop = JsonNode.Parse(File.ReadAllText(summaryPath!))!;
        Assert.Equal("cancelled", afterStop["status"]!.GetValue<string>());
        Assert.Equal(130, afterStop["cliExitCode"]!.GetValue<int>());
        Assert.Equal(beforeStop["completedAt"]!.GetValue<string>(), afterStop["completedAt"]!.GetValue<string>());
        Assert.Equal(passed ? "passed" : "failed", afterStop["resources"]![0]!["outcome"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("artifact")]
    public async Task KeepAliveDoesNotHoldAnIncompleteOrUnwritableRun(string failure)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? outputPath = null;
        var backchannel = new TestAppHostBackchannel
        {
            WaitForTestCompletionAsyncCallback = _ =>
            {
                if (failure == "connection")
                {
                    return Task.FromException<bool>(new ConnectionLostException("AppHost disconnected."));
                }
                var path = Path.Combine(outputPath!, "summary.json");
                File.Delete(path);
                Directory.CreateDirectory(path);
                return Task.FromResult(true);
            }
        };
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                outputPath = context.EnvironmentVariables[KnownConfigNames.TestOutputPath];
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0; }
                finally { tornDown.TrySetResult(); }
            }
        };
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<IInteractionService>(interaction);
        using var provider = services.BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse(["test", "--apphost", appHostFile.FullName, "--keep-alive", "--format", "json"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
        await tornDown.Task.DefaultTimeout();
        var json = JsonNode.Parse(Assert.Single(interaction.DisplayedRawText).Text)!;
        Assert.Equal("failed", json["status"]!.GetValue<string>());
        Assert.Equal(exitCode, json["cliExitCode"]!.GetValue<int>());
        Assert.NotNull(json["error"]);
    }

    private static ConsoleInteractionService CreateConsoleInteraction(TemporaryWorkspace workspace, StringBuilder stdout, StringBuilder stderr,
        AnsiSupport ansi = AnsiSupport.No)
    {
        IAnsiConsole CreateConsole(StringBuilder output)
        {
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = ansi,
                Out = new AnsiConsoleOutput(new StringWriter(output)),
                Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
            });
            console.Profile.Width = int.MaxValue;
            return console;
        }
        return new ConsoleInteractionService(new ConsoleEnvironment(CreateConsole(stdout), CreateConsole(stderr), TextReader.Null),
            workspace.CreateExecutionContext(), TestHelpers.CreateInteractiveHostEnvironment(),
            new EnvironmentProcessPathProvider(), NullLoggerFactory.Instance, new ConsoleLogBufferContext());
    }

    [Theory]
    [InlineData(true, 0, 0)]
    [InlineData(false, 0, 23)]
    [InlineData(true, 42, 23)]
    public async Task KeepAlivePreservesOutcomesWhenTheAppHostStops(bool passed, int processExitCode, int expectedExitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var inspecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processExit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeProvider = new SignalingFakeTimeProvider(TestCommand.s_gracefulShutdownBudget);
        var backchannel = new TestAppHostBackchannel
        {
            WaitForTestCompletionAsyncCallback = _ => Task.FromResult(passed),
            WaitForDisconnectAsyncCalled = inspecting
        };
        string? summaryPath = null;
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = (context, ct) =>
            {
                summaryPath = Path.Combine(context.EnvironmentVariables[KnownConfigNames.TestOutputPath], "summary.json");
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                return processExit.Task.WaitAsync(ct);
            }
        };
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<IInteractionService>(interaction);
        services.AddSingleton<TimeProvider>(timeProvider);
        using var provider = services.BuildServiceProvider();
        var execution = provider.GetRequiredService<RootCommand>()
            .Parse(["test", "--apphost", appHostFile.FullName, "--keep-alive", "--format", "json"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        await inspecting.Task.DefaultTimeout();
        Assert.False(execution.IsCompleted);
        backchannel.DisconnectCompletionSource.TrySetResult();
        await timeProvider.TimerCreated.Task.DefaultTimeout();
        processExit.TrySetResult(processExitCode);
        Assert.Equal(expectedExitCode, await execution.DefaultTimeout());
        var final = JsonNode.Parse(File.ReadAllText(summaryPath!))!;
        Assert.Equal(expectedExitCode, final["cliExitCode"]!.GetValue<int>());
        Assert.Equal(passed ? "passed" : "failed", final["resources"]![0]!["outcome"]!.GetValue<string>());
        Assert.Single(interaction.DisplayedRawText);
    }

    [Fact]
    public async Task KeepAliveDisconnectWithoutProcessExitFailsAndTearsDownTheAppHost()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var inspecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeProvider = new SignalingFakeTimeProvider(TestCommand.s_gracefulShutdownBudget);
        var backchannel = new TestAppHostBackchannel
        {
            WaitForTestCompletionAsyncCallback = _ => Task.FromResult(true),
            WaitForDisconnectAsyncCalled = inspecting
        };
        string? summaryPath = null;
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                summaryPath = Path.Combine(context.EnvironmentVariables[KnownConfigNames.TestOutputPath], "summary.json");
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return 0;
                }
                finally
                {
                    tornDown.TrySetResult();
                }
            }
        };
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<IInteractionService>(interaction);
        services.AddSingleton<TimeProvider>(timeProvider);
        using var provider = services.BuildServiceProvider();
        var execution = provider.GetRequiredService<RootCommand>()
            .Parse(["test", "--apphost", appHostFile.FullName, "--keep-alive", "--format", "json"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        await inspecting.Task.DefaultTimeout();
        backchannel.DisconnectCompletionSource.TrySetResult();
        await timeProvider.TimerCreated.Task.DefaultTimeout();
        timeProvider.Advance(TestCommand.s_gracefulShutdownBudget);
        Assert.NotEqual(CliExitCodes.Success, await execution.DefaultTimeout());
        await tornDown.Task.DefaultTimeout();
        var final = JsonNode.Parse(File.ReadAllText(summaryPath!))!;
        Assert.Equal("failed", final["status"]!.GetValue<string>());
        Assert.Contains(TestCommandStrings.KeepAliveDisconnected, final["error"]!.GetValue<string>());
        Assert.Single(interaction.DisplayedRawText);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AppHostInformationUsesAppendOnlyLinesAndPreservesLabels(bool missingDashboard, bool extension)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var interaction = CreateConsoleInteraction(workspace, stdout, stderr);
        var logPath = Path.Combine(workspace.WorkspaceRoot.FullName, "cli [test].log");
        var dashboard = missingDashboard ? null : "http://localhost:1234/login?t=test";
        var codespaces = missingDashboard ? null : "https://example.app.github.dev/login?t=test";

        TestCommand.RenderAppHostInformation(interaction, "AppHost [test].csproj", dashboard, codespaces, logPath, extension);

        var expected = $"{TestCommandStrings.AppHost}: AppHost [test].csproj{Environment.NewLine}";
        if (!extension)
        {
            expected += $"{TestCommandStrings.Dashboard}: {dashboard ?? "N/A"}{Environment.NewLine}";
            if (codespaces is not null)
            {
                expected += $"{TestCommandStrings.Dashboard}: {codespaces}{Environment.NewLine}";
            }
        }
        expected += $"{TestCommandStrings.Logs}: {logPath}{Environment.NewLine}";
        Assert.Equal(expected, stdout.ToString());
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void AppHostInformationPreservesClickableDashboardAndLogLinks()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var interaction = CreateConsoleInteraction(workspace, stdout, stderr, AnsiSupport.Yes);
        var logPath = Path.Combine(workspace.WorkspaceRoot.FullName, "cli [test].log");
        const string dashboard = "http://localhost:1234/login?t=test";

        TestCommand.RenderAppHostInformation(interaction, "AppHost.csproj", dashboard, null, logPath, false);

        TerminalLinkAssert.ContainsLink(stdout.ToString(), new Uri(Path.GetFullPath(logPath)).AbsoluteUri, logPath);
        TerminalLinkAssert.ContainsLink(stdout.ToString(), dashboard, dashboard);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(42, false)]
    [InlineData(0, true)]
    [InlineData(42, true)]
    public async Task AppHostStartupFailureNeverPasses(int exitCode, bool json)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = (context, _) =>
            {
                context.BuildCompletionSource!.TrySetResult(true);
                return Task.FromResult(exitCode);
            }
        };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<IInteractionService>(CreateConsoleInteraction(workspace, stdout, stderr));
        using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<RootCommand>()
            .Parse(["test", "--apphost", appHostFile.FullName, "--format", json ? "json" : "table"]).InvokeAsync().DefaultTimeout();
        Assert.NotEqual(CliExitCodes.Success, result);
        if (json)
        {
            var output = JsonNode.Parse(stdout.ToString())!;
            Assert.Equal("failed", output["status"]!.GetValue<string>());
            Assert.Equal(result, output["cliExitCode"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task HelpIsRegistered()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        Assert.Equal(0, await provider.GetRequiredService<RootCommand>().Parse("test --help").InvokeAsync().DefaultTimeout());
    }

    [Fact]
    public async Task ShutdownTimeoutFailsAndCancelsTheAppHost()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeProvider = new SignalingFakeTimeProvider(TestCommand.s_gracefulShutdownBudget);
        var backchannel = new TestAppHostBackchannel
        {
            WaitForTestCompletionAsyncCallback = _ => Task.FromResult(true),
            RequestStopAsyncCallback = () =>
            {
                stopped.TrySetResult();
                return Task.CompletedTask;
            }
        };
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return 0;
                }
                finally
                {
                    tornDown.TrySetResult();
                }
            }
        };
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<TimeProvider>(timeProvider);
        using var provider = services.BuildServiceProvider();
        var execution = provider.GetRequiredService<RootCommand>().Parse(["test", "--apphost", appHostFile.FullName])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        await stopped.Task.DefaultTimeout();
        await timeProvider.TimerCreated.Task.DefaultTimeout();
        timeProvider.Advance(TestCommand.s_gracefulShutdownBudget);

        Assert.NotEqual(CliExitCodes.Success, await execution.DefaultTimeout());
        await tornDown.Task.DefaultTimeout();
    }

    [Fact]
    public async Task CancellationDuringStartupTimeoutCleanupRemainsCancellation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        using var cancellation = new CancellationTokenSource();
        var timeProvider = new SignalingFakeTimeProvider(TimeSpan.FromSeconds(1));
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                context.BuildCompletionSource!.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return 0;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    draining.TrySetResult();
                    await releaseDrain.Task;
                    return CliExitCodes.Cancelled;
                }
            }
        };
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<IInteractionService>(interaction);
        services.AddSingleton<TimeProvider>(timeProvider);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IConfiguration>()[CliConfigNames.AppHostStartupTimeout] = "1";
        var execution = provider.GetRequiredService<RootCommand>().Parse(["test", "--format", "json"])
            .InvokeAsync(cancellationToken: cancellation.Token);
        await timeProvider.TimerCreated.Task.DefaultTimeout();
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await draining.Task.DefaultTimeout();
        cancellation.Cancel();
        releaseDrain.TrySetResult();

        Assert.Equal(CliExitCodes.Cancelled, await execution.DefaultTimeout());
        var summary = JsonNode.Parse(Assert.Single(interaction.DisplayedRawText).Text)!;
        Assert.Equal("cancelled", summary["status"]!.GetValue<string>());
        Assert.Equal(CliExitCodes.Cancelled, summary["cliExitCode"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringBuildOrConnectionDrainsAndPersistsJson(bool buildCompleted)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        using var cancellation = new CancellationTokenSource();
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                if (buildCompleted)
                {
                    context.BuildCompletionSource!.TrySetResult(true);
                }
                launched.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return 0;
                }
                finally
                {
                    tornDown.TrySetResult();
                }
            }
        };
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<IInteractionService>(interaction);
        using var provider = services.BuildServiceProvider();
        var execution = provider.GetRequiredService<RootCommand>().Parse(["test", "--format", "json"])
            .InvokeAsync(cancellationToken: cancellation.Token);
        await launched.Task.DefaultTimeout();
        cancellation.Cancel();

        Assert.Equal(CliExitCodes.Cancelled, await execution.DefaultTimeout());
        await tornDown.Task.DefaultTimeout();
        var summary = JsonNode.Parse(Assert.Single(interaction.DisplayedRawText).Text)!;
        Assert.Equal("cancelled", summary["status"]!.GetValue<string>());
        Assert.Equal(CliExitCodes.Cancelled, summary["cliExitCode"]!.GetValue<int>());
        Assert.Empty(summary["resources"]!.AsArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupTimeoutCoversConnectionAndDashboardWithoutLeakingTheAppHost(bool connected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var timeProvider = new SignalingFakeTimeProvider(TimeSpan.FromSeconds(1));
        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannel = new TestAppHostBackchannel
        {
            GetDashboardUrlsAsyncCallback = async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new DashboardUrlsState();
            }
        };
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                context.BuildCompletionSource!.TrySetResult(true);
                if (connected)
                {
                    context.BackchannelCompletionSource!.TrySetResult(backchannel);
                }
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return 0;
                }
                finally
                {
                    tornDown.TrySetResult();
                }
            }
        };
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<IInteractionService>(interaction);
        services.AddSingleton<TimeProvider>(timeProvider);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IConfiguration>()[CliConfigNames.AppHostStartupTimeout] = "1";
        var execution = provider.GetRequiredService<RootCommand>().Parse(["test", "--format", "json"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await timeProvider.TimerCreated.Task.DefaultTimeout();
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(CliExitCodes.FailedToDotnetRunAppHost, await execution.DefaultTimeout());
        await tornDown.Task.DefaultTimeout();
        var summary = JsonNode.Parse(Assert.Single(interaction.DisplayedRawText).Text)!;
        Assert.Equal("failed", summary["status"]!.GetValue<string>());
        Assert.Contains(CliConfigNames.AppHostStartupTimeout, summary["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task BuildTimeDoesNotConsumeTheTestAppHostStartupDeadline()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var building = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannel = new TestAppHostBackchannel
        {
            WaitForTestCompletionAsyncCallback = _ => Task.FromResult(true),
            RequestStopAsyncCallback = () => { stop.TrySetResult(0); return Task.CompletedTask; }
        };
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                building.TrySetResult();
                await finishBuild.Task.WaitAsync(ct);
                timeProvider.Advance(TimeSpan.FromSeconds(5));
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                return await stop.Task.WaitAsync(ct);
            }
        };
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<TimeProvider>(timeProvider);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IConfiguration>()[CliConfigNames.AppHostStartupTimeout] = "1";
        var execution = provider.GetRequiredService<RootCommand>().Parse("test")
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await building.Task.DefaultTimeout();
        Assert.False(execution.IsCompleted);
        finishBuild.TrySetResult();

        Assert.Equal(CliExitCodes.Success, await execution.DefaultTimeout());
    }

    [Fact]
    public async Task InvalidTestStartupTimeoutProducesItsOwnDiagnosticBeforeCreatingArtifacts()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var factory = new TestAppHostProjectFactory { RunAsyncCallback = (_, _) => throw new InvalidOperationException("Must not launch.") };
        var services = CreateServices(workspace, appHostFile, factory);
        var interaction = new TestInteractionService();
        services.AddSingleton<IInteractionService>(interaction);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IConfiguration>()[CliConfigNames.AppHostStartupTimeout] = "0";

        var result = await provider.GetRequiredService<RootCommand>().Parse(["test", "--format", "json"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, result);
        var summary = JsonNode.Parse(Assert.Single(interaction.DisplayedRawText).Text)!;
        Assert.Equal(string.Format(TestCommandStrings.InvalidStartupTimeout, CliConfigNames.AppHostStartupTimeout),
            summary["error"]!.GetValue<string>());
        Assert.Null(summary["summaryPath"]);
        Assert.False(Directory.Exists(Path.Combine(workspace.CreateExecutionContext().AspireHomeDirectory.FullName, "testruns")));
    }

    private IServiceCollection CreateServices(TemporaryWorkspace workspace, FileInfo appHostFile, TestAppHostProjectFactory factory)
    {
        Directory.CreateDirectory(workspace.CreateExecutionContext().HomeDirectory.FullName);

        return CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.EnabledFeatures = [KnownFeatures.DefaultWatchEnabled];
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                    Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
            options.AppHostProjectFactory = _ => factory;
        });
    }

    private static FileInfo CreateAppHost(TemporaryWorkspace workspace)
    {
        var file = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        File.WriteAllText(file.FullName, "<Project />");

        return file;
    }
}
