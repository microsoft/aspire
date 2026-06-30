// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Maui;
using Aspire.Hosting.Tests.Utils;

namespace Aspire.Hosting.Tests;

/// <summary>
/// Tests that OTLP environment variables do not contain DCP template placeholders ({{...}})
/// after evaluation. MAUI resources resolve these templates eagerly because Android/iOS 
/// environment files are generated before DCP's template replacement happens.
/// </summary>
public class MauiOtlpTemplateTests
{
    [Theory]
    [InlineData("Windows", "net10.0-windows10.0.19041.0")]
    [InlineData("Android", "net10.0-android")]
    [InlineData("iOS", "net10.0-ios")]
    [InlineData("MacCatalyst", "net10.0-maccatalyst")]
    public async Task PlatformResource_OtelServiceName_DoesNotContainDcpPlaceholders(string platform, string tfm)
    {
        using var dir = new TestTempDirectory();
        var tempFile = Path.Combine(dir.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(tfm));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var platformResource = AddPlatformByName(maui, platform);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            platformResource.Resource,
            DistributedApplicationOperation.Run,
            TestServiceProvider.Instance);

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
        using var dir = new TestTempDirectory();
        var tempFile = Path.Combine(dir.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent(tfm));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var platformResource = AddPlatformByName(maui, platform);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            platformResource.Resource,
            DistributedApplicationOperation.Run,
            TestServiceProvider.Instance);

        Assert.True(envVars.ContainsKey("OTEL_RESOURCE_ATTRIBUTES"),
            "Expected OTEL_RESOURCE_ATTRIBUTES to be set on MAUI platform resource");
        var resourceAttrs = envVars["OTEL_RESOURCE_ATTRIBUTES"];
        Assert.DoesNotContain("{{", resourceAttrs);
        Assert.DoesNotContain("}}", resourceAttrs);
    }

    [Fact]
    public async Task PlatformResource_HasOtelExporterEndpoint()
    {
        using var dir = new TestTempDirectory();
        var tempFile = Path.Combine(dir.Path, "TempMauiProject.csproj");
        File.WriteAllText(tempFile, MauiTestHelper.CreateProjectContent("net10.0-android"));

        var appBuilder = DistributedApplication.CreateBuilder();
        var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
        var androidDevice = maui.AddAndroidDevice();

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            androidDevice.Resource,
            DistributedApplicationOperation.Run,
            TestServiceProvider.Instance);

        // OTEL_EXPORTER_OTLP_ENDPOINT should be present (set by WithOtlpExporter)
        Assert.True(envVars.ContainsKey("OTEL_EXPORTER_OTLP_ENDPOINT"),
            "Expected OTEL_EXPORTER_OTLP_ENDPOINT to be set on MAUI platform resource");
    }

    private static IResourceBuilder<IResource> AddPlatformByName(
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
