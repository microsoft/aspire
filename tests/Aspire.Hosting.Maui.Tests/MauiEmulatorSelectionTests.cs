// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001 // IInteractionService is experimental.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Maui;
using Aspire.Hosting.Maui.Annotations;
using Aspire.Hosting.Maui.Lifecycle;
using Aspire.Hosting.Maui.Utilities;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

public class MauiEmulatorSelectionTests
{
    [Fact]
    public void ParseAvdList_WithMultipleAvds_ReturnsAll()
    {
        var result = AndroidEmulatorEnumerator.ParseAvdList("""
            Pixel_5_API_35
            Pixel_9_API_36
            """);

        Assert.Collection(
            result,
            option =>
            {
                Assert.Equal("Pixel_5_API_35", option.Id);
                Assert.Equal("Pixel 5 API 35", option.DisplayName);
            },
            option =>
            {
                Assert.Equal("Pixel_9_API_36", option.Id);
                Assert.Equal("Pixel 9 API 36", option.DisplayName);
            });
    }

    [Fact]
    public void ParseAvdList_FiltersNoisyOutput()
    {
        var result = AndroidEmulatorEnumerator.ParseAvdList("""
            INFO    | Storing crashdata in: /tmp/android-user/emu-crash-35.1.20.db
            WARNING | unexpected diagnostic
            Pixel_5_API_35
            Pixel.Tablet_API_36
            """);

        Assert.Collection(
            result,
            option => Assert.Equal("Pixel_5_API_35", option.Id),
            option => Assert.Equal("Pixel.Tablet_API_36", option.Id));
    }

    [Fact]
    public void ParseRunningEmulatorSerials_ReturnsOnlyOnlineEmulators()
    {
        var serials = AndroidEmulatorEnumerator.ParseRunningEmulatorSerials("""
            List of devices attached
            emulator-5554	device
            emulator-5556	offline
            R5CT	device
            emulator-5558	device product:sdk_gphone64_arm64
            """);

        Assert.Equal(["emulator-5554", "emulator-5558"], serials);
    }

    [Fact]
    public void ParseAvdNameForRunningEmulator_IgnoresOkLine()
    {
        var avdName = AndroidEmulatorEnumerator.ParseAvdNameForRunningEmulator("""
            Pixel_5_API_35
            OK
            """);

        Assert.Equal("Pixel_5_API_35", avdName);
    }

    [Fact]
    public void ParseSimctlOutput_WithDevices_ParsesIosSimulators()
    {
        var result = IOSSimulatorEnumerator.ParseSimctlOutput("""
            {
              "devices": {
                "com.apple.CoreSimulator.SimRuntime.iOS-18-2": [
                  { "udid": "AAAA-BBBB", "name": "iPhone 16 Pro", "state": "Shutdown", "isAvailable": true },
                  { "udid": "CCCC-DDDD", "name": "iPad Air", "state": "Shutdown", "isAvailable": false }
                ],
                "com.apple.CoreSimulator.SimRuntime.tvOS-18-2": [
                  { "udid": "EEEE-FFFF", "name": "Apple TV", "state": "Shutdown", "isAvailable": true }
                ],
                "com.apple.CoreSimulator.SimRuntime.iOS-17-0": [
                  { "udid": "GGGG-HHHH", "name": "iPhone 15", "state": "Shutdown", "isAvailable": true }
                ]
              }
            }
            """, NullLogger.Instance);

        Assert.Collection(
            result,
            option =>
            {
                Assert.Equal("AAAA-BBBB", option.Id);
                Assert.Equal("iPhone 16 Pro - iOS 18.2", option.DisplayName);
            },
            option =>
            {
                Assert.Equal("GGGG-HHHH", option.Id);
                Assert.Equal("iPhone 15 - iOS 17.0", option.DisplayName);
            });
    }

