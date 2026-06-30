// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001 // IInteractionService is experimental

using System.Runtime.InteropServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Maui.Annotations;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Maui;
using Aspire.Hosting.Maui.Lifecycle;
using Aspire.Hosting.Maui.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

public class MauiPrerequisiteCheckTests
{
    [Theory]
    [InlineData("maui", true)]
    [InlineData("maui-android", true)]
    [InlineData("MAUI", true)]
    [InlineData("maui-ios", false)]
    [InlineData("wasm-tools", false)]
    public void IsRequiredWorkloadInstalled_DetectsRequiredWorkloadForAndroid(string workloadId, bool expected)
    {
        var output = $$"""
            Workload version: 10.0.100-preview.1.12345
            Installed Workload Id      Manifest Version       Installation Source
            --------------------------------------------------------------------
            {{workloadId}}             10.0.0/10.0.100        SDK 10.0.100
            """;
        var resource = new MauiAndroidEmulatorResource("android", new MauiProjectResource("app", "app.csproj"));

        Assert.Equal(expected, MauiWorkloadChecker.IsRequiredWorkloadInstalled(output, resource));
    }

    [Fact]
    public void IsRequiredWorkloadInstalled_DoesNotAcceptWrongPlatformWorkload()
    {
        var output = """
            Installed Workload Id      Manifest Version       Installation Source
            --------------------------------------------------------------------
            maui-ios                   10.0.0/10.0.100        SDK 10.0.100
            """;
        var resource = new MauiAndroidEmulatorResource("android", new MauiProjectResource("app", "app.csproj"));

        Assert.False(MauiWorkloadChecker.IsRequiredWorkloadInstalled(output, resource));
    }

    [Fact]
    public void IsRequiredWorkloadInstalled_AcceptsMatchingPlatformWorkload()
    {
        var output = """
            Installed Workload Id      Manifest Version       Installation Source
            --------------------------------------------------------------------
            maui-android               10.0.0/10.0.100        SDK 10.0.100
            """;
        var resource = new MauiAndroidEmulatorResource("android", new MauiProjectResource("app", "app.csproj"));

        Assert.True(MauiWorkloadChecker.IsRequiredWorkloadInstalled(output, resource));
    }

    [Fact]
    public async Task MissingMauiWorkload_ThrowsActionableExceptionAndShowsNotification()
    {
        await using var env = await PrerequisiteTestEnvironment.CreateAsync([
            TestableChecker.Missing(".NET MAUI workload", resource => resource is IMauiPlatformResource),
            TestableChecker.Available("Android SDK", resource => resource is MauiAndroidEmulatorResource)
        ]);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => env.PublishBeforeResourceStartedAsync(env.Android));

