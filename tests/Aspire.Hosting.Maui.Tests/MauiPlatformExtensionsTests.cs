// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Maui;
using Aspire.Hosting.Maui.Annotations;
using Aspire.Hosting.Maui.Utilities;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;

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
            typeof(MauiWindowsPlatformResource)),
        
        new PlatformTestConfig("MacCatalyst", "Mac Catalyst", "maccatalyst", "mauiapp-maccatalyst", "net10.0-maccatalyst",
            (maui) => maui.AddMacCatalystDevice(),
            (maui, name) => maui.AddMacCatalystDevice(name),
            typeof(MauiMacCatalystPlatformResource)),
        
        new PlatformTestConfig("AndroidDevice", "Android", "android", "mauiapp-android-device", "net10.0-android",
            (maui) => maui.AddAndroidDevice(),
            (maui, name) => maui.AddAndroidDevice(name),
            typeof(MauiAndroidDeviceResource)),
        
        new PlatformTestConfig("AndroidEmulator", "Android", "android", "mauiapp-android-emulator", "net10.0-android",
            (maui) => maui.AddAndroidEmulator(),
            (maui, name) => maui.AddAndroidEmulator(name),
            typeof(MauiAndroidEmulatorResource)),
        
        new PlatformTestConfig("iOSDevice", "iOS", "ios", "mauiapp-ios-device", "net10.0-ios",
            (maui) => maui.AddiOSDevice(),
            (maui, name) => maui.AddiOSDevice(name),
            typeof(MauiiOSDeviceResource)),
        
        new PlatformTestConfig("iOSSimulator", "iOS", "ios", "mauiapp-ios-simulator", "net10.0-ios",
            (maui) => maui.AddiOSSimulator(),
            (maui, name) => maui.AddiOSSimulator(name),
            typeof(MauiiOSSimulatorResource))
    };

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_CreatesResourceWithCorrectName(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = CreateProjectContent(config.RequiredTfm);
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_WithCustomName_UsesProvidedName(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = CreateProjectContent(config.RequiredTfm);
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_DuplicateName_ThrowsException(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = CreateProjectContent(config.RequiredTfm);
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_HasCorrectAnnotations(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = CreateProjectContent(config.RequiredTfm);
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_ImplementsIMauiPlatformResource(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = CreateProjectContent(config.RequiredTfm);
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void AddPlatform_MultiplePlatforms_AllCreated(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = CreateProjectContent(config.RequiredTfm);
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public async Task AddPlatform_WithoutRequiredTfm_ThrowsOnBeforeStartEvent(PlatformTestConfig config)
    {
        // Arrange - Create project without the required TFM
        var projectContent = CreateProjectContentWithout(config.PlatformIdentifier);
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AddAndroidEmulator_WithEnvironment_EnvironmentVariablesAreSet()
    {
        // Arrange
        var projectContent = CreateProjectContent("net10.0-android");
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void AddAndroidDeviceAndEmulator_CanCoexist()
    {
        // Arrange
        var projectContent = CreateProjectContent("net10.0-android");
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void AddAndroidDevice_WithDeviceId_CreatesResourceWithCorrectName()
    {
        // Arrange
        var projectContent = CreateProjectContent("net10.0-android");
        var tempFile = CreateTempProjectFile(projectContent);

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
        }
        finally
        {
            CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void AddAndroidEmulator_WithEmulatorId_CreatesResourceWithCorrectName()
    {
        // Arrange
        var projectContent = CreateProjectContent("net10.0-android");
        var tempFile = CreateTempProjectFile(projectContent);

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
        }
        finally
        {
            CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void AddiOSDevice_WithDeviceId_CreatesResourceWithCorrectName()
    {
        // Arrange
        var projectContent = CreateProjectContent("net10.0-ios");
        var tempFile = CreateTempProjectFile(projectContent);

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
        }
        finally
        {
            CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void AddiOSSimulator_WithSimulatorId_CreatesResourceWithCorrectName()
    {
        // Arrange
        var projectContent = CreateProjectContent("net10.0-ios");
        var tempFile = CreateTempProjectFile(projectContent);

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
        }
        finally
        {
            CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public void AddiOSDeviceAndSimulator_CanCoexist()
    {
        // Arrange
        var projectContent = CreateProjectContent("net10.0-ios");
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [InlineData(true)]  // Device
    [InlineData(false)] // Simulator
    public void AddiOS_HasEnvironmentAnnotation(bool isDevice)
    {
        // Arrange
        var projectContent = CreateProjectContent("net10.0-ios");
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task AddiOSSimulator_WithEnvironment_EnvironmentVariablesAreSet()
    {
        // Arrange
        var projectContent = CreateProjectContent("net10.0-ios");
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [InlineData(true)]  // Device
    [InlineData(false)] // Emulator
    public void AddAndroid_HasEnvironmentAnnotation(bool isDevice)
    {
        // Arrange
        var projectContent = CreateProjectContent("net10.0-android");
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    // OTLP Dev Tunnel Configuration Tests

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void WithOtlpDevTunnel_AddsOtlpDevTunnelAnnotation(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = CreateProjectContent(config.RequiredTfm);
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public void WithOtlpDevTunnel_MultiplePlatforms_SharesSameInfrastructure(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = CreateProjectContent(config.RequiredTfm);
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(AllPlatforms))]
    public async Task WithOtlpDevTunnel_SetsEndpointWithoutIntermediateEnvironmentVariables(PlatformTestConfig config)
    {
        // Arrange
        var projectContent = CreateProjectContent(config.RequiredTfm);
        var tempFile = CreateTempProjectFile(projectContent);

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
            CleanupTempFile(tempFile);
        }
    }

    // Helper methods

    private static string CreateProjectContent(string requiredTfm)
    {
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">
                <PropertyGroup>
                    <TargetFrameworks>{{requiredTfm}};net10.0-ios</TargetFrameworks>
                </PropertyGroup>
            </Project>
            """;
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

    private static string CreateTempProjectFile(string content)
    {
        var tempFolder = Directory.CreateTempSubdirectory();
        var tempFile = Path.Combine(tempFolder.FullName, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, content);
        return tempFile;
    }

    private static void CleanupTempFile(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
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

        public PlatformTestConfig(
            string platformName,
            string displayName,
            string platformIdentifier,
            string expectedDefaultName,
            string requiredTfm,
            Func<IResourceBuilder<MauiProjectResource>, IResourceBuilder<IResource>> addDefault,
            Func<IResourceBuilder<MauiProjectResource>, string, IResourceBuilder<IResource>> addCustom,
            Type expectedResourceType)
        {
            PlatformName = platformName;
            DisplayName = displayName;
            PlatformIdentifier = platformIdentifier;
            ExpectedDefaultName = expectedDefaultName;
            RequiredTfm = requiredTfm;
            AddPlatformWithDefaultName = addDefault;
            AddPlatformWithCustomName = addCustom;
            ExpectedResourceType = expectedResourceType;
            
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
}
