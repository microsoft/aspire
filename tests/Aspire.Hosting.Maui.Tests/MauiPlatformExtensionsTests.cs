// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Maui;
using Aspire.Hosting.Maui.Annotations;
using Aspire.Hosting.Maui.Utilities;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.Tests;

/// <summary>
/// Consolidated tests for all MAUI platform extensions (Windows, macOS Catalyst, Android Device, Android Emulator).
/// This reduces test duplication by using theory-based tests with platform-specific data.
/// </summary>
public class MauiPlatformExtensionsTests
{
    // Test data provider for platform configurations
    public static TheoryData<PlatformTestConfig> AllPlatforms => new()
    {
        new PlatformTestConfig("Windows", "Windows", "windows", "mauiapp-windows", "net10.0-windows10.0.19041.0",
            (maui) => maui.AddWindowsDevice(),
            (maui, name) => maui.AddWindowsDevice(name),
            typeof(MauiWindowsPlatformResource),
            "device",
            null,
            null,
            null),

        new PlatformTestConfig("MacCatalyst", "Mac Catalyst", "maccatalyst", "mauiapp-maccatalyst", "net10.0-maccatalyst",
            (maui) => maui.AddMacCatalystDevice(),
            (maui, name) => maui.AddMacCatalystDevice(name),
            typeof(MauiMacCatalystPlatformResource),
            "device",
            null,
            null,
            new Dictionary<string, string> { ["OpenArguments"] = "-W" }),

        new PlatformTestConfig("AndroidDevice", "Android", "android", "mauiapp-android-device", "net10.0-android",
            (maui) => maui.AddAndroidDevice(),
            (maui, name) => maui.AddAndroidDevice(name),
            typeof(MauiAndroidDeviceResource),
            "device",
            null,
            null,
            new Dictionary<string, string> { ["AdbTarget"] = "-d" }),

        new PlatformTestConfig("AndroidEmulator", "Android", "android", "mauiapp-android-emulator", "net10.0-android",
            (maui) => maui.AddAndroidEmulator(),
            (maui, name) => maui.AddAndroidEmulator(name),
            typeof(MauiAndroidEmulatorResource),
            "emulator",
            null,
            null,
            new Dictionary<string, string> { ["AdbTarget"] = "-e" }),

        new PlatformTestConfig("iOSDevice", "iOS", "ios", "mauiapp-ios-device", "net10.0-ios",
            (maui) => maui.AddiOSDevice(),
            (maui, name) => maui.AddiOSDevice(name),
            typeof(MauiiOSDeviceResource),
            "device",
            null,
            "ios-arm64",
            new Dictionary<string, string> { ["RuntimeIdentifier"] = "ios-arm64" }),

        new PlatformTestConfig("iOSSimulator", "iOS", "ios", "mauiapp-ios-simulator", "net10.0-ios",
            (maui) => maui.AddiOSSimulator(),
            (maui, name) => maui.AddiOSSimulator(name),
            typeof(MauiiOSSimulatorResource),
            "simulator",
            null,
            null,
            null)
    };

