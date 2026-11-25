// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Watch;
using Aspire.Shared.DotnetWatch;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.DotnetWatch;

public class DotnetWatchAppHostTests
{
    [Fact]
    public async Task ResolvesTheActualPackageTargetInTheAppHostSdkContext()
    {
        using var directory = new DotnetWatchTestDirectory();
        var payload = TestDotnetWatchAppHostPayload.Create(directory.Directory, "10.0.403", "10.0.14");
        var appHost = new FileInfo(Path.Combine(directory.Directory.FullName, "AppHost.csproj"));
        var environment = new Dictionary<string, string> { ["ApplicationVariable"] = "preserved" };
        var runner = new TestDotNetCliRunner
        {
            GetProjectItemsAndPropertiesAsyncCallbackWithTargets = (project, items, properties, targets, options, _) =>
            {
                Assert.Equal(appHost.FullName, project.FullName);
                Assert.Empty(items);
                Assert.Equal(["Configuration", "TargetFrameworks", "NETCoreSdkVersion"], properties);
                Assert.Equal(["ResolveAspireDotnetWatch"], targets);
                Assert.True(options.NoRestore);
                Assert.True(options.QueryTargetResults);
                Assert.True(options.SuppressLogging);
                Assert.Same(environment, options.EnvironmentVariables);
                return (0, payload.CreateQueryDocument());
            }
        };

        var launch = await DotnetWatchAppHostLaunch.ResolveAsync(runner, appHost, environment, _ => null, TestContext.Current.CancellationToken);

        Assert.Null(launch.HostIncompatibility);
        Assert.Equal(payload.ToolPath, launch.ToolPath);
        Assert.Equal(payload.WatchHostPath, launch.WatchHostPath);
        Assert.Equal(payload.DotnetPath, launch.DotnetPath);
        Assert.Equal(payload.SdkDirectory, launch.SdkDirectory);
        Assert.Equal(payload.SdkVersion, launch.SdkVersion);
        Assert.Single(environment);
    }

    [Fact]
    public async Task ResolveCancellationDoesNotBecomeOrdinaryFallback()
    {
        using var directory = new DotnetWatchTestDirectory();
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
            runner, new FileInfo(Path.Combine(directory.Directory.FullName, "AppHost.csproj")),
            new Dictionary<string, string>(), _ => null, cancellation.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task MissingTargetIsDiagnosedWithoutCliAcquisition(int exitCode)
    {
        using var directory = new DotnetWatchTestDirectory();
        var runner = new TestDotNetCliRunner
        {
            GetProjectItemsAndPropertiesAsyncCallbackWithTargets = (_, _, _, _, _, _) => (exitCode, null)
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => DotnetWatchAppHostLaunch.ResolveAsync(
            runner, new FileInfo(Path.Combine(directory.Directory.FullName, "AppHost.csproj")),
            new Dictionary<string, string>(), _ => null, TestContext.Current.CancellationToken));

        Assert.Equal(RunCommandStrings.WatchBootstrapUnavailable, exception.Message);
    }

    [Theory]
    [InlineData("11.0.100-rc.2.26504.105", null, "10.0.14", true)]
    [InlineData("11.0.100-rc.2.26504.105", null, "11.0.0-rc.2.26504.105", false)]
    [InlineData("10.0.403", null, "10.0.14", false)]
    // An SDK patch released after the watcher requests a newer runtime patch; the watcher runs on that patch.
    [InlineData("10.0.404", "10.0.15", "10.0.14", false)]
    [InlineData("11.0.100", "11.0.0", "11.0.0", false)]
    // A watcher only loads MSBuild of its own major, older or newer.
    [InlineData("10.0.403", "10.0.14", "11.0.0", true)]
    public void ValidatesSelectedSdkWithoutReplacingItOrDiscardingTheServiceTool(string sdkVersion, string? msbuildRuntimeVersion, string runtimeVersion, bool incompatible)
    {
        using var directory = new DotnetWatchTestDirectory();
        var payload = TestDotnetWatchAppHostPayload.Create(directory.Directory, sdkVersion, runtimeVersion, msbuildRuntimeVersion);
        using var result = payload.CreateQueryDocument();

        var launch = DotnetWatchAppHostLaunch.Read(new FileInfo(Path.Combine(directory.Directory.FullName, "AppHost.csproj")),
            result.RootElement, toolOverride: null, rollForwardOverride: null);

        Assert.Equal(incompatible, launch.HostIncompatibility is not null);
        Assert.Equal(payload.ToolPath, launch.ToolPath);
        Assert.Equal(payload.DotnetPath, launch.DotnetPath);
        Assert.Equal(payload.SdkDirectory, launch.SdkDirectory);
        Assert.Equal(sdkVersion, launch.SdkVersion);
    }

    [Fact]
    public void AppHostSdkWithoutAShippedWatcherIsAHostCoverageDiagnostic()
    {
        using var directory = new DotnetWatchTestDirectory();
        var payload = TestDotnetWatchAppHostPayload.Create(directory.Directory, "12.0.100-preview.1.26101.1", "10.0.14");
        using var result = payload.CreateQueryDocumentWithoutWatcher();
        var appHost = new FileInfo(Path.Combine(directory.Directory.FullName, "AppHost.csproj"));

        var launch = DotnetWatchAppHostLaunch.Read(appHost, result.RootElement, toolOverride: null, rollForwardOverride: null);

        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostSdkUnsupported,
            12, "12.0.100-preview.1.26101.1"), launch.HostIncompatibility);
        Assert.Equal("12.0.100-preview.1.26101.1", launch.SdkVersion);