    [Fact]
    public void ParseSimctlOutput_WithNoisyOutput_ParsesJsonPayload()
    {
        var result = IOSSimulatorEnumerator.ParseSimctlOutput("""
            2026-06-29 10:00:00.000 simctl[1234:5678] diagnostic noise
            {
              "devices": {
                "com.apple.CoreSimulator.SimRuntime.iOS-18-2": [
                  { "udid": "AAAA-BBBB", "name": "iPhone 16 Pro", "state": "Shutdown", "isAvailable": true }
                ]
              }
            }
            trailing noise
            """, NullLogger.Instance);

        var option = Assert.Single(result);
        Assert.Equal("AAAA-BBBB", option.Id);
    }

    [Fact]
    public void FormatRuntimeName_FormatsAppleRuntimeIdentifier()
    {
        Assert.Equal("iOS 18.2", IOSSimulatorEnumerator.FormatRuntimeName("com.apple.CoreSimulator.SimRuntime.iOS-18-2"));
        Assert.Equal("tvOS 18.2", IOSSimulatorEnumerator.FormatRuntimeName("com.apple.CoreSimulator.SimRuntime.tvOS-18-2"));
        Assert.Equal("unknown", IOSSimulatorEnumerator.FormatRuntimeName("something.unknown"));
    }

    [Fact]
    public async Task AndroidEmulator_WithoutExplicitId_PromptsAndSetsAdbTarget()
    {
        await using var env = await EmulatorSelectionTestEnvironment.CreateAsync(
            androidEmulators:
            [
                new("Pixel_5_API_35", "Pixel 5 API 35"),
                new("Pixel_9_API_36", "Pixel 9 API 36")
            ]);

        var publishTask = env.PublishBeforeResourceStartedAsync(env.Android);
        var interaction = await env.InteractionService.Interactions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Select Android Emulator", interaction.Title);
        Assert.Collection(
            interaction.Inputs[0].Options!,
            option => Assert.Equal(KeyValuePair.Create("Pixel_5_API_35", "Pixel 5 API 35"), option),
            option => Assert.Equal(KeyValuePair.Create("Pixel_9_API_36", "Pixel 9 API 36"), option));

        interaction.CompletionTcs.SetResult(InteractionResult.Ok(new InteractionInput
        {
            Name = "target",
            InputType = InputType.Choice,
            Value = "Pixel_9_API_36"
        }));

        await publishTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Pixel_9_API_36", env.StartedAndroidAvdName);
        Assert.Equal("-p:AdbTarget=-s emulator-5556", MauiPlatformHelper.GetSelectedTargetMsBuildArgument(env.Android));

        var args = await ArgumentEvaluator.GetArgumentListAsync(env.Android, env.App.Services);
        Assert.Contains("-p:AdbTarget=-s emulator-5556", args);
        Assert.DoesNotContain("-p:AdbTarget=-e", args);
    }

    [Fact]
    public async Task AndroidEmulator_WithoutExplicitId_AutoSelectsSingleEmulator()
    {
        await using var env = await EmulatorSelectionTestEnvironment.CreateAsync(
            androidEmulators: [new("Pixel_5_API_35", "Pixel 5 API 35")]);

        await env.PublishBeforeResourceStartedAsync(env.Android);

        Assert.Equal("Pixel_5_API_35", env.StartedAndroidAvdName);
        Assert.Equal("-p:AdbTarget=-s emulator-5556", MauiPlatformHelper.GetSelectedTargetMsBuildArgument(env.Android));
        Assert.False(env.InteractionService.Interactions.Reader.TryRead(out _));
    }

    [Fact]
    public async Task IOSSimulator_WithoutExplicitId_PromptsAndSetsDeviceName()
    {
        await using var env = await EmulatorSelectionTestEnvironment.CreateAsync(
            iOSSimulators:
            [
                new("AAAA-BBBB", "iPhone 16 Pro - iOS 18.2"),
                new("CCCC-DDDD", "iPad Air - iOS 18.2")
            ]);

        var publishTask = env.PublishBeforeResourceStartedAsync(env.IOSSimulator);
        var interaction = await env.InteractionService.Interactions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Select iOS Simulator", interaction.Title);
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(new InteractionInput
        {
            Name = "target",
            InputType = InputType.Choice,
            Value = "CCCC-DDDD"
        }));

        await publishTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("-p:_DeviceName=:v2:udid=CCCC-DDDD", MauiPlatformHelper.GetSelectedTargetMsBuildArgument(env.IOSSimulator));

