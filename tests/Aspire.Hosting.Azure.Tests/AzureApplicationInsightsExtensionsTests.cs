// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureApplicationInsightsExtensionsTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task AddApplicationInsightsWithoutExplicitLawGetsGeneratedLaw()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var appInsights = builder.AddAzureApplicationInsights("appInsights");

        appInsights.Resource.Outputs["appInsightsConnectionString"] = "myinstrumentationkey";

        var connectionStringResource = (IResourceWithConnectionString)appInsights.Resource;

        Assert.Equal("appInsights", appInsights.Resource.Name);
        Assert.Equal("myinstrumentationkey", await connectionStringResource.GetConnectionStringAsync());
        Assert.Equal("{appInsights.outputs.appInsightsConnectionString}", appInsights.Resource.ConnectionStringExpression.ValueExpression);

        var (appInsightsManifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(appInsights.Resource);

        await Verify(bicep, extension: "bicep")
              .AppendContentAsFile(appInsightsManifest.ToString(), "json");
    }

    [Fact]
    public async Task AddApplicationInsightsWithoutExplicitLawGetsDefaultLawParameterInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var appInsights = builder.AddAzureApplicationInsights("appInsights");

        appInsights.Resource.Outputs["appInsightsConnectionString"] = "myinstrumentationkey";

        var connectionStringResource = (IResourceWithConnectionString)appInsights.Resource;

        Assert.Equal("appInsights", appInsights.Resource.Name);
        Assert.Equal("myinstrumentationkey", await connectionStringResource.GetConnectionStringAsync());
        Assert.Equal("{appInsights.outputs.appInsightsConnectionString}", appInsights.Resource.ConnectionStringExpression.ValueExpression);

        var appInsightsManifest = await AzureManifestUtils.GetManifestWithBicep(appInsights.Resource);
        var expectedManifest = """
           {
             "type": "azure.bicep.v0",
             "connectionString": "{appInsights.outputs.appInsightsConnectionString}",
             "path": "appInsights.module.bicep"
           }
           """;
        Assert.Equal(expectedManifest, appInsightsManifest.ManifestNode.ToString());

        await Verify(appInsightsManifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddApplicationInsightsWithExplicitLawArgumentDoesntGetDefaultParameter()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var law = builder.AddAzureLogAnalyticsWorkspace("mylaw");
        var appInsights = builder.AddAzureApplicationInsights("appInsights", law);

        appInsights.Resource.Outputs["appInsightsConnectionString"] = "myinstrumentationkey";

        var connectionStringResource = (IResourceWithConnectionString)appInsights.Resource;

        Assert.Equal("appInsights", appInsights.Resource.Name);
        Assert.Equal("myinstrumentationkey", await connectionStringResource.GetConnectionStringAsync());
        Assert.Equal("{appInsights.outputs.appInsightsConnectionString}", appInsights.Resource.ConnectionStringExpression.ValueExpression);

        var (appInsightsManifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(appInsights.Resource);

        await Verify(bicep, extension: "bicep")
              .AppendContentAsFile(appInsightsManifest.ToString(), "json");
      }

    [Fact]
    public async Task WithReferenceAppInsightsSetsEnvironmentVariable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var appInsights = builder.AddAzureApplicationInsights("ai");

        appInsights.Resource.Outputs["appInsightsConnectionString"] = "myinstrumentationkey";

        var serviceA = builder.AddProject<ProjectA>("serviceA", o => o.ExcludeLaunchProfile = true)
            .WithReference(appInsights);

        var config = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(serviceA.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.True(config.ContainsKey("APPLICATIONINSIGHTS_CONNECTION_STRING"));
        Assert.Equal("myinstrumentationkey", config["APPLICATIONINSIGHTS_CONNECTION_STRING"]);
    }

    [Fact]
    public async Task WithLogAnalyticsWorkspaceId_UsesProvidedWorkspaceId_VerifyBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        // Doesn't need to be ACA but this is a more real scenario
        var env = builder.AddAzureContainerAppEnvironment("aca");

        var appInsights = builder.AddAzureApplicationInsights("appInsights")
                                 .WithLogAnalyticsWorkspace(env.GetOutput("AZURE_LOG_ANALYTICS_WORKSPACE_ID"));

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(appInsights.Resource);

        await Verify(bicep, extension: "bicep")
            .AppendContentAsFile(manifest.ToString(), "json");
    }

    [Fact]
    public async Task WithLogAnalyticsWorkspace_UsesWorkspaceResourceId_VerifyBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var law = builder.AddAzureLogAnalyticsWorkspace("law");
        var appInsights = builder.AddAzureApplicationInsights("appInsights");
        appInsights.WithLogAnalyticsWorkspace(law);
        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(appInsights.Resource);

        await Verify(bicep, extension: "bicep")
            .AppendContentAsFile(manifest.ToString(), "json");
    }

    private sealed class ProjectA : IProjectMetadata
    {
        public string ProjectPath => "projectA";
    }
}