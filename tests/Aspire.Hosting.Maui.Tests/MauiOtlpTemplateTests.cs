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
        var projectContent = MauiTestHelper.CreateProjectContent(tfm);
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var platformResource = AddPlatformByName(maui, platform);

            var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                platformResource.Resource,
                DistributedApplicationOperation.Run,
                TestServiceProvider.Instance);

            if (envVars.TryGetValue("OTEL_SERVICE_NAME", out var serviceName))
            {
                Assert.DoesNotContain("{{", serviceName);
                Assert.DoesNotContain("}}", serviceName);
            }
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Theory]
    [InlineData("Windows", "net10.0-windows10.0.19041.0")]
    [InlineData("Android", "net10.0-android")]
    [InlineData("iOS", "net10.0-ios")]
    [InlineData("MacCatalyst", "net10.0-maccatalyst")]
    public async Task PlatformResource_OtelResourceAttributes_DoesNotContainDcpPlaceholders(string platform, string tfm)
    {
        var projectContent = MauiTestHelper.CreateProjectContent(tfm);
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
            var appBuilder = DistributedApplication.CreateBuilder();
            var maui = appBuilder.AddMauiProject("mauiapp", tempFile);
            var platformResource = AddPlatformByName(maui, platform);

            var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                platformResource.Resource,
                DistributedApplicationOperation.Run,
                TestServiceProvider.Instance);

            if (envVars.TryGetValue("OTEL_RESOURCE_ATTRIBUTES", out var resourceAttrs))
            {
                Assert.DoesNotContain("{{", resourceAttrs);
                Assert.DoesNotContain("}}", resourceAttrs);
            }
        }
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
    }

    [Fact]
    public async Task PlatformResource_HasOtelExporterEndpoint()
    {
        var projectContent = MauiTestHelper.CreateProjectContent("net10.0-android");
        var tempFile = MauiTestHelper.CreateTempProjectFile(projectContent);

        try
        {
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
        finally
        {
            MauiTestHelper.CleanupTempFile(tempFile);
        }
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