        // An explicit override always resolves to one tool, so an empty result can't be the override's.
        var exception = Assert.Throws<InvalidDataException>(() => DotnetWatchAppHostLaunch.Read(
            appHost, result.RootElement, toolOverride: payload.ToolPath, rollForwardOverride: null));
        Assert.Equal(RunCommandStrings.WatchBootstrapInvalid, exception.Message);
    }

    [Fact]
    public void MissingAspNetCoreRuntimeIsAHostCoverageDiagnostic()
    {
        using var directory = new DotnetWatchTestDirectory();
        var payload = TestDotnetWatchAppHostPayload.Create(directory.Directory, "10.0.403", "10.0.14");
        Directory.Delete(Path.Combine(Path.GetDirectoryName(payload.DotnetPath)!, "shared", "Microsoft.AspNetCore.App"), recursive: true);
        using var result = payload.CreateQueryDocument();

        var launch = DotnetWatchAppHostLaunch.Read(new FileInfo(Path.Combine(directory.Directory.FullName, "AppHost.csproj")),
            result.RootElement, toolOverride: null, rollForwardOverride: null);

        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostRuntimeMissing,
            "Microsoft.AspNetCore.App 10.0.14", Path.GetDirectoryName(payload.DotnetPath)), launch.HostIncompatibility);
        Assert.Equal(payload.ToolPath, launch.ToolPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MsBuildDescriptorUsesTheSelectedSdkDirectoryWithoutProbingAnotherInstallation(bool fileDescriptor)
    {
        using var directory = new DotnetWatchTestDirectory();
        var payload = TestDotnetWatchAppHostPayload.Create(directory.Directory, "10.0.403", "10.0.14");
        var query = payload.CreateQueryResult();
        if (fileDescriptor)
        {
            query["TargetResults"]!["ResolveAspireDotnetWatch"]!["Items"]![0]!["MSBuildPath"] =
                Path.Combine(payload.SdkDirectory, "MSBuild.dll");
        }
        using var document = JsonDocument.Parse(query.ToJsonString());

        var launch = DotnetWatchAppHostLaunch.Read(new FileInfo(Path.Combine(directory.Directory.FullName, "AppHost.csproj")),
            document.RootElement, toolOverride: null, rollForwardOverride: null);

        Assert.Null(launch.HostIncompatibility);
        Assert.Equal(payload.SdkDirectory, launch.SdkDirectory);
        Assert.Equal(payload.DotnetPath, launch.DotnetPath);
    }

    [Fact]
    public void MissingHotReloadAgentDisablesTheWrapperWithoutDiscardingTheServiceDescriptor()
    {
        using var directory = new DotnetWatchTestDirectory();
        var payload = TestDotnetWatchAppHostPayload.Create(directory.Directory, "10.0.403", "10.0.14");
        File.Delete(Path.Combine(Path.GetDirectoryName(payload.ToolPath)!, "hotreload", "net10.0", "Microsoft.Extensions.DotNetDeltaApplier.dll"));
        using var document = payload.CreateQueryDocument();

        var launch = DotnetWatchAppHostLaunch.Read(new FileInfo(Path.Combine(directory.Directory.FullName, "AppHost.csproj")),
            document.RootElement, toolOverride: null, rollForwardOverride: null);

        Assert.NotNull(launch.HostIncompatibility);
        Assert.Equal(payload.ToolPath, launch.ToolPath);
    }

    [Theory]
    [InlineData("apphost.cs", "Debug", "")]
    [InlineData("AppHost.fsproj", "Debug", "")]
    [InlineData("AppHost.vbproj", "Debug", "")]
    [InlineData("AppHost.csproj", "Release", "")]
    [InlineData("AppHost.csproj", "Debug", "net10.0;net11.0")]
    public void UnsupportedAppHostEntryPointsOrBuildContextsRetainServiceCoverage(string file, string configuration, string frameworks)
    {
        using var directory = new DotnetWatchTestDirectory();
        var payload = TestDotnetWatchAppHostPayload.Create(directory.Directory, "10.0.403", "10.0.14");
        var query = payload.CreateQueryResult();
        query["Properties"]!["Configuration"] = configuration;
        query["Properties"]!["TargetFrameworks"] = frameworks;
        using var result = JsonDocument.Parse(query.ToJsonString());

        var launch = DotnetWatchAppHostLaunch.Read(new FileInfo(Path.Combine(directory.Directory.FullName, file)),
            result.RootElement, toolOverride: null, rollForwardOverride: null);

        Assert.Equal(file.EndsWith(".csproj", StringComparison.Ordinal)
            ? RunCommandStrings.WatchAppHostDefaultBuildRequired
            : RunCommandStrings.WatchAppHostProjectRequired, launch.HostIncompatibility);
        Assert.Equal(payload.ToolPath, launch.ToolPath);
    }

    [Theory]
    [InlineData("deps")]
    [InlineData("runtimeconfig")]
    [InlineData("dependency")]
    [InlineData("watch-host")]
    [InlineData("evaluation-adapter")]
    [InlineData("evaluation-project")]
    [InlineData("msbuild")]
    public void MissingPayloadFilesAreRejectedBeforeWrapperLaunch(string missing)
    {
        using var directory = new DotnetWatchTestDirectory();
        var payload = TestDotnetWatchAppHostPayload.Create(directory.Directory, "10.0.403", "10.0.14");
        var path = missing switch
        {
            "deps" => Path.ChangeExtension(payload.ToolPath, ".deps.json"),
            "runtimeconfig" => Path.ChangeExtension(payload.ToolPath, ".runtimeconfig.json"),
            "dependency" => payload.DependencyPath,
            "watch-host" => payload.WatchHostPath,
            "evaluation-adapter" => Path.Combine(Path.GetDirectoryName(payload.WatchHostPath)!, "Aspire.DotnetWatch.Evaluation.dll"),
            "evaluation-project" => Path.Combine(Path.GetDirectoryName(payload.WatchHostPath)!, "Aspire.DotnetWatch.Evaluation.proj"),
            _ => Path.Combine(payload.SdkDirectory, "MSBuild.dll")
        };
        File.Delete(path);
        using var result = payload.CreateQueryDocument();

        Assert.Throws<InvalidDataException>(() => DotnetWatchAppHostLaunch.Read(
            new FileInfo(Path.Combine(directory.Directory.FullName, "AppHost.csproj")), result.RootElement,
            toolOverride: null, rollForwardOverride: null));
    }

    [Fact]
    public void RelativeOverrideUsesTheAppHostDirectoryAndInvalidOverrideDoesNotSelectTheBundle()
    {
        using var directory = new DotnetWatchTestDirectory();
        var payload = TestDotnetWatchAppHostPayload.Create(directory.Directory, "10.0.403", "10.0.14");
        var appHostDirectory = Directory.CreateDirectory(Path.Combine(directory.Directory.FullName, "nested", "apphost"));
        var appHost = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        using var result = payload.CreateQueryDocument();
        var relativeOverride = Path.GetRelativePath(appHostDirectory.FullName, payload.ToolPath);

        var launch = DotnetWatchAppHostLaunch.Read(appHost, result.RootElement, relativeOverride, rollForwardOverride: null);
        var exception = Assert.Throws<InvalidDataException>(() =>
            DotnetWatchAppHostLaunch.Read(appHost, result.RootElement, "missing-watch.dll", rollForwardOverride: null));
        var emptyOverride = Assert.Throws<InvalidDataException>(() =>
            DotnetWatchAppHostLaunch.Read(appHost, result.RootElement, "", rollForwardOverride: null));

        Assert.Null(launch.HostIncompatibility);
        Assert.Equal(RunCommandStrings.WatchToolOverrideInvalid, exception.Message);
        Assert.Equal(RunCommandStrings.WatchToolOverrideInvalid, emptyOverride.Message);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "--profile-shaped")]
    [InlineData(true, "ignored")]
    public async Task HostArgumentsProtectProfilesAndSdkShapedApplicationArguments(bool noProfile, string? profile)
    {
        var appHost = new FileInfo(Path.Combine(Environment.CurrentDirectory, "AppHost.csproj"));
        var launch = new DotnetWatchAppHostLaunch("watch.dll", "WatchHost.dll", "selected-sdk", "10.0.403", "selected-dotnet", null);
        var arguments = launch.CreateArguments(appHost, new() { NoLaunchProfile = noProfile, LaunchProfile = profile },
            ["--environment", "two words", "", "--project", "app-input", "--", "--watch"]);

        await Verify(arguments)
            .UseParameters(noProfile, profile ?? "default")
            .ScrubLinesWithReplace(line => line.Replace(appHost.FullName, "AppHost.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsupportedGenerationsDoNotCreateAListener()
    {
        using var directory = new DotnetWatchTestDirectory();
        using var shutdown = new TestGracefulShutdownWindow();
        await using var generation = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, NullLogger.Instance);

        Assert.False(generation.IsEnabled);
        Assert.Throws<InvalidOperationException>(() => generation.Endpoint);
        Assert.Empty(directory.Directory.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task NotificationsRequireTheCurrentSessionAndTheAnnouncedProcessInstance()
    {
        using var directory = new DotnetWatchTestDirectory();
        using var shutdown = new TestGracefulShutdownWindow();
        await using var generation = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, NullLogger.Instance);
        generation.ApplyEnvironment(new Dictionary<string, string>());
        await SendAsync(generation, DotnetWatchAppHostProtocol.HostStarted, "123:456", session: Guid.NewGuid().ToString("N"));
        Assert.False(generation.HostStarted.IsCompleted);
        Assert.Null(generation.GetGracefulShutdownTarget());
        await SendAsync(generation, DotnetWatchAppHostProtocol.HostStarted, "123:456");
        await generation.HostStarted.DefaultTimeout();
        Assert.Equal((123, DateTimeOffset.FromUnixTimeMilliseconds(456)), generation.GetGracefulShutdownTarget());
        await SendAsync(generation, DotnetWatchAppHostProtocol.ModelInvalidated, "124:456");
        Assert.False(generation.Invalidated.IsCompleted);
        Assert.Equal((123, DateTimeOffset.FromUnixTimeMilliseconds(456)), generation.GetGracefulShutdownTarget());
        await SendAsync(generation, DotnetWatchAppHostProtocol.ModelInvalidated, "123:456");

        await generation.Invalidated.DefaultTimeout();
    }

    [Fact]
    public async Task InitialAndDuplicateStartsDoNotInvalidateButARudeEditReplacementDoes()
    {
        using var directory = new DotnetWatchTestDirectory();
        using var shutdown = new TestGracefulShutdownWindow();
        await using var generation = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, NullLogger.Instance);
        generation.ApplyEnvironment(new Dictionary<string, string>());
        await SendAsync(generation, DotnetWatchAppHostProtocol.HostStarted, "123:456");
        await SendAsync(generation, DotnetWatchAppHostProtocol.HostStarted, "123:456");
        Assert.True(generation.HostStarted.IsCompletedSuccessfully);
        Assert.False(generation.Invalidated.IsCompleted);
        await SendAsync(generation, DotnetWatchAppHostProtocol.HostStarted, "123:789");

        await generation.Invalidated.DefaultTimeout();
        Assert.Equal("123:456", await generation.HostStarted);
        Assert.Equal((123, DateTimeOffset.FromUnixTimeMilliseconds(789)), generation.GetGracefulShutdownTarget());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(DotnetWatchAppHostProtocol.MaximumMessageBytes + 1)]
    public async Task MalformedRecordsDoNotInvalidateOrBreakTheNextConnection(int length)
    {
        using var directory = new DotnetWatchTestDirectory();
        using var shutdown = new TestGracefulShutdownWindow();
        await using var generation = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, NullLogger.Instance);
        generation.ApplyEnvironment(new Dictionary<string, string>());
        var frame = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(frame, length);
        await DotnetWatchAppHostTestChannel.SendFrameAsync(generation.Endpoint, frame, TestContext.Current.CancellationToken).DefaultTimeout();
        await DotnetWatchAppHostTestChannel.SendFrameAsync(generation.Endpoint,
            DotnetWatchAppHostTestChannel.Frame($$"""{"Version":2,"Session":"{{generation.Session}}","Kind":1,"Instance":"123:456"}"""),
            TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.False(generation.Invalidated.IsCompleted);
        await SendAsync(generation, DotnetWatchAppHostProtocol.HostStarted, "123:456");
        await SendAsync(generation, DotnetWatchAppHostProtocol.ModelInvalidated, "123:456");

        await generation.Invalidated.DefaultTimeout();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementAwaitsCompleteTeardownAndDoesNotRearmADepartedDetachedLauncher(bool connected)
    {
        using var directory = new DotnetWatchTestDirectory();
        using var shutdown = new TestGracefulShutdownWindow();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<DotnetWatchAppHostGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
        var teardownStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishTeardown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generations = new ConcurrentQueue<string>();
        var restartCount = 0;
        var session = new DotnetWatchAppHostSession(shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, TimeProvider.System, NullLogger.Instance);
        var run = session.RunAsync(async (generation, token) =>
        {
            generations.Enqueue(generation.Session);
            generation.ApplyEnvironment(new Dictionary<string, string>());
            if (generations.Count == 1)
            {
                Assert.True(generation.RequiresLauncherMonitoring);
                started.TrySetResult(generation);
                if (connected)
                {
                    await generation.HostStarted.WaitAsync(token);
                    generation.MarkBackchannelEstablished();
                }
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    generation.BeginGracefulWindow();
                    teardownStarted.TrySetResult();
                    await finishTeardown.Task;
                }
            }
            else
            {
                Assert.Equal(!connected, generation.RequiresLauncherMonitoring);
                Assert.False(generation.GracefulShutdownToken.IsCancellationRequested);
            }

            return CommandResult.Success();
        }, _ => Interlocked.Increment(ref restartCount), cancellation.Token);

        try
        {
            var first = await started.Task.DefaultTimeout();
            await SendAsync(first, DotnetWatchAppHostProtocol.HostStarted, "123:456");
            await SendAsync(first, DotnetWatchAppHostProtocol.ModelInvalidated, "123:456");
            await teardownStarted.Task.DefaultTimeout();
            Assert.Single(generations);
            Assert.Equal(0, Volatile.Read(ref restartCount));
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
    public async Task UserCancellationCannotReviveAWatchGeneration()
    {
        using var directory = new DotnetWatchTestDirectory();
        using var shutdown = new TestGracefulShutdownWindow();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generations = 0;
        var restarts = 0;
        var session = new DotnetWatchAppHostSession(shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, TimeProvider.System, NullLogger.Instance);
        var run = session.RunAsync(async (_, token) =>
        {
            Interlocked.Increment(ref generations);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return CommandResult.Success();
        }, _ => Interlocked.Increment(ref restarts), cancellation.Token);
        await started.Task.DefaultTimeout();
        await cancellation.CancelAsync();

        var result = await run.DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, result.ExitCode);
        Assert.Equal(1, generations);
        Assert.Equal(0, restarts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementRecordsWhetherCtrlRRequestedIt(bool manualReload)
    {
        using var directory = new DotnetWatchTestDirectory();
        using var shutdown = new TestGracefulShutdownWindow();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<DotnetWatchAppHostGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generations = 0;
        var restartReasons = new List<bool>();
        var session = new DotnetWatchAppHostSession(shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, TimeProvider.System, NullLogger.Instance);
        var run = session.RunAsync(async (generation, token) =>
        {
            if (Interlocked.Increment(ref generations) == 1)
            {
                Assert.False(generation.IsReplacement);
                Assert.False(generation.StartedByReload);
                generation.ApplyEnvironment(new Dictionary<string, string>());
                started.TrySetResult(generation);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            else
            {
                Assert.True(generation.IsReplacement);
                Assert.Equal(manualReload, generation.StartedByReload);
            }

            return CommandResult.Success();
        }, restartReasons.Add, cancellation.Token);

        try
        {
            var first = await started.Task.DefaultTimeout();
            if (manualReload)
            {
                Assert.True(first.RequestReload());
                Assert.False(first.RequestReload());
            }
            else
            {
                await SendAsync(first, DotnetWatchAppHostProtocol.HostStarted, "123:456");
                await SendAsync(first, DotnetWatchAppHostProtocol.ModelInvalidated, "123:456");
                Assert.False(first.RequestReload());
            }
            var result = await run.DefaultTimeout();

            Assert.Equal(CliExitCodes.Success, result.ExitCode);
            Assert.Equal(2, generations);
            Assert.Equal([manualReload], restartReasons);
        }
        finally
        {
            await cancellation.CancelAsync();
            await run.DefaultTimeout();
        }
    }

    [Fact]
    public async Task CtrlRCanReplaceAGenerationWhosePrebuildDidNotEnableItsChannel()
    {
        using var directory = new DotnetWatchTestDirectory();
        using var shutdown = new TestGracefulShutdownWindow();
        using var cancellation = new CancellationTokenSource();
        var firstStarted = new TaskCompletionSource<DotnetWatchAppHostGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementStarted = new TaskCompletionSource<DotnetWatchAppHostGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generations = 0;
        var session = new DotnetWatchAppHostSession(shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, TimeProvider.System, NullLogger.Instance);
        var run = session.RunAsync(async (generation, token) =>
        {
            var number = Interlocked.Increment(ref generations);
            if (number == 1)
            {
                generation.ApplyEnvironment(new Dictionary<string, string>());
                firstStarted.TrySetResult(generation);
            }
            else if (number == 2)
            {
                replacementStarted.TrySetResult(generation);
            }
            else
            {
                return CommandResult.Success();
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return CommandResult.Success();
        }, _ => { }, cancellation.Token);

        try
        {
            (await firstStarted.Task.DefaultTimeout()).RequestReload();
            var replacement = await replacementStarted.Task.DefaultTimeout();
            Assert.True(replacement.IsReplacement);
            Assert.False(replacement.IsEnabled);
            Assert.True(replacement.RequestReload());

            Assert.Equal(CliExitCodes.Success, (await run.DefaultTimeout()).ExitCode);
            Assert.Equal(3, generations);
        }
        finally
        {
            await cancellation.CancelAsync();
            await run.DefaultTimeout();
        }
    }

    [Theory]
    [InlineData("handed-over", TelemetryConstants.RunReloadOutcomes.Succeeded)]
    [InlineData("canceled", TelemetryConstants.RunReloadOutcomes.Canceled)]
    [InlineData("teardown-failed", TelemetryConstants.RunReloadOutcomes.RunFailed)]
    [InlineData("teardown-failed-with-message", TelemetryConstants.RunReloadOutcomes.RunFailed)]
    [InlineData("teardown-threw", TelemetryConstants.RunReloadOutcomes.RunFailed)]
    [InlineData("ended", TelemetryConstants.RunReloadOutcomes.Canceled)]
    [InlineData("ended-with-error", TelemetryConstants.RunReloadOutcomes.RunFailed)]
    public async Task ACtrlRReloadIsHandedToItsReplacementOrEndsWithTheSession(string scenario, string outcome)
    {
        using var fixture = new TelemetryFixture();
        using var directory = new DotnetWatchTestDirectory();
        using var shutdown = new TestGracefulShutdownWindow();
        using var cancellation = new CancellationTokenSource();
        var requested = RunReloadActivity.Start(fixture.Telemetry, parent: null, RunReloadMode.AppHost);
        var generations = 0;
        var session = new DotnetWatchAppHostSession(shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, TimeProvider.System, NullLogger.Instance);
        var run = session.RunAsync(async (generation, token) =>
        {
            if (Interlocked.Increment(ref generations) == 1)
            {
                Assert.Null(generation.PendingReload);
                generation.ApplyEnvironment(new Dictionary<string, string>());
                // As the shortcut listener does: request the replacement, then record the reload that requested it.
                Assert.True(generation.RequestReload());
                generation.RequestedReload = requested;
                if (scenario == "canceled")
                {
                    await cancellation.CancelAsync();
                }
                // The AppHost's run ended on its own as the reload was requested.
                if (scenario == "ended")
                {
                    return CommandResult.Success();
                }
                if (scenario == "ended-with-error")
                {
                    return CommandResult.Failure(CliExitCodes.FailedToDotnetRunAppHost, "The AppHost failed.");
                }
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (scenario is "teardown-failed" or "teardown-failed-with-message")
                {
                    // The AppHost's run usually reports only the exit code of the AppHost that stopped with an error.
                    return scenario == "teardown-failed"
                        ? CommandResult.FromExitCode(1)
                        : CommandResult.Failure(CliExitCodes.FailedToDotnetRunAppHost, "Teardown failed.");
                }
                catch (OperationCanceledException) when (scenario == "teardown-threw")
                {
                    throw new InvalidOperationException("Owned process cleanup failed.");
                }
                return CommandResult.Success();
            }

            Assert.Same(requested, generation.PendingReload);
            requested.Complete(TelemetryConstants.RunReloadOutcomes.Succeeded);
            return CommandResult.Success();
        }, _ => { }, cancellation.Token);

        if (scenario == "teardown-threw")
        {
            // A cleanup failure still ends the run with its exception.
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => run.DefaultTimeout());
            Assert.Equal("Owned process cleanup failed.", exception.Message);
        }
        else
        {
            var result = await run.DefaultTimeout();

            Assert.Equal(scenario switch
            {
                "teardown-failed" => 1,
                "teardown-failed-with-message" or "ended-with-error" => CliExitCodes.FailedToDotnetRunAppHost,
                _ => CliExitCodes.Success
            }, result.ExitCode);
            if (scenario is "teardown-failed" or "teardown-failed-with-message")
            {
                // A replacement isn't started, and the run says why.
                var stopFailed = string.Format(CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostReplacedAppHostFailed, result.ExitCode);
                Assert.Equal(scenario == "teardown-failed" ? stopFailed : $"{stopFailed}{Environment.NewLine}Teardown failed.", result.ErrorMessage);
            }
        }
        Assert.Equal(scenario == "handed-over" ? 2 : 1, generations);
        var reload = Assert.Single(fixture.GetStoppedActivities(TelemetryConstants.Activities.RunAppHostReload));
        Assert.Equal(outcome, reload.GetTagItem(TelemetryConstants.Tags.RunReloadOutcome));
    }

    [Fact]
    public async Task FailedTeardownDoesNotAuthorizeAnotherGeneration()
    {
        using var directory = new DotnetWatchTestDirectory();
        using var shutdown = new TestGracefulShutdownWindow();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<DotnetWatchAppHostGeneration>(TaskCreationOptions.RunContinuationsAsynchronously);
        var generations = 0;
        var restarts = 0;
        var session = new DotnetWatchAppHostSession(shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, TimeProvider.System, NullLogger.Instance);
        var run = session.RunAsync(async (generation, token) =>
        {
            Interlocked.Increment(ref generations);
            generation.ApplyEnvironment(new Dictionary<string, string>());
            started.TrySetResult(generation);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return CommandResult.Success();
            }
            finally
            {
                throw new InvalidOperationException("Owned process cleanup failed.");
            }
        }, _ => Interlocked.Increment(ref restarts), cancellation.Token);

        var first = await started.Task.DefaultTimeout();
        await SendAsync(first, DotnetWatchAppHostProtocol.HostStarted, "123:456");
        await SendAsync(first, DotnetWatchAppHostProtocol.ModelInvalidated, "123:456");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => run.DefaultTimeout());

        Assert.Equal("Owned process cleanup failed.", exception.Message);
        Assert.Equal(1, generations);
        Assert.Equal(0, restarts);
    }

    [Fact]
    public async Task GenerationGracefulShutdownDoesNotExpireTheCommandOrNextGeneration()
    {
        using var directory = new DotnetWatchTestDirectory();
        using var shutdown = new TestGracefulShutdownWindow();
        await using var first = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromMilliseconds(10), directory.RelativePath, NullLogger.Instance);
        first.BeginGracefulWindow();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Delay(Timeout.InfiniteTimeSpan, first.GracefulShutdownToken).DefaultTimeout());
        await using var second = new DotnetWatchAppHostGeneration(shutdown, TimeSpan.FromSeconds(5), directory.RelativePath, NullLogger.Instance);

        Assert.False(shutdown.GracefulShutdownToken.IsCancellationRequested);
        Assert.False(second.GracefulShutdownToken.IsCancellationRequested);
    }

    private static Task SendAsync(DotnetWatchAppHostGeneration generation, int kind, string instance, string? session = null)
        => DotnetWatchAppHostTestChannel.SendAsync(generation.Endpoint, new()
        {
            Version = DotnetWatchAppHostProtocol.Version,
            Session = session ?? generation.Session,
            Kind = kind,
            Instance = instance
        }, TestContext.Current.CancellationToken).DefaultTimeout();
}
