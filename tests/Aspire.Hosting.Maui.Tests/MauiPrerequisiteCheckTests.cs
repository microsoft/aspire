// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001 // IInteractionService is experimental

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
        var notification = await env.ReadNotificationAsync();
        Assert.Contains(".NET MAUI workload", notification.Message);
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
    public void XcodeChecker_StaleXcodeSelectionWithoutPlatformsDirectoryIsNotFullXcode()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var staleDeveloperDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "Xcode.app", "Contents", "Developer"));

            Assert.False(XcodeChecker.IsFullXcodePath(staleDeveloperDirectory.FullName));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void XcodeChecker_DeveloperDirectoryWithPlatformsDirectoryIsFullXcode()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var developerDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "Xcode.app", "Contents", "Developer"));
            Directory.CreateDirectory(Path.Combine(developerDirectory.FullName, "Platforms"));

            Assert.True(XcodeChecker.IsFullXcodePath(developerDirectory.FullName));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AndroidSdkChecker_AndroidDeviceRequiresAdbSdk()
    {
        var checker = new AndroidSdkChecker(findSdkPath: () => null);
        var resource = new MauiAndroidDeviceResource("android-device", new MauiProjectResource("app", "app.csproj"));

        var result = await checker.CheckAsync(resource, NullLogger.Instance, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("platform-tools/adb", result.Details);
    }

    [Fact]
    public async Task AndroidSdkChecker_AndroidEmulatorRequiresOnlyAdbSdk()
    {
        var checker = new AndroidSdkChecker(
            new FakeProcessRunner(_ => throw new InvalidOperationException("Project-configured SDK lookup should not be used without build info.")),
            (_, _, _) => Task.FromResult<string?>(null),
            findSdkPath: () => "/android-sdk",
            hasAdbTool: _ => true);
        var resource = new MauiAndroidEmulatorResource("android-emulator", new MauiProjectResource("app", "app.csproj"));

        var result = await checker.CheckAsync(resource, NullLogger.Instance, CancellationToken.None);

        Assert.True(result.IsAvailable);
    }

    [Fact]
    public async Task AndroidSdkChecker_UsesProjectConfiguredAndroidSdkDirectory()
    {
        var configuredSdkPath = OperatingSystem.IsWindows() ? @"C:\android-sdk" : "/android-sdk";
        var checkerProcessRunner = new FakeProcessRunner(_ => new ProcessResult(0, configuredSdkPath, ""));
        var checker = new AndroidSdkChecker(
            checkerProcessRunner,
            getConfiguredSdkPathAsync: null,
            findSdkPath: () => null,
            hasAdbTool: path => string.Equals(path, configuredSdkPath, StringComparison.Ordinal));
        var resource = new MauiAndroidDeviceResource("android-device", new MauiProjectResource("app", "/repo/src/MauiApp/MauiApp.csproj"));
        resource.Annotations.Add(new MauiBuildInfoAnnotation("/repo/src/MauiApp/MauiApp.csproj", "/repo/src/MauiApp", "net10.0-android", "Debug"));

        var result = await checker.CheckAsync(resource, NullLogger.Instance, CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal("dotnet", checkerProcessRunner.FileName);
        Assert.Equal("/repo/src/MauiApp", checkerProcessRunner.WorkingDirectory);
        Assert.Collection(
            checkerProcessRunner.Arguments,
            arg => Assert.Equal("msbuild", arg),
            arg => Assert.Equal("/repo/src/MauiApp/MauiApp.csproj", arg),
            arg => Assert.Equal("-p:TargetFramework=net10.0-android", arg),
            arg => Assert.Equal("-p:Configuration=Debug", arg),
            arg => Assert.Equal("-getProperty:AndroidSdkDirectory", arg),
            arg => Assert.Equal("-nologo", arg));
        Assert.Collection(
            checkerProcessRunner.EnvironmentVariables.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            pair =>
            {
                Assert.Equal(KnownConfigNames.DotnetCliTelemetryOptOut, pair.Key);
                Assert.Equal("1", pair.Value);
            },
            pair =>
            {
                Assert.Equal(KnownConfigNames.DotnetCliWorkloadUpdateNotifyDisable, pair.Key);
                Assert.Equal("1", pair.Value);
            });
    }

    [Fact]
    public async Task AndroidSdkChecker_ProjectConfiguredAndroidSdkDirectoryMustContainExecutableAdb()
    {
        var configuredSdkPath = OperatingSystem.IsWindows() ? @"C:\android-sdk" : "/android-sdk";
        var checker = new AndroidSdkChecker(
            new FakeProcessRunner(_ => new ProcessResult(0, configuredSdkPath, "")),
            getConfiguredSdkPathAsync: null,
            findSdkPath: () => throw new InvalidOperationException("Global SDK lookup should not be used when AndroidSdkDirectory is configured."),
            hasAdbTool: _ => false);
        var resource = new MauiAndroidDeviceResource("android-device", new MauiProjectResource("app", "/repo/src/MauiApp/MauiApp.csproj"));
        resource.Annotations.Add(new MauiBuildInfoAnnotation("/repo/src/MauiApp/MauiApp.csproj", "/repo/src/MauiApp", "net10.0-android"));

        var result = await checker.CheckAsync(resource, NullLogger.Instance, CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("executable `platform-tools/adb`", result.Details);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "UnixFileMode does not describe Windows ACLs")]
    public void AndroidSdkChecker_SdkDerivedAdbMustHaveUnixExecuteBit()
    {
        var tempDirectory = Directory.CreateTempSubdirectory();
        try
        {
            var platformToolsDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory.FullName, "platform-tools"));
            var adbPath = Path.Combine(platformToolsDirectory.FullName, "adb");
            File.WriteAllText(adbPath, string.Empty);

            // CA1416 does not understand SkipOnPlatform, which already keeps this off Windows.
#pragma warning disable CA1416
            File.SetUnixFileMode(adbPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416

            Assert.False(AndroidSdkChecker.IsValidSdkPath(tempDirectory.FullName));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AndroidSdkChecker_ParseAndroidSdkDirectoryIgnoresMultilineOutput()
    {
        var sdkPath = OperatingSystem.IsWindows() ? @"C:\android-sdk" : "/android-sdk";
        var output = $"""
            Workload updates are available. Run `dotnet workload list` for more information.
            {sdkPath}
            """;

        var result = AndroidSdkChecker.ParseAndroidSdkDirectory(output);

        Assert.Null(result);
    }

    [Fact]
    public async Task AndroidSdkChecker_SuccessfulConfiguredSdkCheckDoesNotSkipSamePlatformResourceFromDifferentProject()
    {
        var firstProjectPath = "/repo/src/FirstMauiApp/FirstMauiApp.csproj";
        var secondProjectPath = "/repo/src/SecondMauiApp/SecondMauiApp.csproj";
        var firstSdkPath = OperatingSystem.IsWindows() ? @"C:\first-android-sdk" : "/first-android-sdk";
        var secondSdkPath = OperatingSystem.IsWindows() ? @"C:\second-android-sdk" : "/second-android-sdk";
        var processRunner = new FakeProcessRunner(args =>
        {
            return args[1] switch
            {
                var projectPath when string.Equals(projectPath, firstProjectPath, StringComparison.Ordinal) => new ProcessResult(0, firstSdkPath, ""),
                var projectPath when string.Equals(projectPath, secondProjectPath, StringComparison.Ordinal) => new ProcessResult(0, secondSdkPath, ""),
                _ => throw new InvalidOperationException("Unexpected project path.")
            };
        });
        var checker = new AndroidSdkChecker(
            processRunner,
            getConfiguredSdkPathAsync: null,
            findSdkPath: () => null,
            hasAdbTool: path => string.Equals(path, firstSdkPath, StringComparison.Ordinal));
        await using var env = await PrerequisiteTestEnvironment.CreateAsync([checker]);
        env.Android.Annotations.Add(new MauiBuildInfoAnnotation(firstProjectPath, Path.GetDirectoryName(firstProjectPath)!, "net10.0-android"));
        env.AndroidFromSecondProject.Annotations.Add(new MauiBuildInfoAnnotation(secondProjectPath, Path.GetDirectoryName(secondProjectPath)!, "net10.0-android"));

        await env.PublishBeforeResourceStartedAsync(env.Android);
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => env.PublishBeforeResourceStartedAsync(env.AndroidFromSecondProject));

        Assert.Contains("executable `platform-tools/adb`", exception.Message);
        Assert.Equal(2, processRunner.CallCount);
    }

    [Fact]
    public async Task SuccessfulPrerequisites_DoNotShowNotificationOrBlockStart()
    {
        await using var env = await PrerequisiteTestEnvironment.CreateAsync([
            TestableChecker.Available(".NET MAUI workload", resource => resource is IMauiPlatformResource),
            TestableChecker.Available("Android SDK", resource => resource is MauiAndroidEmulatorResource)
        ]);

        await env.PublishBeforeResourceStartedAsync(env.Android);

        Assert.False(env.TryCompletePendingInteraction());
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
        Assert.False(env.TryCompletePendingInteraction());
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
    public async Task CancelledCallerDoesNotRemoveSharedPrerequisiteCheckBeforeItCompletes()
    {
        var checkerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChecker = new TaskCompletionSource<MauiPrerequisiteCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new TestableChecker(".NET MAUI workload", resource => resource is IMauiPlatformResource)
        {
            CheckCallback = async _ =>
            {
                checkerStarted.TrySetResult();
                return await releaseChecker.Task.ConfigureAwait(false);
            }
        };

        await using var env = await PrerequisiteTestEnvironment.CreateAsync([checker]);
        using var canceledStartCts = new CancellationTokenSource();

        var canceledStart = env.PublishBeforeResourceStartedAsync(env.Android, canceledStartCts.Token);
        await checkerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        canceledStartCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledStart.WaitAsync(TimeSpan.FromSeconds(5)));

        var laterStart = env.PublishBeforeResourceStartedAsync(env.AndroidFromSecondProject);
        await Task.Delay(100);
        Assert.Equal(1, checker.CheckCount);

        releaseChecker.SetResult(MauiPrerequisiteCheckResult.Available);

        await laterStart.WaitAsync(TimeSpan.FromSeconds(5));
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
    public async Task WorkloadChecker_CancelledCallerDoesNotRemoveSharedWorkloadListBeforeItCompletes()
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
        using var canceledCheckCts = new CancellationTokenSource();

        var canceledCheck = checker.CheckAsync(android, NullLogger.Instance, canceledCheckCts.Token);
        await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        canceledCheckCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledCheck.WaitAsync(TimeSpan.FromSeconds(5)));

        var laterCheck = checker.CheckAsync(ios, NullLogger.Instance, CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(1, processRunner.CallCount);

        releaseProcess.SetResult(new ProcessResult(0, """
            Installed Workload Id      Manifest Version       Installation Source
            --------------------------------------------------------------------
            maui                       10.0.0/10.0.100        SDK 10.0.100
            """, ""));

        Assert.True((await laterCheck.WaitAsync(TimeSpan.FromSeconds(5))).IsAvailable);
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
    public async Task WorkloadChecker_UsesPathResolvedDotNetToMatchBuildAndLaunch()
    {
        var processRunner = new FakeProcessRunner(_ => new ProcessResult(0, """
            Installed Workload Id      Manifest Version       Installation Source
            --------------------------------------------------------------------
            maui                       10.0.0/10.0.100        SDK 10.0.100
            """, ""));
        var resource = new MauiAndroidEmulatorResource("android", new MauiProjectResource("app", "app.csproj"));
        var checker = new MauiWorkloadChecker(processRunner);

        await checker.CheckAsync(resource, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("dotnet", processRunner.FileName);
        Assert.Collection(
            processRunner.EnvironmentVariables.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            pair =>
            {
                Assert.Equal(KnownConfigNames.DotnetCliTelemetryOptOut, pair.Key);
                Assert.Equal("1", pair.Value);
            },
            pair =>
            {
                Assert.Equal(KnownConfigNames.DotnetCliWorkloadUpdateNotifyDisable, pair.Key);
                Assert.Equal("1", pair.Value);
            });
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
    public async Task ProcessRunner_TimesOutWhenExitedProcessLeavesOutputStreamsOpen()
    {
        var processRunner = new ProcessRunner();
        var (fileName, arguments) = CreateProcessWithChildThatInheritsOutputHandles();

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => processRunner.RunAsync(
                fileName,
                arguments,
                workingDirectory: null,
                timeout: TimeSpan.FromMilliseconds(500),
                environmentVariables: null,
                cancellationToken: CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Contains("did not complete within", exception.Message);
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

    private static (string FileName, IReadOnlyList<string> Arguments) CreateProcessWithChildThatInheritsOutputHandles()
    {
        // Start a shell that exits successfully after launching a longer-lived child. The child inherits
        // the redirected stderr pipe, so the parent's successful exit is not enough to finish ReadToEndAsync:
        //   sh -c "sleep 3 &"
        //   cmd /c start "" /b cmd /c "ping -n 4 127.0.0.1 > nul"
        return OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", "start \"\" /b cmd /c \"ping -n 4 127.0.0.1 > nul\""])
            : ("/bin/sh", ["-c", "sleep 3 &"]);
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

        public async Task<InteractionData> ReadNotificationAsync()
        {
            var interaction = await InteractionService.Interactions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(InteractionType.Notification, interaction.Type);
            interaction.CompletionTcs.TrySetResult(InteractionResult.Ok(false));
            return interaction;
        }

        public bool TryCompletePendingInteraction()
        {
            if (!InteractionService.Interactions.Reader.TryRead(out var interaction))
            {
                return false;
            }

            interaction.CompletionTcs.TrySetResult(InteractionResult.Ok(false));
            return true;
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

        public string? FileName { get; private set; }

        public IReadOnlyList<string> Arguments { get; private set; } = [];

        public IReadOnlyDictionary<string, string> EnvironmentVariables { get; private set; } = new Dictionary<string, string>();

        public string? WorkingDirectory { get; private set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            string? workingDirectory,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string>? environmentVariables,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            FileName = fileName;
            Arguments = arguments;
            EnvironmentVariables = environmentVariables ?? new Dictionary<string, string>();
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

}