    public static TheoryData<PlatformTestConfig> MauiPlatformsWithIdeLaunchConfiguration => AllPlatforms;

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_CreatesResourceWithCorrectName(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent(config.RequiredTfm);
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act
            var platform = config.AddPlatformWithDefaultName(maui);

            // Assert
            Assert.NotNull(platform);
            Assert.Equal(config.ExpectedDefaultName, platform.Resource.Name);
            var resourceWithParent = Assert.IsAssignableFrom<IResourceWithParent<MauiProjectResource>>(platform.Resource);
            Assert.Same(maui.Resource, resourceWithParent.Parent);
            Assert.IsType(config.ExpectedResourceType, platform.Resource);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_WithCustomName_UsesProvidedName(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent(config.RequiredTfm);
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var customName = $"custom-{config.PlatformName}";

            // Act
            var platform = config.AddPlatformWithCustomName(maui, customName);

            // Assert
            Assert.Equal(customName, platform.Resource.Name);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_DuplicateName_ThrowsException(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent(config.RequiredTfm);
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var name = "duplicate-name";
            config.AddPlatformWithCustomName(maui, name);

            // Act & Assert
            var exception = Assert.Throws<DistributedApplicationException>(() =>
                config.AddPlatformWithCustomName(maui, name));
            Assert.Contains("already exists", exception.Message);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(MauiPlatformsWithIdeLaunchConfiguration))]
    public void AddMauiPlatform_EmitsMauiIdeLaunchConfiguration(PlatformTestConfig config)
    {
        var projectContent = MauiTestHelper.CreateProjectContent(config.RequiredTfm);
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            var platform = config.AddPlatformWithDefaultName(maui);

            var debugSupport = Assert.Single(platform.Resource.Annotations, annotation => annotation.GetType().FullName == "Aspire.Hosting.ApplicationModel.SupportsDebuggingAnnotation");
            Assert.Equal("maui", GetPropertyValue(debugSupport, "LaunchConfigurationType"));

            var exe = CreateExecutableForDebugTest();
            var annotator = Assert.IsAssignableFrom<Delegate>(GetPropertyValue(debugSupport, "LaunchConfigurationAnnotator"));
            annotator.DynamicInvoke(exe, "Debug");

            var launchConfigurations = GetLaunchConfigurations<SerializedMauiLaunchConfiguration>(exe);

            var launchConfiguration = Assert.Single(launchConfigurations);
            Assert.Equal("maui", launchConfiguration.Type);
            Assert.Equal("Debug", launchConfiguration.Mode);
            Assert.Equal(tempFile, launchConfiguration.ProjectPath);
            Assert.Equal(config.RequiredTfm, launchConfiguration.TargetFramework);
            Assert.Equal(config.PlatformIdentifier, launchConfiguration.Platform);
            Assert.Equal(config.ExpectedTargetKind, launchConfiguration.TargetKind);
            Assert.Equal(config.ExpectedDevice, launchConfiguration.Device);
            Assert.Equal(config.ExpectedRuntimeIdentifier, launchConfiguration.RuntimeIdentifier);
            Assert.Equal(config.ExpectedMsBuildProperties, launchConfiguration.MsBuildProperties);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_HasCorrectAnnotations(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent(config.RequiredTfm);
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act
            var platform = config.AddPlatformWithDefaultName(maui);

            // Assert
            var resource = platform.Resource;

            // Check ExecutableAnnotation
            var execAnnotation = resource.Annotations.OfType<ExecutableAnnotation>().FirstOrDefault();
            Assert.NotNull(execAnnotation);
            Assert.Equal("dotnet", execAnnotation.Command);
            Assert.NotNull(execAnnotation.WorkingDirectory);

            // Check MauiProjectMetadata
            var metadata = resource.Annotations.OfType<MauiProjectMetadata>().FirstOrDefault();
            Assert.NotNull(metadata);
            Assert.Equal(tempFile, metadata.ProjectPath);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_ImplementsIMauiPlatformResource(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent(config.RequiredTfm);
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act
            var platform = config.AddPlatformWithDefaultName(maui);

            // Assert
            Assert.IsAssignableFrom<IMauiPlatformResource>(platform.Resource);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_MultiplePlatforms_AllCreated(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent(config.RequiredTfm);
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act
            var platform1 = config.AddPlatformWithCustomName(maui, $"{config.PlatformName}-1");
            var platform2 = config.AddPlatformWithCustomName(maui, $"{config.PlatformName}-2");

            // Assert
            Assert.NotEqual(platform1.Resource.Name, platform2.Resource.Name);
            var parent1 = Assert.IsAssignableFrom<IResourceWithParent<MauiProjectResource>>(platform1.Resource);
            var parent2 = Assert.IsAssignableFrom<IResourceWithParent<MauiProjectResource>>(platform2.Resource);
            Assert.Same(parent1.Parent, parent2.Parent);
            Assert.Same(maui.Resource, parent1.Parent);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public async Task AddPlatform_WithoutRequiredTfm_ThrowsOnBeforeStartEvent(PlatformTestConfig config)
    {
        // Arrange - Create project without the required TFM
        var projectContent = CreateProjectContentWithout(config.PlatformIdentifier);
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act - Adding the platform should succeed (validation deferred to start)
            var platform = config.AddPlatformWithDefaultName(maui);
            Assert.NotNull(platform);

            // Build the app to get access to eventing
            await using var app = appBuilder.Build();

            // Trigger the BeforeResourceStartedEvent which should throw
            var exception = await Assert.ThrowsAsync<DistributedApplicationException>(async () =>
            {
                await app.Services.GetRequiredService<IDistributedApplicationEventing>()
                    .PublishAsync(new BeforeResourceStartedEvent(platform.Resource, app.Services), CancellationToken.None);
            });

            Assert.Contains($"Unable to detect {config.DisplayName}", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AddAndroidEmulator_WithEnvironment_EnvironmentVariablesAreSet()
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-android");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act
            var androidEmulator = maui.AddAndroidEmulator()
                .WithEnvironment("DEBUG_MODE", "true")
                .WithEnvironment("API_TIMEOUT", "30");

            // Assert
            var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                androidEmulator.Resource,
                DistributedApplicationOperation.Run,
                TestServiceProvider.Instance);

            Assert.Contains(envVars, kvp => kvp.Key == "DEBUG_MODE" && kvp.Value == "true");
            Assert.Contains(envVars, kvp => kvp.Key == "API_TIMEOUT" && kvp.Value == "30");
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void AddAndroidDeviceAndEmulator_CanCoexist()
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-android");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act
            var androidDevice = maui.AddAndroidDevice();
            var androidEmulator = maui.AddAndroidEmulator();

            // Assert
            Assert.NotNull(androidDevice);
            Assert.NotNull(androidEmulator);
            Assert.NotEqual(androidDevice.Resource.Name, androidEmulator.Resource.Name);
            Assert.IsType<MauiAndroidDeviceResource>(androidDevice.Resource);
            Assert.IsType<MauiAndroidEmulatorResource>(androidEmulator.Resource);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void AddAndroidDevice_WithDeviceId_CreatesResourceWithCorrectName()
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-android");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act
            var device = maui.AddAndroidDevice("my-device", "abc12345");

            // Assert
            Assert.NotNull(device);
            Assert.Equal("my-device", device.Resource.Name);
            Assert.IsType<MauiAndroidDeviceResource>(device.Resource);
            var launchConfiguration = GetSingleMauiLaunchConfiguration(device.Resource);
            Assert.Equal("abc12345", launchConfiguration.Device);
            Assert.Equal(new Dictionary<string, string> { ["AdbTarget"] = "-s abc12345" }, launchConfiguration.MsBuildProperties);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void AddAndroidEmulator_WithEmulatorId_CreatesResourceWithCorrectName()
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-android");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act
            var emulator = maui.AddAndroidEmulator("my-emulator", "Pixel_5_API_33");

            // Assert
            Assert.NotNull(emulator);
            Assert.Equal("my-emulator", emulator.Resource.Name);
            Assert.IsType<MauiAndroidEmulatorResource>(emulator.Resource);
            var launchConfiguration = GetSingleMauiLaunchConfiguration(emulator.Resource);
            Assert.Equal("Pixel_5_API_33", launchConfiguration.Device);
            Assert.Equal(new Dictionary<string, string> { ["AdbTarget"] = "-s Pixel_5_API_33" }, launchConfiguration.MsBuildProperties);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void AddiOSDevice_WithDeviceId_CreatesResourceWithCorrectName()
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act
            var device = maui.AddiOSDevice("my-device", "00008030-001234567890123A");

            // Assert
            Assert.NotNull(device);
            Assert.Equal("my-device", device.Resource.Name);
            Assert.IsType<MauiiOSDeviceResource>(device.Resource);
            var launchConfiguration = GetSingleMauiLaunchConfiguration(device.Resource);
            Assert.Equal("00008030-001234567890123A", launchConfiguration.Device);
            Assert.Equal("ios-arm64", launchConfiguration.RuntimeIdentifier);
            Assert.Equal(new Dictionary<string, string>
            {
                ["RuntimeIdentifier"] = "ios-arm64",
                ["_DeviceName"] = "00008030-001234567890123A"
            }, launchConfiguration.MsBuildProperties);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void AddiOSSimulator_WithSimulatorId_CreatesResourceWithCorrectName()
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act
            var simulator = maui.AddiOSSimulator("my-simulator", "E25BBE37-69BA-4720-B6FD-D54C97791E79");

            // Assert
            Assert.NotNull(simulator);
            Assert.Equal("my-simulator", simulator.Resource.Name);
            Assert.IsType<MauiiOSSimulatorResource>(simulator.Resource);
            var launchConfiguration = GetSingleMauiLaunchConfiguration(simulator.Resource);
            Assert.Equal("E25BBE37-69BA-4720-B6FD-D54C97791E79", launchConfiguration.Device);
            Assert.Equal(new Dictionary<string, string>
            {
                ["_DeviceName"] = ":v2:udid=E25BBE37-69BA-4720-B6FD-D54C97791E79"
            }, launchConfiguration.MsBuildProperties);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AddiOSSimulator_OptsOutOfProjectIdeLaunchAndKeepsSdkRunArgs()
    {
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var simulator = maui.AddiOSSimulator("my-simulator", "E25BBE37-69BA-4720-B6FD-D54C97791E79");

            var debugSupport = Assert.Single(simulator.Resource.Annotations, annotation => annotation.GetType().FullName == "Aspire.Hosting.ApplicationModel.SupportsDebuggingAnnotation");
            Assert.Equal("maui", GetPropertyValue(debugSupport, "LaunchConfigurationType"));

            var args = new List<object>();
            var argsContext = new CommandLineArgsCallbackContext(args, simulator.Resource);
            foreach (var argsAnnotation in simulator.Resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
            {
                await argsAnnotation.Callback(argsContext);
            }

            Assert.Collection(args,
                arg => Assert.Equal("run", Assert.IsType<string>(arg)),
                arg => Assert.Equal("-f", Assert.IsType<string>(arg)),
                arg => Assert.Equal("net10.0-ios", Assert.IsType<string>(arg)),
                arg => Assert.Equal("-p:_DeviceName=:v2:udid=E25BBE37-69BA-4720-B6FD-D54C97791E79", Assert.IsType<string>(arg)));
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void AddiOSDeviceAndSimulator_CanCoexist()
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act
            var device = maui.AddiOSDevice();
            var simulator = maui.AddiOSSimulator();

            // Assert
            Assert.NotNull(device);
            Assert.NotNull(simulator);
            Assert.NotEqual(device.Resource.Name, simulator.Resource.Name);
            Assert.IsType<MauiiOSDeviceResource>(device.Resource);
            Assert.IsType<MauiiOSSimulatorResource>(simulator.Resource);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [InlineData(true)]  // Device
    [InlineData(false)] // Simulator
    public void AddiOS_HasEnvironmentAnnotation(bool isDevice)
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act
            IResource resource;
            if (isDevice)
            {
                resource = maui.AddiOSDevice().Resource;
            }
            else
            {
                resource = maui.AddiOSSimulator().Resource;
            }

            // Assert
            var annotation = resource.Annotations.OfType<MauiiOSEnvironmentAnnotation>().FirstOrDefault();
            Assert.NotNull(annotation);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AddiOSSimulator_WithEnvironment_EnvironmentVariablesAreSet()
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-ios");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act
            var iosSimulator = maui.AddiOSSimulator()
                .WithEnvironment("DEBUG_MODE", "true")
                .WithEnvironment("API_TIMEOUT", "30");

            // Assert
            var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                iosSimulator.Resource,
                DistributedApplicationOperation.Run,
                TestServiceProvider.Instance);

            Assert.Contains(envVars, kvp => kvp.Key == "DEBUG_MODE" && kvp.Value == "true");
            Assert.Contains(envVars, kvp => kvp.Key == "API_TIMEOUT" && kvp.Value == "30");
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [InlineData(true)]  // Device
    [InlineData(false)] // Emulator
    public void AddAndroid_HasEnvironmentAnnotation(bool isDevice)
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-android");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

            // Act
            IResource resource;
            if (isDevice)
            {
                resource = maui.AddAndroidDevice().Resource;
            }
            else
            {
                resource = maui.AddAndroidEmulator().Resource;
            }

            // Assert
            var annotation = resource.Annotations.OfType<MauiAndroidEnvironmentAnnotation>().FirstOrDefault();
            Assert.NotNull(annotation);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    // OTLP Dev Tunnel Configuration Tests

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void WithOtlpDevTunnel_AddsOtlpDevTunnelAnnotation(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent(config.RequiredTfm);
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var platform = config.AddPlatformWithDefaultName(maui);

            // Act - WithOtlpDevTunnel works on the concrete platform resource builder
            config.ApplyWithOtlpDevTunnel(platform);

            // Assert
            // Verify that the tunnel infrastructure was created on the parent
            var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().FirstOrDefault();
            Assert.NotNull(tunnelConfig);
            Assert.NotNull(tunnelConfig.OtlpStub);
            Assert.NotNull(tunnelConfig.DevTunnel);
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void WithOtlpDevTunnel_MultiplePlatforms_SharesSameInfrastructure(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent(config.RequiredTfm);
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var platform1 = config.AddPlatformWithCustomName(maui, $"{config.PlatformName}-1");
            var platform2 = config.AddPlatformWithCustomName(maui, $"{config.PlatformName}-2");

            // Act - Apply dev tunnel to both platforms
            config.ApplyWithOtlpDevTunnel(platform1);
            config.ApplyWithOtlpDevTunnel(platform2);

            // Assert - Both platforms should share the same tunnel infrastructure
            var annotations = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().ToList();
            Assert.Single(annotations); // Only one tunnel infrastructure created
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public async Task WithOtlpDevTunnel_SetsEndpointWithoutIntermediateEnvironmentVariables(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = MauiTestHelper.CreateProjectContent(config.RequiredTfm);
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var platform = config.AddPlatformWithDefaultName(maui);

            // Act
            config.ApplyWithOtlpDevTunnel(platform);

            var endpointAnnotations = appBuilder.Resources.SelectMany(x => x.Annotations.OfType<EndpointAnnotation>());

            foreach (var endpointAnnotation in endpointAnnotations)
            {
                endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "localhost", 1234);
            }

            var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                platform.Resource,
                DistributedApplicationOperation.Run,
                TestServiceProvider.Instance);

            // Assert - OTEL_EXPORTER_OTLP_ENDPOINT should be set directly from the tunnel endpoint
            Assert.True(envVars.TryGetValue("OTEL_EXPORTER_OTLP_ENDPOINT", out var endpointValue));
            Assert.False(string.IsNullOrWhiteSpace(endpointValue));
            Assert.True(Uri.TryCreate(endpointValue, UriKind.Absolute, out _));

            // No intermediate service discovery or endpoint env vars should be present
            var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().Single();
            var stubName = tunnelConfig.OtlpStub.Name;
            Assert.DoesNotContain(envVars.Keys, k => k.StartsWith($"services__{stubName}__"));
            Assert.DoesNotContain(envVars.Keys, k => k.StartsWith($"{EnvironmentVariableNameEncoder.Encode(stubName).ToUpperInvariant()}_"));
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    // Helper methods

    private static string CreateProjectContentWithout(string excludePlatform)
    {
        // Create project with all TFMs except the one being tested
        var tfms = new List<string> { "net10.0-ios", "net10.0-windows10.0.19041.0", "net10.0-maccatalyst" };
        if (excludePlatform != "android")
        {
            tfms.Add("net10.0-android");
        }
        tfms.RemoveAll(tfm => tfm.Contains(excludePlatform, StringComparison.OrdinalIgnoreCase));

        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <TargetFrameworks>{string.Join(";", tfms)}</TargetFrameworks>
                </PropertyGroup>
            </Project>
            """;
    }

    private static object? GetPropertyValue(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName);
        Assert.NotNull(property);

        return property.GetValue(target);
    }

    private static object CreateExecutableForDebugTest()
    {
        var executableType = typeof(DistributedApplication).Assembly.GetType("Aspire.Hosting.Dcp.Model.Executable");
        Assert.NotNull(executableType);

        var createMethod = executableType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(createMethod);

        return createMethod.Invoke(null, ["test", "dotnet"])!;
    }

    private static List<T> GetLaunchConfigurations<T>(object executable)
    {
        var metadata = GetPropertyValue(executable, "Metadata");
        Assert.NotNull(metadata);

        var annotations = Assert.IsAssignableFrom<IDictionary<string, string>>(
            GetPropertyValue(metadata, "Annotations"));

        Assert.True(annotations.TryGetValue("executable.usvc-dev.developer.microsoft.com/launch-configurations", out var json));
        Assert.False(string.IsNullOrWhiteSpace(json));

        var launchConfigurations = JsonSerializer.Deserialize<List<T>>(json);
        Assert.NotNull(launchConfigurations);

        return launchConfigurations;
    }

    private static SerializedMauiLaunchConfiguration GetSingleMauiLaunchConfiguration(IResource resource)
    {
        var debugSupport = Assert.Single(resource.Annotations, annotation => annotation.GetType().FullName == "Aspire.Hosting.ApplicationModel.SupportsDebuggingAnnotation");
        var executable = CreateExecutableForDebugTest();
        var annotator = Assert.IsAssignableFrom<Delegate>(GetPropertyValue(debugSupport, "LaunchConfigurationAnnotator"));
        annotator.DynamicInvoke(executable, "Debug");

        return Assert.Single(GetLaunchConfigurations<SerializedMauiLaunchConfiguration>(executable));
    }

    // Configuration class for platform-specific test data
    public class PlatformTestConfig
    {
        public string PlatformName { get; }
        public string DisplayName { get; }
        public string PlatformIdentifier { get; }
        public string ExpectedDefaultName { get; }
        public string RequiredTfm { get; }
        public Func<IResourceBuilder<MauiProjectResource>, IResourceBuilder<IResource>> AddPlatformWithDefaultName { get; }
        public Func<IResourceBuilder<MauiProjectResource>, string, IResourceBuilder<IResource>> AddPlatformWithCustomName { get; }
        public Action<IResourceBuilder<IResource>> ApplyWithOtlpDevTunnel { get; }
        public Type ExpectedResourceType { get; }
        public string ExpectedTargetKind { get; }
        public string? ExpectedDevice { get; }
        public string? ExpectedRuntimeIdentifier { get; }
        public Dictionary<string, string>? ExpectedMsBuildProperties { get; }

        public PlatformTestConfig(
            string platformName,
            string displayName,
            string platformIdentifier,
            string expectedDefaultName,
            string requiredTfm,
            Func<IResourceBuilder<MauiProjectResource>, IResourceBuilder<IResource>> addDefault,
            Func<IResourceBuilder<MauiProjectResource>, string, IResourceBuilder<IResource>> addCustom,
            Type expectedResourceType,
            string expectedTargetKind,
            string? expectedDevice,
            string? expectedRuntimeIdentifier,
            Dictionary<string, string>? expectedMsBuildProperties)
        {
            PlatformName = platformName;
            DisplayName = displayName;
            PlatformIdentifier = platformIdentifier;
            ExpectedDefaultName = expectedDefaultName;
            RequiredTfm = requiredTfm;
            AddPlatformWithDefaultName = addDefault;
            AddPlatformWithCustomName = addCustom;
            ExpectedResourceType = expectedResourceType;
            ExpectedTargetKind = expectedTargetKind;
            ExpectedDevice = expectedDevice;
            ExpectedRuntimeIdentifier = expectedRuntimeIdentifier;
            ExpectedMsBuildProperties = expectedMsBuildProperties;

            // Set up WithOtlpDevTunnel based on the expected resource type
            ApplyWithOtlpDevTunnel = expectedResourceType.Name switch
            {
                nameof(MauiWindowsPlatformResource) => builder => ((IResourceBuilder<MauiWindowsPlatformResource>)builder).WithOtlpDevTunnel(),
                nameof(MauiMacCatalystPlatformResource) => builder => ((IResourceBuilder<MauiMacCatalystPlatformResource>)builder).WithOtlpDevTunnel(),
                nameof(MauiAndroidDeviceResource) => builder => ((IResourceBuilder<MauiAndroidDeviceResource>)builder).WithOtlpDevTunnel(),
                nameof(MauiAndroidEmulatorResource) => builder => ((IResourceBuilder<MauiAndroidEmulatorResource>)builder).WithOtlpDevTunnel(),
                nameof(MauiiOSDeviceResource) => builder => ((IResourceBuilder<MauiiOSDeviceResource>)builder).WithOtlpDevTunnel(),
                nameof(MauiiOSSimulatorResource) => builder => ((IResourceBuilder<MauiiOSSimulatorResource>)builder).WithOtlpDevTunnel(),
                _ => throw new NotSupportedException($"Unsupported resource type: {expectedResourceType.Name}")
            };
        }

        public override string ToString() => PlatformName;
    }

    private sealed class SerializedMauiLaunchConfiguration
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = string.Empty;

        [JsonPropertyName("project_path")]
        public string ProjectPath { get; set; } = string.Empty;

        [JsonPropertyName("target_framework")]
        public string TargetFramework { get; set; } = string.Empty;

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = string.Empty;

        [JsonPropertyName("target_kind")]
        public string TargetKind { get; set; } = string.Empty;

        [JsonPropertyName("device")]
        public string? Device { get; set; }

        [JsonPropertyName("runtime_identifier")]
        public string? RuntimeIdentifier { get; set; }

        [JsonPropertyName("msbuild_properties")]
        public Dictionary<string, string>? MsBuildProperties { get; set; }
    }
}
