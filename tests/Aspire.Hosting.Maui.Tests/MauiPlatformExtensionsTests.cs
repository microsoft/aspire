// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001 // Debug support APIs are experimental.
#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.DevTunnels;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Maui;
using Aspire.Hosting.Maui.Annotations;
using Aspire.Hosting.Maui.Utilities;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.Tests;

/// <summary>
/// Consolidated tests for all MAUI platform extensions (Windows, macOS Catalyst, Android Device, Android Emulator).
/// This reduces test duplication by using theory-based tests with platform-specific data.
/// </summary>
public class MauiPlatformExtensionsTests(ITestOutputHelper outputHelper)
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
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(config.RequiredTfm));

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

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_WithCustomName_UsesProvidedName(PlatformTestConfig config)
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(config.RequiredTfm));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var customName = $"custom-{config.PlatformName}";

        // Act
        var platform = config.AddPlatformWithCustomName(maui, customName);

        // Assert
        Assert.Equal(customName, platform.Resource.Name);
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_DuplicateName_ThrowsException(PlatformTestConfig config)
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(config.RequiredTfm));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var name = "duplicate-name";
        config.AddPlatformWithCustomName(maui, name);

        // Act & Assert
        var exception = Assert.Throws<DistributedApplicationException>(() =>
            config.AddPlatformWithCustomName(maui, name));
        Assert.Contains("already exists", exception.Message);
    }

    [Theory]
    [MemberData(nameof(MauiPlatformsWithIdeLaunchConfiguration))]
    public async Task AddMauiPlatform_EmitsMauiIdeLaunchConfiguration(PlatformTestConfig config)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(config.RequiredTfm));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

        var platform = config.AddPlatformWithDefaultName(maui);

        var debugSupport = Assert.Single(platform.Resource.Annotations.OfType<SupportsDebuggingAnnotation>());
        Assert.Equal("maui", debugSupport.LaunchConfigurationType);

        var launchConfiguration = await DeserializeLaunchConfigurationAsync(platform.Resource);
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

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_HasCorrectAnnotations(PlatformTestConfig config)
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(config.RequiredTfm));

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

        var buildInfo = resource.Annotations.OfType<MauiBuildInfoAnnotation>().FirstOrDefault();
        Assert.NotNull(buildInfo);
        Assert.Equal(config.RequiredTfm, buildInfo.TargetFramework);
        Assert.Equal(GetTestAssemblyConfiguration(), buildInfo.Configuration);
        Assert.Equal(
            config.ExpectedMsBuildProperties?.Select(property => $"-p:{property.Key}={property.Value}").ToArray() ?? [],
            buildInfo.AdditionalBuildArguments);

        var launchOverride = resource.Annotations.OfType<ProjectLaunchArgsOverrideAnnotation>().FirstOrDefault();
        Assert.NotNull(launchOverride);
        Assert.Collection(
            launchOverride.Arguments,
            arg => Assert.Equal("build", arg),
            arg => Assert.Equal("--no-restore", arg),
            arg => Assert.Equal("/t:Run", arg),
            arg => Assert.Equal("-p:NoBuild=true", arg));
        Assert.Equal("run", launchOverride.LeadingResourceArgumentToRemove);
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_ImplementsIMauiPlatformResource(PlatformTestConfig config)
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(config.RequiredTfm));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

        // Act
        var platform = config.AddPlatformWithDefaultName(maui);

        // Assert
        Assert.IsAssignableFrom<IMauiPlatformResource>(platform.Resource);
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_MultiplePlatforms_AllCreated(PlatformTestConfig config)
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(config.RequiredTfm));

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

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public async Task AddPlatform_WithoutRequiredTfm_ThrowsOnBeforeStartEvent(PlatformTestConfig config)
    {
        // Arrange - Create project without the required TFM
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, CreateProjectContentWithout(config.PlatformIdentifier));

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

    [Fact]
    public async Task AddAndroidEmulator_WithEnvironment_EnvironmentVariablesAreSet()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

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

    [Fact]
    public void AddAndroidDeviceAndEmulator_CanCoexist()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

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

    [Fact]
    public async Task AddAndroidDevice_WithDeviceId_CreatesResourceWithCorrectName()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

        // Act
        var device = maui.AddAndroidDevice("my-device", "abc12345");

        // Assert
        Assert.NotNull(device);
        Assert.Equal("my-device", device.Resource.Name);
        Assert.IsType<MauiAndroidDeviceResource>(device.Resource);
        var launchConfiguration = await GetSingleMauiLaunchConfigurationAsync(device.Resource);
        Assert.Equal("abc12345", launchConfiguration.Device);
        Assert.Equal(new Dictionary<string, string> { ["AdbTarget"] = "-s abc12345" }, launchConfiguration.MsBuildProperties);
    }

    [Fact]
    public async Task AddAndroidEmulator_WithEmulatorId_CreatesResourceWithCorrectName()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

        // Act
        var emulator = maui.AddAndroidEmulator("my-emulator", "Pixel_5_API_33");

        // Assert
        Assert.NotNull(emulator);
        Assert.Equal("my-emulator", emulator.Resource.Name);
        Assert.IsType<MauiAndroidEmulatorResource>(emulator.Resource);
        var launchConfiguration = await GetSingleMauiLaunchConfigurationAsync(emulator.Resource);
        Assert.Equal("Pixel_5_API_33", launchConfiguration.Device);
        Assert.Equal(new Dictionary<string, string> { ["AdbTarget"] = "-s Pixel_5_API_33" }, launchConfiguration.MsBuildProperties);
    }

    [Fact]
    public async Task AddiOSDevice_WithDeviceId_CreatesResourceWithCorrectName()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

        // Act
        var device = maui.AddiOSDevice("my-device", "00008030-001234567890123A");

        // Assert
        Assert.NotNull(device);
        Assert.Equal("my-device", device.Resource.Name);
        Assert.IsType<MauiiOSDeviceResource>(device.Resource);
        var launchConfiguration = await GetSingleMauiLaunchConfigurationAsync(device.Resource);
        Assert.Equal("00008030-001234567890123A", launchConfiguration.Device);
        Assert.Equal("ios-arm64", launchConfiguration.RuntimeIdentifier);
        Assert.Equal(new Dictionary<string, string>
        {
            ["RuntimeIdentifier"] = "ios-arm64",
            ["_DeviceName"] = "00008030-001234567890123A"
        }, launchConfiguration.MsBuildProperties);
    }

    [Fact]
    public async Task AddiOSSimulator_WithSimulatorId_CreatesResourceWithCorrectName()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

        // Act
        var simulator = maui.AddiOSSimulator("my-simulator", "E25BBE37-69BA-4720-B6FD-D54C97791E79");

        // Assert
        Assert.NotNull(simulator);
        Assert.Equal("my-simulator", simulator.Resource.Name);
        Assert.IsType<MauiiOSSimulatorResource>(simulator.Resource);
        var launchConfiguration = await GetSingleMauiLaunchConfigurationAsync(simulator.Resource);
        Assert.Equal("E25BBE37-69BA-4720-B6FD-D54C97791E79", launchConfiguration.Device);
        Assert.Equal(new Dictionary<string, string>
        {
            ["_DeviceName"] = ":v2:udid=E25BBE37-69BA-4720-B6FD-D54C97791E79"
        }, launchConfiguration.MsBuildProperties);
    }

    [Fact]
    public async Task AddiOSSimulator_OptsOutOfProjectIdeLaunchAndKeepsSdkRunArgs()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var simulator = maui.AddiOSSimulator("my-simulator", "E25BBE37-69BA-4720-B6FD-D54C97791E79");

        var debugSupport = Assert.Single(simulator.Resource.Annotations.OfType<SupportsDebuggingAnnotation>());
        Assert.Equal("maui", debugSupport.LaunchConfigurationType);

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

    [Fact]
    public void AddiOSDeviceAndSimulator_CanCoexist()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

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

    [Theory]
    [InlineData(true)]  // Device
    [InlineData(false)] // Simulator
    public void AddiOS_HasEnvironmentAnnotation(bool isDevice)
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

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

    [Fact]
    public async Task AddiOSSimulator_WithEnvironment_EnvironmentVariablesAreSet()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

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

    [Theory]
    [InlineData("android", "net10.0-android")]
    [InlineData("ios", "net10.0-ios")]
    public async Task MobileEnvironmentTargetsFileIsRegeneratedWhenResourceRestarts(string platform, string targetFramework)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(targetFramework));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var resource = platform == "android"
            ? (IResource)maui.AddAndroidEmulator().Resource
            : maui.AddiOSSimulator().Resource;
        resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
        {
            context.EnvironmentVariables["RESTART_VALUE"] = "first";
        }));

        var existingArgumentCallbacks = resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>().ToHashSet();

        await using var app = appBuilder.Build();
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        IDistributedApplicationEventingSubscriber environmentSubscriber = platform == "android"
            ? new MauiAndroidEnvironmentSubscriber(
                executionContext,
                app.Services.GetRequiredService<ResourceLoggerService>(),
                app.Services.GetRequiredService<ResourceNotificationService>(),
                app.Services.GetRequiredService<IFileSystemService>())
            : new MauiiOSEnvironmentSubscriber(
                executionContext,
                app.Services.GetRequiredService<ResourceLoggerService>(),
                app.Services.GetRequiredService<ResourceNotificationService>(),
                app.Services.GetRequiredService<IFileSystemService>());
        await environmentSubscriber.SubscribeAsync(eventing, executionContext, CancellationToken.None);
        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, app.Services), CancellationToken.None);

        var targetsFileCallback = Assert.Single(
            resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>(),
            callback => !existingArgumentCallbacks.Contains(callback));

        var firstPath = await EvaluateTargetsFileAsync(targetsFileCallback, resource);
        Assert.Contains("RESTART_VALUE=first", await File.ReadAllTextAsync(firstPath));

        resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
        {
            context.EnvironmentVariables["RESTART_VALUE"] = "second";
        }));

        var secondPath = await EvaluateTargetsFileAsync(targetsFileCallback, resource);
        Assert.Equal(firstPath, secondPath);
        Assert.Contains("RESTART_VALUE=second", await File.ReadAllTextAsync(secondPath));

        static async Task<string> EvaluateTargetsFileAsync(CommandLineArgsCallbackAnnotation callback, IResource resource)
        {
            var args = new List<object>();
            await callback.Callback(new CommandLineArgsCallbackContext(args, resource, CancellationToken.None));
            var property = Assert.Single(
                args.OfType<string>(),
                argument => argument.StartsWith("-p:CustomAfterMicrosoftCommonTargets=", StringComparison.Ordinal));

            return property["-p:CustomAfterMicrosoftCommonTargets=".Length..];
        }
    }

    [Theory]
    [InlineData(true)]  // Device
    [InlineData(false)] // Emulator
    public void AddAndroid_HasEnvironmentAnnotation(bool isDevice)
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

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

    // OTLP Dev Tunnel Configuration Tests

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void WithOtlpDevTunnel_AddsOtlpDevTunnelAnnotation(PlatformTestConfig config)
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(config.RequiredTfm));

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

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void WithOtlpDevTunnel_MultiplePlatforms_SharesSameInfrastructure(PlatformTestConfig config)
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(config.RequiredTfm));

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

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public async Task WithOtlpDevTunnel_SetsEndpointWithoutIntermediateEnvironmentVariables(PlatformTestConfig config)
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(config.RequiredTfm));

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

    [Fact]
    public async Task WithOtlpDevTunnel_UsesGrpcWhenDashboardOnlyHasGrpcEndpoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        ClearDashboardOtlpEndpointConfiguration(appBuilder.Configuration);

        var dashboard = appBuilder.AddResource(new ContainerResource(KnownResourceNames.AspireDashboard));
        dashboard.Resource.Annotations.Add(new EndpointAnnotation(
            ProtocolType.Tcp,
            name: KnownEndpointNames.OtlpGrpcEndpointName,
            uriScheme: "http",
            isProxied: true,
            transport: "http2"));

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var iosSimulator = maui.AddiOSSimulator()
            .WithOtlpDevTunnel();

        var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().Single();
        var stubEndpoint = tunnelConfig.OtlpStub.OtlpEndpoint;

        Assert.Null(stubEndpoint.Port);
        Assert.Null(stubEndpoint.TargetPort);
        Assert.Null(stubEndpoint.AllocatedEndpoint);

        await using var app = appBuilder.Build();

        var dashboardEndpoint = dashboard.Resource.Annotations.OfType<EndpointAnnotation>().Single(e => e.Name == KnownEndpointNames.OtlpGrpcEndpointName);
        dashboardEndpoint.AllocatedEndpoint = new AllocatedEndpoint(dashboardEndpoint, "localhost", 55075);
        await appBuilder.Eventing.PublishAsync(new ResourceEndpointsAllocatedEvent(dashboard.Resource, app.Services), CancellationToken.None);

        Assert.Equal("http", stubEndpoint.UriScheme);
        Assert.Equal(55075, stubEndpoint.Port);
        Assert.Equal(55075, stubEndpoint.TargetPort);
        Assert.Equal("http://localhost:55075", stubEndpoint.AllocatedEndpoint?.UriString);
        Assert.Equal("http2", stubEndpoint.Transport);

        var tunnelEndpoint = tunnelConfig.DevTunnel.GetEndpoint(tunnelConfig.OtlpStub, "otlp");
        tunnelEndpoint.EndpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(tunnelEndpoint.EndpointAnnotation, "mobile-otlp.devtunnels.ms", 443);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            iosSimulator.Resource,
            DistributedApplicationOperation.Run,
            app.Services);

        Assert.Equal("https://mobile-otlp.devtunnels.ms:443", envVars[KnownOtelConfigNames.ExporterOtlpEndpoint]);
        Assert.Equal("grpc", envVars[KnownOtelConfigNames.ExporterOtlpProtocol]);
    }

    [Fact]
    public async Task WithOtlpDevTunnel_PrefersHttpAndUsesConcreteTargetPort()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        ClearDashboardOtlpEndpointConfiguration(appBuilder.Configuration);

        var dashboard = appBuilder.AddResource(new ExecutableResource(KnownResourceNames.AspireDashboard, "dashboard", ""));
        dashboard.Resource.Annotations.Add(new EndpointAnnotation(
            ProtocolType.Tcp,
            name: KnownEndpointNames.OtlpGrpcEndpointName,
            uriScheme: "http",
            isProxied: true,
            transport: "http2"));
        dashboard.Resource.Annotations.Add(new EndpointAnnotation(
            ProtocolType.Tcp,
            name: KnownEndpointNames.OtlpHttpEndpointName,
            uriScheme: "http",
            isProxied: true));

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var iosSimulator = maui.AddiOSSimulator().WithOtlpDevTunnel();
        var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().Single();

        await using var app = appBuilder.Build();

        var httpEndpoint = dashboard.Resource.Annotations.OfType<EndpointAnnotation>()
            .Single(endpoint => endpoint.Name == KnownEndpointNames.OtlpHttpEndpointName);
        httpEndpoint.AllocatedEndpoint = new AllocatedEndpoint(httpEndpoint, "localhost", 55076, targetPortExpression: "55077");
        var grpcEndpoint = dashboard.Resource.Annotations.OfType<EndpointAnnotation>()
            .Single(endpoint => endpoint.Name == KnownEndpointNames.OtlpGrpcEndpointName);
        grpcEndpoint.AllocatedEndpoint = new AllocatedEndpoint(grpcEndpoint, "localhost", 55078, targetPortExpression: "55079");

        await appBuilder.Eventing.PublishAsync(
            new ResourceEndpointsAllocatedEvent(dashboard.Resource, app.Services),
            CancellationToken.None);

        var stubEndpoint = tunnelConfig.OtlpStub.OtlpEndpoint;
        Assert.Equal(55077, stubEndpoint.Port);
        Assert.Equal(55077, stubEndpoint.TargetPort);
        Assert.Equal("http://localhost:55077", stubEndpoint.AllocatedEndpoint?.UriString);
        Assert.Equal("http", stubEndpoint.Transport);
        Assert.Equal("http", Assert.Single(appBuilder.Resources.OfType<DevTunnelPortResource>()).Options.Protocol);

        httpEndpoint.AllocatedEndpoint = new AllocatedEndpoint(httpEndpoint, "localhost", 55088, targetPortExpression: "55089");
        await appBuilder.Eventing.PublishAsync(
            new ResourceEndpointsAllocatedEvent(dashboard.Resource, app.Services),
            CancellationToken.None);
        Assert.Equal(55077, stubEndpoint.Port);

        var tunnelEndpoint = tunnelConfig.DevTunnel.GetEndpoint(tunnelConfig.OtlpStub, "otlp");
        tunnelEndpoint.EndpointAnnotation.AllocatedEndpoint =
            new AllocatedEndpoint(tunnelEndpoint.EndpointAnnotation, "mobile-otlp.devtunnels.ms", 443);

        var environmentVariables = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            iosSimulator.Resource,
            DistributedApplicationOperation.Run,
            app.Services);

        Assert.Equal("http/protobuf", environmentVariables[KnownOtelConfigNames.ExporterOtlpProtocol]);
    }

    [Fact]
    public async Task WithOtlpDevTunnel_UsesAllocatedProxyForNonlocalTargetHost()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        ClearDashboardOtlpEndpointConfiguration(appBuilder.Configuration);
        var dashboard = appBuilder.AddResource(new ExecutableResource(KnownResourceNames.AspireDashboard, "dashboard", ""));
        var dashboardEndpoint = new EndpointAnnotation(
            ProtocolType.Tcp,
            name: KnownEndpointNames.OtlpHttpEndpointName,
            uriScheme: "http",
            isProxied: true)
        {
            TargetHost = "192.0.2.1"
        };
        dashboard.Resource.Annotations.Add(dashboardEndpoint);

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        maui.AddiOSSimulator().WithOtlpDevTunnel();
        var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().Single();

        await using var app = appBuilder.Build();

        dashboardEndpoint.AllocatedEndpoint = new AllocatedEndpoint(
            dashboardEndpoint,
            "localhost",
            55076,
            targetPortExpression: "55077");
        await appBuilder.Eventing.PublishAsync(
            new ResourceEndpointsAllocatedEvent(dashboard.Resource, app.Services),
            CancellationToken.None);

        Assert.Equal(55076, tunnelConfig.OtlpStub.OtlpEndpoint.Port);
        Assert.Equal("http://localhost:55076", tunnelConfig.OtlpStub.OtlpEndpoint.AllocatedEndpoint?.UriString);
    }

    [Fact]
    public async Task WithOtlpDevTunnel_ResolvesTargetPortExpressionFromDashboardSnapshot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        ClearDashboardOtlpEndpointConfiguration(appBuilder.Configuration);
        var dashboard = appBuilder.AddResource(new ExecutableResource(KnownResourceNames.AspireDashboard, "dashboard", ""));
        dashboard.Resource.Annotations.Add(new EndpointAnnotation(
            ProtocolType.Tcp,
            name: KnownEndpointNames.OtlpHttpEndpointName,
            uriScheme: "http",
            isProxied: true));

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var iosSimulator = maui.AddiOSSimulator().WithOtlpDevTunnel();
        var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().Single();

        await using var app = appBuilder.Build();

        await app.Services.GetRequiredService<ResourceNotificationService>()
            .PublishUpdateAsync(dashboard.Resource, snapshot => snapshot with
            {
                State = KnownResourceStates.Running,
                EnvironmentVariables =
                [
                    new(
                        KnownConfigNames.DashboardOtlpHttpEndpointUrl,
                        "http://localhost:55077",
                        IsFromSpec: false)
                ]
            });

        var dashboardEndpoint = dashboard.Resource.Annotations.OfType<EndpointAnnotation>().Single();
        dashboardEndpoint.AllocatedEndpoint = new AllocatedEndpoint(
            dashboardEndpoint,
            "localhost",
            55076,
            targetPortExpression: """{{- portForServing "dashboard-otlp-http" -}}""");

        await appBuilder.Eventing.PublishAsync(
            new ResourceEndpointsAllocatedEvent(dashboard.Resource, app.Services),
            CancellationToken.None);

        var stubEndpoint = tunnelConfig.OtlpStub.OtlpEndpoint;
        Assert.Equal(55077, stubEndpoint.Port);
        Assert.Equal(55077, stubEndpoint.TargetPort);
        Assert.Equal("http://localhost:55077", stubEndpoint.AllocatedEndpoint?.UriString);
        Assert.Equal("http", stubEndpoint.Transport);
    }

    [Fact]
    public async Task WithOtlpDevTunnel_AllocatesStubFromConfiguredOtlpEndpoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        using var appBuilder = TestDistributedApplicationBuilder.Create();
        appBuilder.Configuration[KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "http://localhost:18889";

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var androidEmulator = maui.AddAndroidEmulator()
            .WithOtlpDevTunnel();
        var dashboard = appBuilder.AddResource(new ContainerResource(KnownResourceNames.AspireDashboard));
        var dashboardEndpoint = new EndpointAnnotation(
            ProtocolType.Tcp,
            name: KnownEndpointNames.OtlpGrpcEndpointName,
            uriScheme: "https",
            isProxied: true,
            transport: "http2");
        dashboard.Resource.Annotations.Add(dashboardEndpoint);

        var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().Single();
        var stubEndpoint = tunnelConfig.OtlpStub.OtlpEndpoint;
        var stubEndpointEventPublished = false;
        appBuilder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>((evt, _) =>
        {
            if (ReferenceEquals(evt.Resource, tunnelConfig.OtlpStub))
            {
                stubEndpointEventPublished = true;
            }

            return Task.CompletedTask;
        });

        Assert.Equal("http", stubEndpoint.UriScheme);
        Assert.Equal(18889, stubEndpoint.Port);
        Assert.Equal(18889, stubEndpoint.TargetPort);
        Assert.Equal("http://localhost:18889", stubEndpoint.AllocatedEndpoint?.UriString);
        Assert.Equal("http2", stubEndpoint.Transport);

        await using var app = appBuilder.Build();

        await appBuilder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, app.Services.GetRequiredService<DistributedApplicationModel>()), CancellationToken.None);
        Assert.True(stubEndpointEventPublished);

        dashboardEndpoint.AllocatedEndpoint = new AllocatedEndpoint(dashboardEndpoint, "localhost", 55075);
        await appBuilder.Eventing.PublishAsync(new ResourceEndpointsAllocatedEvent(dashboard.Resource, app.Services), CancellationToken.None);

        Assert.Equal("http", stubEndpoint.UriScheme);
        Assert.Equal(18889, stubEndpoint.Port);
        Assert.Equal(18889, stubEndpoint.TargetPort);
        Assert.Equal("http://localhost:18889", stubEndpoint.AllocatedEndpoint?.UriString);

        var tunnelEndpoint = tunnelConfig.DevTunnel.GetEndpoint(tunnelConfig.OtlpStub, "otlp");
        tunnelEndpoint.EndpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(tunnelEndpoint.EndpointAnnotation, "mobile-otlp.devtunnels.ms", 443);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            androidEmulator.Resource,
            DistributedApplicationOperation.Run,
            app.Services);

        Assert.Equal("https://mobile-otlp.devtunnels.ms:443", envVars[KnownOtelConfigNames.ExporterOtlpEndpoint]);
        Assert.Equal("grpc", envVars[KnownOtelConfigNames.ExporterOtlpProtocol]);
    }

    [Fact]
    public async Task WithOtlpDevTunnel_PrefersConfiguredHttpEndpoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        using var appBuilder = TestDistributedApplicationBuilder.Create();
        appBuilder.Configuration[KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "http://localhost:18889";
        appBuilder.Configuration[KnownConfigNames.DashboardOtlpHttpEndpointUrl] = "http://dashboard.localhost:18890";

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var iosSimulator = maui.AddiOSSimulator().WithOtlpDevTunnel();
        var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().Single();
        var stubEndpoint = tunnelConfig.OtlpStub.OtlpEndpoint;

        Assert.Equal(18890, stubEndpoint.Port);
        Assert.Equal(18890, stubEndpoint.TargetPort);
        Assert.Equal("http://localhost:18890", stubEndpoint.AllocatedEndpoint?.UriString);
        Assert.Equal("http", stubEndpoint.Transport);
        Assert.Equal("http", Assert.Single(appBuilder.Resources.OfType<DevTunnelPortResource>()).Options.Protocol);

        await using var app = appBuilder.Build();
        var tunnelEndpoint = tunnelConfig.DevTunnel.GetEndpoint(tunnelConfig.OtlpStub, "otlp");
        tunnelEndpoint.EndpointAnnotation.AllocatedEndpoint =
            new AllocatedEndpoint(tunnelEndpoint.EndpointAnnotation, "mobile-otlp.devtunnels.ms", 443);

        var environmentVariables = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            iosSimulator.Resource,
            DistributedApplicationOperation.Run,
            app.Services);

        Assert.Equal("http/protobuf", environmentVariables[KnownOtelConfigNames.ExporterOtlpProtocol]);
    }

    [Theory]
    [InlineData("http://0.0.0.0:18890")]
    [InlineData("http://[::]:18890")]
    public void WithOtlpDevTunnel_AcceptsConfiguredWildcardBinding(string endpointUrl)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        using var appBuilder = TestDistributedApplicationBuilder.Create();
        appBuilder.Configuration[KnownConfigNames.DashboardOtlpHttpEndpointUrl] = endpointUrl;

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        maui.AddiOSSimulator().WithOtlpDevTunnel();
        var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().Single();

        Assert.Equal("http://localhost:18890", tunnelConfig.OtlpStub.OtlpEndpoint.AllocatedEndpoint?.UriString);
    }

    [Fact]
    public async Task WithOtlpDevTunnel_DoesNotAddRunOnlyEnvironmentDuringPublish()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        using var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        appBuilder.Configuration[KnownConfigNames.DashboardOtlpHttpEndpointUrl] = "http://localhost:18890";

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var iosSimulator = maui.AddiOSSimulator().WithOtlpDevTunnel();

        var environmentVariables = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            iosSimulator.Resource,
            DistributedApplicationOperation.Publish,
            TestServiceProvider.Instance);

        Assert.DoesNotContain(KnownOtelConfigNames.ExporterOtlpEndpoint, environmentVariables.Keys);
        Assert.DoesNotContain(KnownOtelConfigNames.ExporterOtlpProtocol, environmentVariables.Keys);
    }

    [Fact]
    public async Task WithOtlpDevTunnel_ThrowsWhenDashboardDisabledAndNoConfiguredOtlpEndpoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { DisableDashboard = true });
        ClearDashboardOtlpEndpointConfiguration(appBuilder.Configuration);

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var androidEmulator = maui.AddAndroidEmulator()
            .WithOtlpDevTunnel();

        var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().Single();

        await using var app = appBuilder.Build();

        var environmentTask = EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            androidEmulator.Resource,
            DistributedApplicationOperation.Run,
            app.Services).AsTask();
        Assert.False(environmentTask.IsCompleted);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            appBuilder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnelConfig.DevTunnel.Resource, app.Services), CancellationToken.None));

        Assert.Contains("requires the Aspire dashboard", exception.Message);
        await AssertEnvironmentResolutionFailsAsync(environmentTask, exception);
    }

    [Fact]
    public async Task WithOtlpDevTunnel_ThrowsWhenDashboardHasNoAllocatedOtlpEndpoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        ClearDashboardOtlpEndpointConfiguration(appBuilder.Configuration);
        appBuilder.AddResource(new ContainerResource(KnownResourceNames.AspireDashboard));

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        maui.AddAndroidEmulator()
            .WithOtlpDevTunnel();

        var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().Single();

        await using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            appBuilder.Eventing.PublishAsync(new BeforeResourceStartedEvent(tunnelConfig.DevTunnel.Resource, app.Services), CancellationToken.None));

        Assert.Contains("does not have a concrete OTLP endpoint", exception.Message);
        Assert.Contains(KnownEndpointNames.OtlpGrpcEndpointName, exception.Message);
        Assert.Contains(KnownEndpointNames.OtlpHttpEndpointName, exception.Message);
    }

    [Fact]
    public async Task WithOtlpDevTunnel_RejectsSyntheticStubEndpoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        ClearDashboardOtlpEndpointConfiguration(appBuilder.Configuration);
        appBuilder.AddResource(new ContainerResource(KnownResourceNames.AspireDashboard));

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        maui.AddAndroidEmulator().WithOtlpDevTunnel();
        var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().Single();
        var stubEndpoint = tunnelConfig.OtlpStub.OtlpEndpoint;
        stubEndpoint.Port = 12345;
        stubEndpoint.TargetPort = 12345;
        stubEndpoint.AllocatedEndpoint = new AllocatedEndpoint(stubEndpoint, "localhost", 12345);

        await using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            appBuilder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(tunnelConfig.DevTunnel.Resource, app.Services),
                CancellationToken.None));

        Assert.Contains("does not have a concrete OTLP endpoint", exception.Message);
        Assert.False(tunnelConfig.IsOtlpEndpointResolved);
    }

    [Fact]
    public async Task WithOtlpDevTunnel_TimesOutWaitingForConcreteDashboardListener()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        ClearDashboardOtlpEndpointConfiguration(appBuilder.Configuration);
        var dashboard = appBuilder.AddResource(new ExecutableResource(KnownResourceNames.AspireDashboard, "dashboard", ""));
        dashboard.Resource.Annotations.Add(new EndpointAnnotation(
            ProtocolType.Tcp,
            name: KnownEndpointNames.OtlpHttpEndpointName,
            uriScheme: "http",
            isProxied: true));

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var iosSimulator = maui.AddiOSSimulator().WithOtlpDevTunnel();
        var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().Single();
        tunnelConfig.RuntimeSnapshotResolutionTimeout = TimeSpan.FromMilliseconds(50);
        // Use a same-name stand-in to exercise the MAUI resolver without invoking the real
        // DevTunnel resource's CLI-backed lifecycle after endpoint resolution recovers.
        var resolutionEventResource = new DevTunnelResource(
            tunnelConfig.DevTunnel.Resource.Name,
            "test",
            "devtunnel",
            Environment.CurrentDirectory);

        await using var app = appBuilder.Build();

        var dashboardEndpoint = dashboard.Resource.Annotations.OfType<EndpointAnnotation>().Single();
        dashboardEndpoint.AllocatedEndpoint = new AllocatedEndpoint(
            dashboardEndpoint,
            "localhost",
            55076,
            targetPortExpression: """{{- portForServing "dashboard-otlp-http" -}}""");
        await app.Services.GetRequiredService<ResourceNotificationService>()
            .PublishUpdateAsync(dashboard.Resource, snapshot => snapshot with
            {
                State = KnownResourceStates.Running
            });

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            appBuilder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(resolutionEventResource, app.Services),
                CancellationToken.None));

        Assert.Contains("did not publish a concrete OTLP listener", exception.Message);

        var environmentTask = EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            iosSimulator.Resource,
            DistributedApplicationOperation.Run,
            app.Services).AsTask();
        await AssertEnvironmentResolutionFailsAsync(environmentTask, exception);
        Assert.Null(tunnelConfig.TunnelEndpoint.EndpointAnnotation.AllocatedEndpoint);

        await app.Services.GetRequiredService<ResourceNotificationService>()
            .PublishUpdateAsync(dashboard.Resource, snapshot => snapshot with
            {
                EnvironmentVariables =
                [
                    new(
                        KnownConfigNames.DashboardOtlpHttpEndpointUrl,
                        "http://localhost:55077",
                        IsFromSpec: false)
                ]
            });
        await appBuilder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(resolutionEventResource, app.Services),
            CancellationToken.None);

        tunnelConfig.TunnelEndpoint.EndpointAnnotation.AllocatedEndpoint =
            new AllocatedEndpoint(tunnelConfig.TunnelEndpoint.EndpointAnnotation, "mobile-otlp.devtunnels.ms", 443);

        var recoveredEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            iosSimulator.Resource,
            DistributedApplicationOperation.Run,
            app.Services);
        Assert.Equal("https://mobile-otlp.devtunnels.ms:443", recoveredEnvironment[KnownOtelConfigNames.ExporterOtlpEndpoint]);
        Assert.Equal("http/protobuf", recoveredEnvironment[KnownOtelConfigNames.ExporterOtlpProtocol]);
    }

    [Fact]
    public async Task WithOtlpDevTunnel_EnvironmentEvaluationTimesOutWhenDashboardResolutionDoesNotStart()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        ClearDashboardOtlpEndpointConfiguration(appBuilder.Configuration);
        var dashboard = appBuilder.AddResource(new ExecutableResource(KnownResourceNames.AspireDashboard, "dashboard", ""));
        dashboard.Resource.Annotations.Add(new EndpointAnnotation(
            ProtocolType.Tcp,
            name: KnownEndpointNames.OtlpHttpEndpointName,
            uriScheme: "http",
            isProxied: true));

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var iosSimulator = maui.AddiOSSimulator().WithOtlpDevTunnel();
        var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().Single();
        tunnelConfig.RuntimeSnapshotResolutionTimeout = TimeSpan.FromMilliseconds(50);
        await using var app = appBuilder.Build();

        var exception = await Assert.ThrowsAsync<AggregateException>(async () =>
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                iosSimulator.Resource,
                DistributedApplicationOperation.Run,
                app.Services));

        Assert.Equal(2, exception.InnerExceptions.Count);
        Assert.All(exception.InnerExceptions, innerException => Assert.IsType<DistributedApplicationException>(innerException));
        Assert.Contains(exception.InnerExceptions, innerException => innerException.Message.Contains("endpoint could not be determined", StringComparison.Ordinal));
        Assert.Contains(exception.InnerExceptions, innerException => innerException.Message.Contains("protocol could not be determined", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Terminated")]
    [InlineData(nameof(KnownResourceStates.Exited))]
    public async Task WithOtlpDevTunnel_FailsWhenDashboardTerminatesWhileWaiting(string dashboardState)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-ios"));

        var appBuilder = DistributedApplication.CreateBuilder();
        ClearDashboardOtlpEndpointConfiguration(appBuilder.Configuration);
        var dashboard = appBuilder.AddResource(new ExecutableResource(KnownResourceNames.AspireDashboard, "dashboard", ""));
        dashboard.Resource.Annotations.Add(new EndpointAnnotation(
            ProtocolType.Tcp,
            name: KnownEndpointNames.OtlpHttpEndpointName,
            uriScheme: "http",
            isProxied: true));

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var iosSimulator = maui.AddiOSSimulator().WithOtlpDevTunnel();
        var tunnelConfig = maui.Resource.Annotations.OfType<OtlpDevTunnelConfigurationAnnotation>().Single();

        await using var app = appBuilder.Build();

        var dashboardEndpoint = dashboard.Resource.Annotations.OfType<EndpointAnnotation>().Single();
        dashboardEndpoint.AllocatedEndpoint = new AllocatedEndpoint(
            dashboardEndpoint,
            "localhost",
            55076,
            targetPortExpression: """{{- portForServing "dashboard-otlp-http" -}}""");

        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        await notificationService.PublishUpdateAsync(dashboard.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });

        var environmentTask = EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            iosSimulator.Resource,
            DistributedApplicationOperation.Run,
            app.Services).AsTask();
        Assert.False(environmentTask.IsCompleted);

        var beforeStartTask = appBuilder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(tunnelConfig.DevTunnel.Resource, app.Services),
            CancellationToken.None);
        Assert.False(beforeStartTask.IsCompleted);

        await notificationService.PublishUpdateAsync(dashboard.Resource, snapshot => snapshot with
        {
            State = dashboardState
        });

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => beforeStartTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("terminated", exception.Message);

        await AssertEnvironmentResolutionFailsAsync(environmentTask, exception);

        Assert.False(tunnelConfig.IsOtlpEndpointResolved);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("http://localhost:0")]
    [InlineData("https://example.com:4318")]
    public void WithOtlpDevTunnel_ThrowsForInvalidConfiguredOtlpEndpoint(string endpointUrl)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        appBuilder.Configuration[KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = endpointUrl;

        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var androidEmulator = maui.AddAndroidEmulator();

        var exception = Assert.Throws<DistributedApplicationException>(() => androidEmulator.WithOtlpDevTunnel());

        Assert.Contains(KnownConfigNames.DashboardOtlpGrpcEndpointUrl, exception.Message);
        Assert.Contains(endpointUrl, exception.Message);
    }

    // Helper methods

    private static async Task AssertEnvironmentResolutionFailsAsync(
        Task<Dictionary<string, string>> environmentTask,
        DistributedApplicationException expectedException)
    {
        var environmentException = await Assert.ThrowsAsync<AggregateException>(
            () => environmentTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.All(
            environmentException.InnerExceptions,
            innerException => Assert.Same(expectedException, innerException));
    }

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

    private static string? GetTestAssemblyConfiguration() =>
        typeof(MauiPlatformExtensionsTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;

    private static Task<SerializedMauiLaunchConfiguration> GetSingleMauiLaunchConfigurationAsync(IResource resource)
    {
        return DeserializeLaunchConfigurationAsync(resource);
    }

    /// <summary>
    /// Round-trips the launch configuration through JSON so assertions run against the wire shape the
    /// IDE receives (snake_case property names), not the in-memory type.
    /// </summary>
    private static async Task<SerializedMauiLaunchConfiguration> DeserializeLaunchConfigurationAsync(IResource resource)
    {
        var callbackContext = LaunchConfigurationTestHelpers.CreateCallbackContext(
            resource,
            ExecutableLaunchMode.Debug);
        var json = JsonSerializer.Serialize(
            await LaunchConfigurationTestHelpers.InvokeLaunchConfigurationProducerAsync(resource, callbackContext));
        var launchConfiguration = JsonSerializer.Deserialize<SerializedMauiLaunchConfiguration>(json);
        Assert.NotNull(launchConfiguration);

        return launchConfiguration;
    }

    private static void ClearDashboardOtlpEndpointConfiguration(ConfigurationManager configuration)
    {
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KnownConfigNames.DashboardOtlpGrpcEndpointUrl] = "",
            [KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl] = "",
            [KnownConfigNames.DashboardOtlpHttpEndpointUrl] = "",
            [KnownConfigNames.Legacy.DashboardOtlpHttpEndpointUrl] = ""
        });
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
