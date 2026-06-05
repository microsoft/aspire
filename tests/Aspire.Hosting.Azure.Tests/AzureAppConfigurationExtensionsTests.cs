// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureAppConfigurationExtensionsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AddAzureAppConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var appConfig = builder.AddAzureAppConfiguration("appConfig");
        appConfig.Resource.Outputs["appConfigEndpoint"] = "https://myendpoint";
        Assert.Equal("https://myendpoint", await appConfig.Resource.ConnectionStringExpression.GetValueAsync(default));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var manifest = await GetManifestWithBicep(model, appConfig.Resource);

        var connectionStringResource = (IResourceWithConnectionString)appConfig.Resource;

        Assert.Equal("https://myendpoint", await connectionStringResource.GetConnectionStringAsync());

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{appConfig.outputs.appConfigEndpoint}",
              "path": "appConfig.module.bicep"
            }
            """;
        Assert.Equal(expectedManifest, manifest.ManifestNode.ToString());

        var expectedBicep = """
            @description('The location for the resource(s) to be deployed.')
            param location string = resourceGroup().location

            resource appConfig 'Microsoft.AppConfiguration/configurationStores@2024-06-01' = {
              name: take('appConfig-${uniqueString(resourceGroup().id)}', 50)
              location: location
              properties: {
                disableLocalAuth: true
              }
              sku: {
                name: 'standard'
              }
              tags: {
                'aspire-resource-name': 'appConfig'
              }
            }

            output appConfigEndpoint string = appConfig.properties.endpoint

            output name string = appConfig.name

            output id string = appConfig.id
            """;
        output.WriteLine(manifest.BicepText);
        Assert.Equal(expectedBicep, manifest.BicepText);

        var appConfigRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "appConfig-roles");
        var appConfigRolesManifest = await GetManifestWithBicep(appConfigRoles, skipPreparer: true);
        expectedBicep = """
            @description('The location for the resource(s) to be deployed.')
            param location string = resourceGroup().location

            param appconfig_outputs_name string

            param principalType string

            param principalId string

            resource appConfig 'Microsoft.AppConfiguration/configurationStores@2024-06-01' existing = {
              name: appconfig_outputs_name
            }

            resource appConfig_AppConfigurationDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
              name: guid(appConfig.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b'))
              properties: {
                principalId: principalId
                roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b')
                principalType: principalType
              }
              scope: appConfig
            }
            """;
        output.WriteLine(appConfigRolesManifest.BicepText);
        Assert.Equal(expectedBicep, appConfigRolesManifest.BicepText);
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureAppConfigurationResource()
    {
        // Arrange
        var appConfigurationResource = new AzureAppConfigurationResource("test-app-config", _ => { });
        var infrastructure = new AzureResourceInfrastructure(appConfigurationResource, "test-app-config");

        // Act - Call AddAsExistingResource twice
        var firstResult = appConfigurationResource.AddAsExistingResource(infrastructure);
        var secondResult = appConfigurationResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureAppConfigurationResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-appconfig-name");
        var existingResourceGroup = builder.AddParameter("existing-appconfig-rg");

        var appConfig = builder.AddAzureAppConfiguration("test-app-config")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = appConfig.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }
}
