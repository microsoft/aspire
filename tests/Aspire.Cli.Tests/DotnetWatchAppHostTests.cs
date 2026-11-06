// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Watch;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests;

public class DotnetWatchAppHostTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ResolvesPackageOwnedTargetInAppHostContextAndReusesDescriptor()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var payload = TestDotnetWatchAppHostPayload.Create(workspace.WorkspaceRoot, "10.0.403", "10.0.14");
        var appHost = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        var environment = new Dictionary<string, string> { ["ASPIRE_HOME"] = workspace.Path };
        var runner = new TestDotNetCliRunner
        {
            GetProjectItemsAndPropertiesAsyncCallbackWithTargets = (project, items, properties, targets, options, _) =>
            {
                Assert.Equal(appHost.FullName, project.FullName);
                Assert.Equal(["AspireDotnetWatchTool"], items);
                Assert.Equal(["EnsureAspireDotnetWatchTool"], targets);
                Assert.Equal(["Configuration", "TargetFrameworks", "NETCoreSdkVersion"], properties);
                Assert.True(options.NoRestore);
                Assert.True(options.SuppressLogging);
                Assert.Same(environment, options.EnvironmentVariables);
                return (0, payload.CreateQueryDocument());
            }
        };

        var launch = await DotnetWatchAppHostLaunch.ResolveAsync(runner, appHost, environment, TestContext.Current.CancellationToken);
        launch.ApplyEnvironment(environment);

        Assert.Null(launch.HostIncompatibility);
        Assert.Equal(payload.ToolPath, environment[DotnetWatchConfiguration.ToolPath]);
        Assert.Equal("custom", environment[DotnetWatchConfiguration.ToolVersion]);
        Assert.Equal("10", environment[DotnetWatchConfiguration.ToolRuntimeMajor]);
        Assert.Equal(payload.DotnetPath, launch.DotnetPath);
        Assert.Equal(payload.SdkDirectory, launch.SdkDirectory);
    }

    [Fact]
    public async Task BootstrapCancellationNeverBecomesOrdinaryFallback()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var runner = new TestDotNetCliRunner
        {
            GetProjectItemsAndPropertiesAsyncCallbackWithTargets = (_, _, _, _, _, _) =>
            {
                cancellation.Cancel();
                return (1, null);
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DotnetWatchAppHostLaunch.ResolveAsync(
            runner, new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj")), new Dictionary<string, string>(), cancellation.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task MissingBootstrapIsDiagnosedWithoutCliAcquisition(int exitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var runner = new TestDotNetCliRunner
        {
            GetProjectItemsAndPropertiesAsyncCallbackWithTargets = (_, _, _, _, _, _) => (exitCode, null)
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => DotnetWatchAppHostLaunch.ResolveAsync(
            runner, new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj")), new Dictionary<string, string>(), TestContext.Current.CancellationToken));

        Assert.Equal(RunCommandStrings.WatchBootstrapUnavailable, exception.Message);
    }

    [Fact]
    public void Sdk11DoesNotLoadIntoRuntime10WatcherOrLoseServiceDescriptor()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var payload = TestDotnetWatchAppHostPayload.Create(workspace.WorkspaceRoot, "11.0.100-rc.2.26504.105", "10.0.14");
        using var result = payload.CreateQueryDocument();

        var launch = DotnetWatchAppHostLaunch.Read(new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj")), result.RootElement);

        Assert.Equal(payload.ToolPath, launch.ToolPath);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostSdkIncompatible,
            "custom", 10, payload.SdkVersion), launch.HostIncompatibility);
    }

    [Fact]
    public void DeveloperSelectedRuntime11WatcherUsesActualSdk11Directory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var payload = TestDotnetWatchAppHostPayload.Create(workspace.WorkspaceRoot, "11.0.100-rc.2.26504.105", "11.0.0-rc.2.26504.105");
        using var result = payload.CreateQueryDocument();

        var launch = DotnetWatchAppHostLaunch.Read(new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj")), result.RootElement);

        Assert.Null(launch.HostIncompatibility);
        Assert.Equal(11, launch.ToolRuntimeMajor);
        Assert.Equal(payload.SdkDirectory, launch.SdkDirectory);
    }

    [Fact]
    public void MissingAspNetCoreRuntimeIsNotReportedAsAnSdkFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var payload = TestDotnetWatchAppHostPayload.Create(workspace.WorkspaceRoot, "10.0.403", "10.0.14");
        Directory.Delete(Path.Combine(Path.GetDirectoryName(payload.DotnetPath)!, "shared", "Microsoft.AspNetCore.App"), recursive: true);
        using var result = payload.CreateQueryDocument();

        var launch = DotnetWatchAppHostLaunch.Read(new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj")), result.RootElement);

        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostRuntimeMissing,
            "Microsoft.AspNetCore.App", "10.0.14", Path.GetDirectoryName(PathNormalizer.ResolveSymlinks(payload.DotnetPath))), launch.HostIncompatibility);
    }

    [Theory]
    [InlineData("apphost.cs")]
    [InlineData("AppHost.fsproj")]
    [InlineData("AppHost.vbproj")]
    public void UnsupportedHostEntryPointKeepsResolvedServiceTool(string name)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var payload = TestDotnetWatchAppHostPayload.Create(workspace.WorkspaceRoot, "10.0.403", "10.0.14");
        using var result = payload.CreateQueryDocument();

        var launch = DotnetWatchAppHostLaunch.Read(new FileInfo(Path.Combine(workspace.Path, name)), result.RootElement);

        Assert.Equal(payload.ToolPath, launch.ToolPath);
        Assert.Equal(RunCommandStrings.WatchAppHostProjectRequired, launch.HostIncompatibility);
    }

    [Theory]
    [InlineData("Release", "")]
    [InlineData("Debug", "net10.0;net11.0")]
    public void UnsupportedHostBuildContextIsDiagnosed(string configuration, string frameworks)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var payload = TestDotnetWatchAppHostPayload.Create(workspace.WorkspaceRoot, "10.0.403", "10.0.14");
        var result = payload.CreateQueryResult();
        result["Properties"]!["Configuration"] = configuration;
        result["Properties"]!["TargetFrameworks"] = frameworks;
        using var document = JsonDocument.Parse(result.ToJsonString());

        var launch = DotnetWatchAppHostLaunch.Read(new FileInfo(Path.Combine(workspace.Path, "AppHost.csproj")), document.RootElement);

        Assert.Equal(RunCommandStrings.WatchAppHostDefaultBuildRequired, launch.HostIncompatibility);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "--profile-shaped")]
    [InlineData(true, "ignored")]
    public async Task HostArgumentsPreserveProfileAndApplicationArgumentBoundaries(bool excludeProfile, string? profile)
    {
        var appHost = new FileInfo(Path.Combine(Environment.CurrentDirectory, "AppHost.csproj"));
        var launch = new DotnetWatchAppHostLaunch("watch.dll", "custom", 10, "selected-sdk", "selected-dotnet", null);

        var arguments = launch.CreateArguments(appHost, new() { NoLaunchProfile = excludeProfile, LaunchProfile = profile },
            ["--environment", "two words", "", "--project", "app-input"]);

        await VerifyJson(JsonSerializer.Serialize(arguments))
            .UseParameters(excludeProfile, profile ?? "default")
            .ScrubLinesWithReplace(line => line.Replace(appHost.FullName.Replace("\\", "\\\\", StringComparison.Ordinal), "AppHost.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrivateNotificationsRequireAnEnabledMatchingGeneration()
    {
        using var shutdown = new TestGracefulShutdownWindow();
        await using var generation = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromSeconds(5), NullLogger.Instance);
        generation.ApplyEnvironment(new Dictionary<string, string>());
        await SendAsync(generation, new() { Session = "old-generation" });
        await SendAsync(generation, new() { Version = 2, Session = generation.Session });
        await SendAsync(generation, new() { Session = generation.Session });
        await generation.Invalidated.DefaultTimeout();
    }

    [Fact]
    public async Task DisabledGenerationNeverInvalidates()
    {
        using var shutdown = new TestGracefulShutdownWindow();
        var generation = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromSeconds(5), NullLogger.Instance);
        await using (generation)
        {
            await SendAsync(generation, new() { Session = generation.Session });
        }

        Assert.False(generation.Invalidated.IsCompleted);
    }

    [Fact]
    public async Task UpstreamChildRestartInvalidatesWithoutTreatingInitialOrDuplicateStartupAsAModelChange()
    {
        using var shutdown = new TestGracefulShutdownWindow();
        await using var generation = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromSeconds(5), NullLogger.Instance);
        generation.ApplyEnvironment(new Dictionary<string, string>());
        await SendAsync(generation, new() { Session = generation.Session, Kind = DotnetWatchHostProtocol.HostStarted, Instance = "first-process" });
        await SendAsync(generation, new() { Session = generation.Session, Kind = DotnetWatchHostProtocol.HostStarted, Instance = "first-process" });
        await SendAsync(generation, new() { Session = generation.Session, Kind = DotnetWatchHostProtocol.HostStarted, Instance = "replacement-process" });

        await generation.Invalidated.DefaultTimeout();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(DotnetWatchHostProtocol.MaximumMessageBytes + 1)]
    public async Task InvalidNotificationLengthsDoNotInvalidateOrBreakNextConnection(int size)
    {
        using var shutdown = new TestGracefulShutdownWindow();
        await using var generation = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromSeconds(5), NullLogger.Instance);
        generation.ApplyEnvironment(new Dictionary<string, string>());
        await using (var client = CreateClient(generation))
        {
            await client.ConnectAsync(TestContext.Current.CancellationToken).DefaultTimeout();
            var prefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, size);
            await client.WriteAsync(prefix, TestContext.Current.CancellationToken);
        }

        await SendAsync(generation, new() { Session = generation.Session });
        await generation.Invalidated.DefaultTimeout();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionWaitsForCompleteOldGenerationTeardownBeforeRelaunch(bool backchannelEstablished)
    {
        using var shutdown = new TestGracefulShutdownWindow();
        using var cancellation = new CancellationTokenSource();
        var firstStarted = new TaskCompletionSource<DotnetWatchAppHostGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardownStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishTeardown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generations = new List<string>();
        var restartCount = 0;
        var session = new DotnetWatchAppHostSession(shutdown, TimeSpan.FromSeconds(5), TimeProvider.System, NullLogger.Instance);
        var run = session.RunAsync(async (generation, token) =>
        {
            generations.Add(generation.Session);
            generation.ApplyEnvironment(new Dictionary<string, string>());
            if (generations.Count == 1)
            {
                Assert.True(generation.RequiresLauncherMonitoring);
                if (backchannelEstablished)
                {
                    generation.MarkBackchannelEstablished();
                }
                firstStarted.TrySetResult(generation);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    teardownStarted.TrySetResult();
                    await finishTeardown.Task;
                }
            }
            else
            {
                Assert.Equal(!backchannelEstablished, generation.RequiresLauncherMonitoring);
            }

            return CommandResult.Success();
        }, () => restartCount++, cancellation.Token);

        try
        {
            var first = await firstStarted.Task.DefaultTimeout();
            await SendAsync(first, new() { Session = first.Session });
            await teardownStarted.Task.DefaultTimeout();
            Assert.Single(generations);
            finishTeardown.TrySetResult();

            var result = await run.DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, result.ExitCode);
            Assert.Equal(1, restartCount);
            Assert.Equal(2, generations.Distinct().Count());
            Assert.False(shutdown.GracefulShutdownToken.IsCancellationRequested);
        }
        finally
        {
            finishTeardown.TrySetResult();
            await cancellation.CancelAsync();
            await run.DefaultTimeout();
        }
    }

    [Fact]
    public async Task UserCancellationDoesNotCreateAnotherGeneration()
    {
        using var shutdown = new TestGracefulShutdownWindow();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generationCount = 0;
        var restartCount = 0;
        var session = new DotnetWatchAppHostSession(shutdown, TimeSpan.FromSeconds(5), TimeProvider.System, NullLogger.Instance);
        var run = session.RunAsync(async (_, token) =>
        {
            generationCount++;
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return CommandResult.Success();
        }, () => restartCount++, cancellation.Token);

        await started.Task.DefaultTimeout();
        await cancellation.CancelAsync();
        var result = await run.DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(1, generationCount);
        Assert.Equal(0, restartCount);
    }

    [Fact]
    public async Task GenerationShutdownDoesNotExpireTheCommandOrFollowingGeneration()
    {
        using var shutdown = new TestGracefulShutdownWindow();
        await using var first = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromMilliseconds(10), NullLogger.Instance);
        first.BeginGracefulWindow();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Delay(Timeout.InfiniteTimeSpan, first.GracefulShutdownToken).DefaultTimeout());

        await using var second = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromSeconds(5), NullLogger.Instance);

        Assert.False(shutdown.GracefulShutdownToken.IsCancellationRequested);
        Assert.False(second.GracefulShutdownToken.IsCancellationRequested);
    }

    private static NamedPipeClientStream CreateClient(DotnetWatchAppHostGeneration generation)
        => new(".", generation.PipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task SendAsync(DotnetWatchAppHostGeneration generation, DotnetWatchHostInvalidation message)
    {
        await using var client = CreateClient(generation);
        await client.ConnectAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        await DotnetWatchHostProtocol.WriteAsync(client, message, TestContext.Current.CancellationToken).DefaultTimeout();
    }
}