        Assert.Contains(".NET MAUI workload", exception.Message);
        Assert.Contains("dotnet workload install maui", exception.Message);
        Assert.Equal(1, env.InteractionService.NotificationCount);
        Assert.Contains(".NET MAUI workload", env.InteractionService.LastNotificationMessage);
    }

    [Fact]
    public async Task AndroidPrerequisiteMissing_OnlyBlocksAndroidResources()
    {
        var androidSdk = TestableChecker.Missing("Android SDK", resource => resource is MauiAndroidDeviceResource or MauiAndroidEmulatorResource);

        await using var env = await PrerequisiteTestEnvironment.CreateAsync([
            TestableChecker.Available(".NET MAUI workload", resource => resource is IMauiPlatformResource),
            androidSdk,
            TestableChecker.Available("Xcode", resource => resource is MauiiOSSimulatorResource)
        ]);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => env.PublishBeforeResourceStartedAsync(env.Android));

        Assert.Contains("Android SDK", exception.Message);

        await env.PublishBeforeResourceStartedAsync(env.IOSSimulator);
        Assert.Equal(1, androidSdk.CheckCount);
    }

    [Fact]
    public async Task XcodeChecker_DoesNotApplyToIosOrMacCatalystOnNonMacOS()
    {
        var processRunner = new FakeProcessRunner(_ => throw new InvalidOperationException("xcode-select should not be invoked"));
        var xcodeChecker = new XcodeChecker(processRunner, isMacOS: () => false);

        Assert.False(xcodeChecker.AppliesTo(new MauiiOSSimulatorResource("ios-simulator", new MauiProjectResource("app", "app.csproj"))));
        Assert.False(xcodeChecker.AppliesTo(new MauiMacCatalystPlatformResource("maccatalyst", new MauiProjectResource("app", "app.csproj"))));

        await using var env = await PrerequisiteTestEnvironment.CreateAsync([
            TestableChecker.Available(".NET MAUI workload", resource => resource is IMauiPlatformResource),
            xcodeChecker
        ]);

        await env.PublishBeforeResourceStartedAsync(env.IOSSimulator);
        Assert.Equal(0, processRunner.CallCount);
    }

    [Fact]
    public async Task XcodeChecker_MissingOnMacOS_ReturnsActionableFailure()
    {
        var processRunner = new FakeProcessRunner(_ => new ProcessResult(0, "/Library/Developer/CommandLineTools\n", ""));
        var checker = new XcodeChecker(processRunner, isMacOS: () => true);
        var resource = new MauiiOSSimulatorResource("ios-simulator", new MauiProjectResource("app", "app.csproj"));

        var result = await checker.CheckAsync(resource, NullLogger.Instance, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("not a full Xcode installation", result.Details);
    }

    [Fact]
    public async Task AndroidSdkChecker_AndroidDeviceRequiresAdbSdk()
    {
        var checker = new AndroidSdkChecker(findSdkPath: () => null, hasEmulatorTool: _ => true);
        var resource = new MauiAndroidDeviceResource("android-device", new MauiProjectResource("app", "app.csproj"));

        var result = await checker.CheckAsync(resource, NullLogger.Instance, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("platform-tools/adb", result.Details);
    }

    [Fact]
    public async Task AndroidSdkChecker_AndroidEmulatorRequiresEmulatorTool()
    {
        var checker = new AndroidSdkChecker(findSdkPath: () => "/android-sdk", hasEmulatorTool: _ => false);
        var resource = new MauiAndroidEmulatorResource("android-emulator", new MauiProjectResource("app", "app.csproj"));

        var result = await checker.CheckAsync(resource, NullLogger.Instance, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("Android emulator tool", result.Details);
    }

    [Fact]
    public async Task SuccessfulPrerequisites_DoNotShowNotificationOrBlockStart()
    {
        await using var env = await PrerequisiteTestEnvironment.CreateAsync([
            TestableChecker.Available(".NET MAUI workload", resource => resource is IMauiPlatformResource),
            TestableChecker.Available("Android SDK", resource => resource is MauiAndroidEmulatorResource)
        ]);

        await env.PublishBeforeResourceStartedAsync(env.Android);

        Assert.Equal(0, env.InteractionService.NotificationCount);
    }

    [Fact]
    public async Task FailedPrerequisites_AreRecheckedAfterRetry()
    {
        var workload = TestableChecker.Missing(".NET MAUI workload", resource => resource is IMauiPlatformResource);
        await using var env = await PrerequisiteTestEnvironment.CreateAsync([workload]);

        await Assert.ThrowsAsync<DistributedApplicationException>(
            () => env.PublishBeforeResourceStartedAsync(env.Android));

        workload.Result = MauiPrerequisiteCheckResult.Available;

        await env.PublishBeforeResourceStartedAsync(env.Android);
        Assert.Equal(2, workload.CheckCount);
    }

    [Fact]
    public async Task SuccessfulPrerequisites_AreCachedPerChecker()
    {
        var workload = TestableChecker.Available(".NET MAUI workload", resource => resource is IMauiPlatformResource);
        var android = TestableChecker.Available("Android SDK", resource => resource is MauiAndroidEmulatorResource);
        await using var env = await PrerequisiteTestEnvironment.CreateAsync([workload, android]);

        await env.PublishBeforeResourceStartedAsync(env.Android);
        await env.PublishBeforeResourceStartedAsync(env.Android);

        Assert.Equal(1, workload.CheckCount);
        Assert.Equal(1, android.CheckCount);
    }

    [Fact]
    public async Task SuccessfulAndroidDeviceCheck_DoesNotSkipAndroidEmulatorToolCheck()
    {
        var android = TestableChecker.Available("Android SDK", resource => resource is MauiAndroidDeviceResource or MauiAndroidEmulatorResource);
        await using var env = await PrerequisiteTestEnvironment.CreateAsync([android]);

        await env.PublishBeforeResourceStartedAsync(env.AndroidDevice);
        await env.PublishBeforeResourceStartedAsync(env.Android);

        Assert.Equal(2, android.CheckCount);
    }

    [Fact]
    public async Task SuccessfulWorkloadCheck_DoesNotSkipSamePlatformResourceFromDifferentProjectDirectory()
    {
        var workload = new TestableChecker(".NET MAUI workload", resource => resource is IMauiPlatformResource)
        {
            CacheKeyCallback = resource => resource is IMauiPlatformResource mauiResource
                ? $"{mauiResource.Parent.ProjectPath}:{resource.GetType().FullName}"
                : resource.Name
        };
        await using var env = await PrerequisiteTestEnvironment.CreateAsync([workload]);

        await env.PublishBeforeResourceStartedAsync(env.Android);
        await env.PublishBeforeResourceStartedAsync(env.AndroidFromSecondProject);

        Assert.Equal(2, workload.CheckCount);
    }

    [Fact]
    public async Task CancellationDuringCheck_PropagatesAndDoesNotShowNotification()
    {
        var checkerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChecker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new TestableChecker(".NET MAUI workload", resource => resource is IMauiPlatformResource)
        {
            CheckCallback = async cancellationToken =>
            {
                checkerStarted.SetResult();
                await releaseChecker.Task.WaitAsync(cancellationToken);
                return MauiPrerequisiteCheckResult.Available;
            }
        };

        await using var env = await PrerequisiteTestEnvironment.CreateAsync([checker]);
        using var cts = new CancellationTokenSource();

        var publishTask = env.PublishBeforeResourceStartedAsync(env.Android, cts.Token);
        await checkerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => publishTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, env.InteractionService.NotificationCount);
    }

    [Fact]
    public async Task CancellingOneCallerDoesNotCancelSharedPrerequisiteCheckForOtherResources()
    {
        var checkerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChecker = new TaskCompletionSource<MauiPrerequisiteCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new TestableChecker(".NET MAUI workload", resource => resource is IMauiPlatformResource)
        {
            CheckCallback = async _ =>
            {
                checkerStarted.SetResult();
                return await releaseChecker.Task.ConfigureAwait(false);
            }
        };

        await using var env = await PrerequisiteTestEnvironment.CreateAsync([checker]);
        using var canceledStartCts = new CancellationTokenSource();

        var canceledStart = env.PublishBeforeResourceStartedAsync(env.Android, canceledStartCts.Token);
        await checkerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stillStarting = env.PublishBeforeResourceStartedAsync(env.AndroidFromSecondProject);

        canceledStartCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledStart.WaitAsync(TimeSpan.FromSeconds(5)));

        releaseChecker.SetResult(MauiPrerequisiteCheckResult.Available);

        await stillStarting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, checker.CheckCount);
    }

    [Fact]
    public async Task WorkloadChecker_ProcessFailureReportsMissingPrerequisite()
    {
        var checker = new MauiWorkloadChecker(new FakeProcessRunner(_ => throw new InvalidOperationException("dotnet was not found")));
        var resource = new MauiAndroidEmulatorResource("android", new MauiProjectResource("app", "app.csproj"));

        var result = await checker.CheckAsync(resource, NullLogger.Instance, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("dotnet workload list", result.Details);
        Assert.Contains("dotnet was not found", result.Details);
    }

    [Fact]
    public async Task WorkloadChecker_RunsFromMauiBuildWorkingDirectory()
    {
        var processRunner = new FakeProcessRunner(_ => new ProcessResult(0, """
            Installed Workload Id      Manifest Version       Installation Source
            --------------------------------------------------------------------
            maui                       10.0.0/10.0.100        SDK 10.0.100
            """, ""));
        var resource = new MauiAndroidEmulatorResource("android", new MauiProjectResource("app", "/repo/src/MauiApp/MauiApp.csproj"));
        resource.Annotations.Add(new MauiBuildInfoAnnotation("/repo/src/MauiApp/MauiApp.csproj", "/repo/src/MauiApp", "net10.0-android"));
        var checker = new MauiWorkloadChecker(processRunner);

        await checker.CheckAsync(resource, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("/repo/src/MauiApp", processRunner.WorkingDirectory);
    }

    [Fact]
    public async Task WorkloadChecker_SharesConcurrentWorkloadListOutputAcrossPlatformsInSameProjectDirectory()
    {
        var processStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProcess = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processRunner = new FakeProcessRunner(_ => throw new InvalidOperationException("Synchronous callback should not be used."))
        {
            AsyncCallback = async _ =>
            {
                processStarted.TrySetResult();
                return await releaseProcess.Task.ConfigureAwait(false);
            }
        };
        var parent = new MauiProjectResource("app", "/repo/src/MauiApp/MauiApp.csproj");
        var android = new MauiAndroidEmulatorResource("android", parent);
        var ios = new MauiiOSSimulatorResource("ios", parent);
        android.Annotations.Add(new MauiBuildInfoAnnotation("/repo/src/MauiApp/MauiApp.csproj", "/repo/src/MauiApp", "net10.0-android"));
        ios.Annotations.Add(new MauiBuildInfoAnnotation("/repo/src/MauiApp/MauiApp.csproj", "/repo/src/MauiApp", "net10.0-ios"));
        var checker = new MauiWorkloadChecker(processRunner);

        var androidCheck = checker.CheckAsync(android, NullLogger.Instance, CancellationToken.None);
        await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var iosCheck = checker.CheckAsync(ios, NullLogger.Instance, CancellationToken.None);
        await Task.Delay(100);

        Assert.Equal(1, processRunner.CallCount);

        releaseProcess.SetResult(new ProcessResult(0, """
            Installed Workload Id      Manifest Version       Installation Source
            --------------------------------------------------------------------
            maui                       10.0.0/10.0.100        SDK 10.0.100
            """, ""));

        Assert.True((await androidCheck).IsAvailable);
        Assert.True((await iosCheck).IsAvailable);
        Assert.Equal(1, processRunner.CallCount);
    }

    [Fact]
    public async Task WorkloadChecker_CancellingOneCallerDoesNotCancelSharedWorkloadListForOtherResources()
    {
        var processStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProcess = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processRunner = new FakeProcessRunner(_ => throw new InvalidOperationException("Synchronous callback should not be used."))
        {
            AsyncCallback = async _ =>
            {
                processStarted.SetResult();
                return await releaseProcess.Task.ConfigureAwait(false);
            }
        };
        var parent = new MauiProjectResource("app", "/repo/src/MauiApp/MauiApp.csproj");
        var android = new MauiAndroidEmulatorResource("android", parent);
        var ios = new MauiiOSSimulatorResource("ios", parent);
        android.Annotations.Add(new MauiBuildInfoAnnotation("/repo/src/MauiApp/MauiApp.csproj", "/repo/src/MauiApp", "net10.0-android"));
        ios.Annotations.Add(new MauiBuildInfoAnnotation("/repo/src/MauiApp/MauiApp.csproj", "/repo/src/MauiApp", "net10.0-ios"));
        var checker = new MauiWorkloadChecker(processRunner);
        using var canceledCheckCts = new CancellationTokenSource();

        var canceledCheck = checker.CheckAsync(android, NullLogger.Instance, canceledCheckCts.Token);
        await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stillChecking = checker.CheckAsync(ios, NullLogger.Instance, CancellationToken.None);

        canceledCheckCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledCheck.WaitAsync(TimeSpan.FromSeconds(5)));

        releaseProcess.SetResult(new ProcessResult(0, """
            Installed Workload Id      Manifest Version       Installation Source
            --------------------------------------------------------------------
            maui                       10.0.0/10.0.100        SDK 10.0.100
            """, ""));

        Assert.True((await stillChecking.WaitAsync(TimeSpan.FromSeconds(5))).IsAvailable);
        Assert.Equal(1, processRunner.CallCount);
    }

    [Fact]
    public async Task WorkloadChecker_DoesNotCacheCompletedWorkloadListAcrossRetries()
    {
        var invocationCount = 0;
        var processRunner = new FakeProcessRunner(_ =>
        {
            return Interlocked.Increment(ref invocationCount) == 1
                ? new ProcessResult(0, "There are no installed workloads to display.\n", "")
                : new ProcessResult(0, """
                    Installed Workload Id      Manifest Version       Installation Source
                    --------------------------------------------------------------------
                    maui                       10.0.0/10.0.100        SDK 10.0.100
                    """, "");
        });
        var resource = new MauiAndroidEmulatorResource("android", new MauiProjectResource("app", "app.csproj"));
        var checker = new MauiWorkloadChecker(processRunner);

        var missing = await checker.CheckAsync(resource, NullLogger.Instance, CancellationToken.None);
        var available = await checker.CheckAsync(resource, NullLogger.Instance, CancellationToken.None);

        Assert.False(missing.IsAvailable);
        Assert.True(available.IsAvailable);
        Assert.Equal(2, processRunner.CallCount);
    }

    [Fact]
    public void WorkloadChecker_PrefersDotNetHostPathWhenAvailable()
    {
        var dotnetHostPath = OperatingSystem.IsWindows() ? @"C:\Program Files\dotnet\dotnet.exe" : "/usr/local/share/dotnet/dotnet";

        var dotnetPath = MauiWorkloadChecker.ResolveDotNetExecutable(
            name => string.Equals(name, "DOTNET_HOST_PATH", StringComparison.Ordinal) ? dotnetHostPath : null,
            path => string.Equals(path, dotnetHostPath, StringComparison.Ordinal));

        Assert.Equal(dotnetHostPath, dotnetPath);
    }

    [Fact]
    public void WorkloadChecker_PrefersDotNetRootForCurrentArchitecture()
    {
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        var dotnetRoot = OperatingSystem.IsWindows() ? @"C:\repo\.dotnet" : "/repo/.dotnet";
        var expectedDotNetPath = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        var dotnetPath = MauiWorkloadChecker.ResolveDotNetExecutable(
            name => string.Equals(name, $"DOTNET_ROOT_{architecture}", StringComparison.Ordinal) ? dotnetRoot : null,
            path => string.Equals(path, expectedDotNetPath, StringComparison.Ordinal));

        Assert.Equal(expectedDotNetPath, dotnetPath);
    }

    [Fact]
    public async Task WorkloadChecker_ProcessTimeoutReportsMissingPrerequisite()
    {
        var checker = new MauiWorkloadChecker(new FakeProcessRunner(_ => throw new TimeoutException("command timed out")));
        var resource = new MauiAndroidEmulatorResource("android", new MauiProjectResource("app", "app.csproj"));

        var result = await checker.CheckAsync(resource, NullLogger.Instance, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("command timed out", result.Details);
    }

    [Fact]
    public async Task PrerequisiteFailureRunsBeforeBuildQueue()
    {
        await using var env = await PrerequisiteTestEnvironment.CreateAsync([
            TestableChecker.Missing(".NET MAUI workload", resource => resource is IMauiPlatformResource)
        ]);
        var buildQueueProbe = new BuildQueueProbeSubscriber();

        await buildQueueProbe.SubscribeAsync(env.Eventing, new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), CancellationToken.None);

        await Assert.ThrowsAsync<DistributedApplicationException>(
            () => env.PublishBeforeResourceStartedAsync(env.Android));

        Assert.False(buildQueueProbe.WasCalled);
    }

    [Fact]
    public void AddMauiHostingServices_RegistersPrerequisiteChecksBeforeBuildQueue()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddMauiHostingServices();

        var subscriberTypes = appBuilder.Services
            .Where(static descriptor => descriptor.ServiceType == typeof(IDistributedApplicationEventingSubscriber))
            .Select(static descriptor => descriptor.ImplementationType)
            .ToList();
        var prerequisiteIndex = subscriberTypes.IndexOf(typeof(MauiPrerequisiteCheckEventSubscriber));
        var buildQueueIndex = subscriberTypes.IndexOf(typeof(MauiBuildQueueEventSubscriber));

        Assert.NotEqual(-1, prerequisiteIndex);
        Assert.NotEqual(-1, buildQueueIndex);
        Assert.True(
            prerequisiteIndex < buildQueueIndex,
            "MAUI prerequisite checks must subscribe before the build queue so missing tooling fails before any build/device-selection work starts.");
    }

    [Fact]
    public async Task Subscriber_DoesNotSubscribeInPublishMode()
    {
        var checker = TestableChecker.Missing(".NET MAUI workload", resource => resource is IMauiPlatformResource);
        await using var env = await PrerequisiteTestEnvironment.CreateAsync([checker], subscribe: false);

        await env.Subscriber.SubscribeAsync(
            env.Eventing,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            CancellationToken.None);

        await env.PublishBeforeResourceStartedAsync(env.Android);
        Assert.Equal(0, checker.CheckCount);
    }

    private sealed class PrerequisiteTestEnvironment : IAsyncDisposable
    {
        public required DistributedApplication App { get; init; }
        public required MauiAndroidEmulatorResource Android { get; init; }
        public required MauiAndroidEmulatorResource AndroidFromSecondProject { get; init; }
        public required MauiAndroidDeviceResource AndroidDevice { get; init; }
        public required MauiiOSSimulatorResource IOSSimulator { get; init; }
        public required TestInteractionService InteractionService { get; init; }
        public required MauiPrerequisiteCheckEventSubscriber Subscriber { get; init; }

        public IDistributedApplicationEventing Eventing => App.Services.GetRequiredService<IDistributedApplicationEventing>();

        public Task PublishBeforeResourceStartedAsync(IResource resource, CancellationToken cancellationToken = default)
        {
            return Eventing.PublishAsync(new BeforeResourceStartedEvent(resource, App.Services), cancellationToken);
        }

        public static async Task<PrerequisiteTestEnvironment> CreateAsync(IEnumerable<IMauiPrerequisiteChecker> checkers, bool subscribe = true)
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var parent = new MauiProjectResource("mauiapp", "/fake/path.csproj");
            appBuilder.CreateResourceBuilder(parent);

            var android = new MauiAndroidEmulatorResource("android", parent);
            appBuilder.AddResource(android);

            var androidDevice = new MauiAndroidDeviceResource("android-device", parent);
            appBuilder.AddResource(androidDevice);

            var iosSimulator = new MauiiOSSimulatorResource("ios-simulator", parent);
            appBuilder.AddResource(iosSimulator);

            var secondParent = new MauiProjectResource("mauiapp2", "/other/fake/path.csproj");
            appBuilder.CreateResourceBuilder(secondParent);

            var androidFromSecondProject = new MauiAndroidEmulatorResource("android2", secondParent);
            appBuilder.AddResource(androidFromSecondProject);

            var app = appBuilder.Build();
            var interactionService = new TestInteractionService();
            var subscriber = new MauiPrerequisiteCheckEventSubscriber(
                checkers,
                interactionService,
                app.Services.GetRequiredService<ResourceLoggerService>(),
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<MauiPrerequisiteCheckEventSubscriber>());

            if (subscribe)
            {
                await subscriber.SubscribeAsync(
                    app.Services.GetRequiredService<IDistributedApplicationEventing>(),
                    new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
                    CancellationToken.None);
            }

            return new PrerequisiteTestEnvironment
            {
                App = app,
                Android = android,
                AndroidFromSecondProject = androidFromSecondProject,
                AndroidDevice = androidDevice,
                IOSSimulator = iosSimulator,
                InteractionService = interactionService,
                Subscriber = subscriber
            };
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
        }
    }

    private sealed class TestableChecker(string name, Func<IResource, bool> appliesTo) : IMauiPrerequisiteChecker
    {
        private int _checkCount;

        public string Name => name;

        public string InstallHint => name == ".NET MAUI workload" ? "Run `dotnet workload install maui`." : $"Install {name}.";

        public string DocumentationUrl => $"https://example.com/{Uri.EscapeDataString(name)}";

        public MauiPrerequisiteCheckResult Result { get; set; } = MauiPrerequisiteCheckResult.Available;

        public Func<CancellationToken, Task<MauiPrerequisiteCheckResult>>? CheckCallback { get; init; }

        public Func<IResource, string>? CacheKeyCallback { get; init; }

        public int CheckCount => _checkCount;

        public static TestableChecker Available(string name, Func<IResource, bool> appliesTo)
        {
            return new(name, appliesTo);
        }

        public static TestableChecker Missing(string name, Func<IResource, bool> appliesTo)
        {
            return new(name, appliesTo)
            {
                Result = MauiPrerequisiteCheckResult.Missing($"{name} is missing.")
            };
        }

        public bool AppliesTo(IResource resource) => appliesTo(resource);

        public string GetCacheKey(IResource resource)
        {
            return CacheKeyCallback?.Invoke(resource) ?? $"{Name}:{resource.GetType().FullName}";
        }

        public async Task<MauiPrerequisiteCheckResult> CheckAsync(IResource resource, ILogger logger, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _checkCount);

            if (CheckCallback is not null)
            {
                return await CheckCallback(cancellationToken).ConfigureAwait(false);
            }

            return Result;
        }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly Func<IReadOnlyList<string>, ProcessResult> _callback;
        private int _callCount;

        public FakeProcessRunner(Func<IReadOnlyList<string>, ProcessResult> callback)
        {
            _callback = callback;
        }

        public Func<IReadOnlyList<string>, Task<ProcessResult>>? AsyncCallback { get; init; }

        public int CallCount => _callCount;

        public string? WorkingDirectory { get; private set; }

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            WorkingDirectory = workingDirectory;
            return AsyncCallback is not null ? AsyncCallback(arguments) : Task.FromResult(_callback(arguments));
        }
    }

    private sealed class BuildQueueProbeSubscriber : IDistributedApplicationEventingSubscriber
    {
        public bool WasCalled { get; private set; }

        public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
        {
            eventing.Subscribe<BeforeResourceStartedEvent>((@event, _) =>
            {
                WasCalled = true;
                return Task.CompletedTask;
            });

            return Task.CompletedTask;
        }
    }

    private sealed class TestInteractionService : IInteractionService
    {
        private int _notificationCount;

        public bool IsAvailable => true;

        public int NotificationCount => _notificationCount;

        public string LastNotificationMessage { get; private set; } = string.Empty;

        public Task<InteractionResult<bool>> PromptNotificationAsync(string title, string message, NotificationInteractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _notificationCount);
            LastNotificationMessage = message;
            return Task.FromResult(InteractionResult.Ok(false));
        }

        public Task<InteractionResult<bool>> PromptConfirmationAsync(string title, string message, MessageBoxInteractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InteractionResult<bool>> PromptMessageBoxAsync(string title, string message, MessageBoxInteractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InteractionResult<InteractionInput>> PromptInputAsync(string title, string? message, string inputLabel, string placeHolder, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InteractionResult<InteractionInput>> PromptInputAsync(string title, string? message, InteractionInput input, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InteractionResult<InteractionInputCollection>> PromptInputsAsync(string title, string? message, IReadOnlyList<InteractionInput> inputs, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InteractionResult<bool>> PromptProgressAsync(string message, string? title = null, ProgressInteractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
