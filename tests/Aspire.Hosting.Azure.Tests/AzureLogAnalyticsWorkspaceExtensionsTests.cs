// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureLogAnalyticsWorkspaceExtensionsTests
{
    [Fact]
    public async Task AddLogAnalyticsWorkspace()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var logAnalyticsWorkspace = builder.AddAzureLogAnalyticsWorkspace("logAnalyticsWorkspace");

        Assert.Equal("logAnalyticsWorkspace", logAnalyticsWorkspace.Resource.Name);
        Assert.Equal("{logAnalyticsWorkspace.outputs.logAnalyticsWorkspaceId}", logAnalyticsWorkspace.Resource.Id.ValueExpression);

        var appInsightsManifest = await AzureManifestUtils.GetManifestWithBicep(logAnalyticsWorkspace.Resource);
        var expectedManifest = """
           {
             "type": "azure.bicep.v0",
             "path": "logAnalyticsWorkspace.module.bicep"
           }
           """;
        Assert.Equal(expectedManifest, appInsightsManifest.ManifestNode.ToString());

        await Verify(appInsightsManifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureLogAnalyticsWorkspaceResource()
    {
        // Arrange
        var logAnalyticsWorkspaceResource = new AzureLogAnalyticsWorkspaceResource("test-log-workspace", _ => { });
        var infrastructure = new AzureResourceInfrastructure(logAnalyticsWorkspaceResource, "test-log-workspace");

        // Act - Call AddAsExistingResource twice
        var firstResult = logAnalyticsWorkspaceResource.AddAsExistingResource(infrastructure);
        var secondResult = logAnalyticsWorkspaceResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureLogAnalyticsWorkspaceResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-logworkspace-name");
        var existingResourceGroup = builder.AddParameter("existing-logworkspace-rg");

        var logAnalyticsWorkspace = builder.AddAzureLogAnalyticsWorkspace("test-log-workspace")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = logAnalyticsWorkspace.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }
}