        var args = await ArgumentEvaluator.GetArgumentListAsync(env.IOSSimulator, env.App.Services);
        Assert.Contains("-p:_DeviceName=:v2:udid=CCCC-DDDD", args);
    }

    [Fact]
    public async Task ExplicitIds_BypassSelectionPrompt()
    {
        await using var env = await EmulatorSelectionTestEnvironment.CreateAsync(
            addExplicitTargets: true,
            androidEmulators: [new("Pixel_5_API_35", "Pixel 5 API 35")],
            iOSSimulators: [new("E25BBE37-69BA-4720-B6FD-D54C97791E79", "iPhone 16 Pro - iOS 18.2")]);

        await env.PublishBeforeResourceStartedAsync(env.Android);
        await env.PublishBeforeResourceStartedAsync(env.IOSSimulator);

        Assert.False(env.Android.TryGetLastAnnotation<SelectedEmulatorAnnotation>(out _));
        Assert.False(env.IOSSimulator.TryGetLastAnnotation<SelectedEmulatorAnnotation>(out _));
        Assert.False(env.InteractionService.Interactions.Reader.TryRead(out _));

        var androidArgs = await ArgumentEvaluator.GetArgumentListAsync(env.Android, env.App.Services);
        Assert.Contains("-p:AdbTarget=-s emulator-5554", androidArgs);

        var iosArgs = await ArgumentEvaluator.GetArgumentListAsync(env.IOSSimulator, env.App.Services);
        Assert.Contains("-p:_DeviceName=:v2:udid=E25BBE37-69BA-4720-B6FD-D54C97791E79", iosArgs);
    }

    [Fact]
    public async Task NonInteractiveMultipleTargets_ThrowsActionableError()
    {
        await using var env = await EmulatorSelectionTestEnvironment.CreateAsync(
            interactionAvailable: false,
            androidEmulators:
            [
                new("Pixel_5_API_35", "Pixel 5 API 35"),
                new("Pixel_9_API_36", "Pixel 9 API 36")
            ]);

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => env.PublishBeforeResourceStartedAsync(env.Android));

        Assert.Contains("interactive selection is not available", ex.Message);
        Assert.Contains("Pixel_5_API_35", ex.Message);
    }

    [Fact]
    public async Task NoTargetsFound_ThrowsActionableError()
    {
        await using var env = await EmulatorSelectionTestEnvironment.CreateAsync(androidEmulators: []);

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => env.PublishBeforeResourceStartedAsync(env.Android));

        Assert.Contains("No Android emulators found", ex.Message);
        Assert.Contains("Android Studio Device Manager", ex.Message);
    }

    [Fact]
    public async Task PromptCancellation_CancelsResourceStart()
    {
        await using var env = await EmulatorSelectionTestEnvironment.CreateAsync(
            androidEmulators:
            [
                new("Pixel_5_API_35", "Pixel 5 API 35"),
                new("Pixel_9_API_36", "Pixel 9 API 36")
            ]);

        var publishTask = env.PublishBeforeResourceStartedAsync(env.Android);
        var interaction = await env.InteractionService.Interactions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        interaction.CompletionTcs.SetResult(InteractionResult.Cancel<InteractionInput>());

        await Assert.ThrowsAsync<OperationCanceledException>(() => publishTask);
    }

    [Fact]
    public async Task PromptCancellation_OverridesFailedToStartWithExitedState()
    {
        await using var env = await EmulatorSelectionTestEnvironment.CreateAsync(
            androidEmulators:
            [
                new("Pixel_5_API_35", "Pixel 5 API 35"),
                new("Pixel_9_API_36", "Pixel 9 API 36")
            ]);

        var publishTask = env.PublishBeforeResourceStartedAsync(env.Android);
        var interaction = await env.InteractionService.Interactions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        interaction.CompletionTcs.SetResult(InteractionResult.Cancel<InteractionInput>());

        await Assert.ThrowsAsync<OperationCanceledException>(() => publishTask);
        await env.NotificationService.PublishUpdateAsync(env.Android, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error)
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await env.NotificationService.WaitForResourceAsync(
            env.Android.Name,
            e => string.Equals(e.Snapshot.State?.Text, KnownResourceStates.Exited, StringComparison.Ordinal),
            cts.Token);
    }

    [Fact]
    public async Task CancellationToken_CancelsBeforePromptCompletes()
    {
        await using var env = await EmulatorSelectionTestEnvironment.CreateAsync(
            androidEmulators:
            [
                new("Pixel_5_API_35", "Pixel 5 API 35"),
                new("Pixel_9_API_36", "Pixel 9 API 36")
            ]);

        using var cts = new CancellationTokenSource();
        var publishTask = env.PublishBeforeResourceStartedAsync(env.Android, cts.Token);
        var interaction = await env.InteractionService.Interactions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        cts.Cancel();
        interaction.CompletionTcs.SetResult(InteractionResult.Cancel<InteractionInput>());

        await Assert.ThrowsAsync<OperationCanceledException>(() => publishTask);
    }

    private sealed class EmulatorSelectionTestEnvironment : IAsyncDisposable
    {
        private TestTempDirectory TempDirectory { get; } = new();

        public DistributedApplication App { get; private set; } = null!;
        public TestInteractionService InteractionService { get; private set; } = null!;
        public MauiAndroidEmulatorResource Android { get; private set; } = null!;
        public MauiiOSSimulatorResource IOSSimulator { get; private set; } = null!;
        public string? StartedAndroidAvdName { get; private set; }
        public ResourceNotificationService NotificationService => App.Services.GetRequiredService<ResourceNotificationService>();

        public async Task PublishBeforeResourceStartedAsync(IResource resource, CancellationToken cancellationToken = default)
        {
            var eventing = App.Services.GetRequiredService<IDistributedApplicationEventing>();
            await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, App.Services), cancellationToken);
        }

        public static async Task<EmulatorSelectionTestEnvironment> CreateAsync(
            bool addExplicitTargets = false,
            bool interactionAvailable = true,
            IReadOnlyList<EmulatorOption>? androidEmulators = null,
            IReadOnlyList<EmulatorOption>? iOSSimulators = null)
        {
            var env = new EmulatorSelectionTestEnvironment();
            var projectPath = Path.Combine(env.TempDirectory.Path, "TempMauiProject.csproj");
            File.WriteAllText(projectPath, MauiTestHelper.CreateProjectContent("net10.0-android;net10.0-ios"));

            var appBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                DisableDashboard = true
            });

            var maui = appBuilder.AddMauiProject("mauiapp", projectPath);
            var android = addExplicitTargets
                ? maui.AddAndroidEmulator("android", "emulator-5554").Resource
                : maui.AddAndroidEmulator("android").Resource;
            var iosSimulator = addExplicitTargets
                ? maui.AddiOSSimulator("ios-simulator", "E25BBE37-69BA-4720-B6FD-D54C97791E79").Resource
                : maui.AddiOSSimulator("ios-simulator").Resource;

            var app = appBuilder.Build();
            var interactionService = new TestInteractionService
            {
                IsAvailable = interactionAvailable
            };

            var subscriber = new MauiEmulatorSelectionEventSubscriber(
                interactionService,
                app.Services.GetRequiredService<ResourceNotificationService>(),
                app.Services.GetRequiredService<ResourceLoggerService>(),
                app.Services.GetRequiredService<ILogger<MauiEmulatorSelectionEventSubscriber>>())
            {
                AndroidEnumeratorOverride = (_, _) => Task.FromResult(androidEmulators ?? []),
                IOSSimulatorEnumeratorOverride = (_, _) => Task.FromResult(iOSSimulators ?? []),
                EnsureAndroidEmulatorRunningOverride = (avdName, _, _) =>
                {
                    env.StartedAndroidAvdName = avdName;
                    return Task.FromResult("emulator-5556");
                }
            };

            await subscriber.SubscribeAsync(
                app.Services.GetRequiredService<IDistributedApplicationEventing>(),
                app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
                CancellationToken.None);

            env.App = app;
            env.InteractionService = interactionService;
            env.Android = android;
            env.IOSSimulator = iosSimulator;

            return env;
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            TempDirectory.Dispose();
        }
    }
}
