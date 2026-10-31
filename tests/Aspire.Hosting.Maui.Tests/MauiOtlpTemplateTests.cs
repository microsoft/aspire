// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Maui;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

/// <summary>
/// Tests that OTLP environment variables do not contain DCP template placeholders ({{...}})
/// after evaluation. MAUI resources resolve these templates eagerly because Android/iOS 
/// environment files are generated before DCP's template replacement happens.
/// </summary>
public class MauiOtlpTemplateTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("Windows", "net10.0-windows10.0.19041.0")]
    [InlineData("Android", "net10.0-android")]
    [InlineData("iOS", "net10.0-ios")]
    [InlineData("MacCatalyst", "net10.0-maccatalyst")]
    public async Task PlatformResource_OtelServiceName_DoesNotContainDcpPlaceholders(string platform, string tfm)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(tfm));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var platformResource = AddPlatformByName(maui, platform);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            platformResource.Resource,
            DistributedApplicationOperation.Run,
            TestServiceProvider.Instance).DefaultTimeout();

        Assert.True(envVars.ContainsKey("OTEL_SERVICE_NAME"),
            "Expected OTEL_SERVICE_NAME to be set on MAUI platform resource");
        var serviceName = envVars["OTEL_SERVICE_NAME"];
        Assert.DoesNotContain("{{", serviceName);
        Assert.DoesNotContain("}}", serviceName);
    }

    [Theory]
    [InlineData("Windows", "net10.0-windows10.0.19041.0")]
    [InlineData("Android", "net10.0-android")]
    [InlineData("iOS", "net10.0-ios")]
    [InlineData("MacCatalyst", "net10.0-maccatalyst")]
    public async Task PlatformResource_OtelResourceAttributes_DoesNotContainDcpPlaceholders(string platform, string tfm)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(tfm));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var platformResource = AddPlatformByName(maui, platform);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            platformResource.Resource,
            DistributedApplicationOperation.Run,
            TestServiceProvider.Instance).DefaultTimeout();

        Assert.True(envVars.ContainsKey("OTEL_RESOURCE_ATTRIBUTES"),
            "Expected OTEL_RESOURCE_ATTRIBUTES to be set on MAUI platform resource");
        var resourceAttrs = envVars["OTEL_RESOURCE_ATTRIBUTES"];
        Assert.DoesNotContain("{{", resourceAttrs);
        Assert.DoesNotContain("}}", resourceAttrs);
    }

    [Theory]
    [InlineData("Windows", "net10.0-windows10.0.19041.0")]
    [InlineData("Android", "net10.0-android")]
    [InlineData("iOS", "net10.0-ios")]
    [InlineData("MacCatalyst", "net10.0-maccatalyst")]
    public async Task PlatformResource_UserResourceAttributes_KeepResolvedServiceInstanceId(string platform, string tfm)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(tfm));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);

        // The platform resource replaces the DCP template with a GUID before this value is applied, so the
        // GUID is the id that has to be kept, not the template.
        var platformResource = AddPlatformByName(maui, platform)
            .WithEnvironment("OTEL_RESOURCE_ATTRIBUTES", "deployment.environment=dev");

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            platformResource.Resource,
            DistributedApplicationOperation.Run,
            TestServiceProvider.Instance).DefaultTimeout();

        const string expectedPrefix = "deployment.environment=dev,service.instance.id=";
        var resourceAttrs = envVars["OTEL_RESOURCE_ATTRIBUTES"];
        Assert.StartsWith(expectedPrefix, resourceAttrs);
        Assert.True(Guid.TryParse(resourceAttrs[expectedPrefix.Length..], out _),
            $"Expected a GUID service.instance.id, got '{resourceAttrs}'");
    }

    [Fact]
    public async Task PlatformResource_HasOtelExporterEndpoint()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var tempFile = Path.Combine(workspace.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var androidDevice = maui.AddAndroidDevice();

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            androidDevice.Resource,
            DistributedApplicationOperation.Run,
            TestServiceProvider.Instance).DefaultTimeout();

        // OTEL_EXPORTER_OTLP_ENDPOINT should be present (set by WithOtlpExporter)
        Assert.True(envVars.ContainsKey("OTEL_EXPORTER_OTLP_ENDPOINT"),
            "Expected OTEL_EXPORTER_OTLP_ENDPOINT to be set on MAUI platform resource");
    }

    private static IResourceBuilder<ProjectResource> AddPlatformByName(
        IResourceBuilder<MauiProjectResource> maui, string platform)
    {
        return platform switch
        {
            "Windows" => maui.AddWindowsDevice(),
            "Android" => maui.AddAndroidDevice(),
            "iOS" => maui.AddiOSDevice(),
            "MacCatalyst" => maui.AddMacCatalystDevice(),
            _ => throw new ArgumentException($"Unknown platform: {platform}")
        };
    }
}
