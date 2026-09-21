// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Azure.Provisioning.Search;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureSearchExtensionsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AddAzureSearch()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        // Add search and parameterize the SKU
        var sku = builder.AddParameter("searchSku");
        var search = builder.AddAzureSearch("search")
            .ConfigureInfrastructure(infrastructure =>
            {
                var search = infrastructure.GetProvisionableResources().OfType<SearchService>().Single();
                search.SearchSkuName = sku.AsProvisioningParameter(infrastructure);
            });

        // Pretend we deployed it
        const string fakeConnectionString = "mysearchconnectionstring";
        search.Resource.Outputs["connectionString"] = fakeConnectionString;

        var connectionStringResource = (IResourceWithConnectionString)search.Resource;

        // Validate the resource
        Assert.Equal("search", search.Resource.Name);
        Assert.Equal("{search.outputs.connectionString}", connectionStringResource.ConnectionStringExpression.ValueExpression);
        Assert.Equal(fakeConnectionString, await connectionStringResource.GetConnectionStringAsync());

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var manifest = await GetManifestWithBicep(model, search.Resource);

        // Validate the manifest
        var expectedManifest =
            """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{search.outputs.connectionString}",
              "path": "search.module.bicep",
              "params": {
                "searchSku": "{searchSku.value}"
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ManifestNode.ToString());

        await Verify(manifest.BicepText, extension: "bicep");

        var searchRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "search-roles");
        var searchRolesManifest = await GetManifestWithBicep(searchRoles, skipPreparer: true);
        var expectedBicep = """
            @description('The location for the resource(s) to be deployed.')
            param location string = resourceGroup().location

            param search_outputs_name string

            param principalType string

            param principalId string

            resource search 'Microsoft.Search/searchServices@2023-11-01' existing = {
              name: search_outputs_name
            }

            resource search_SearchIndexDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
              name: guid(search.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7'))
              properties: {
                principalId: principalId
                roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
                principalType: principalType
              }
              scope: search
            }

            resource search_SearchServiceContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
              name: guid(search.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7ca78c08-252a-4471-8644-bb5ff32d4ba0'))
              properties: {
                principalId: principalId
                roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
                principalType: principalType
              }
              scope: search
            }
            """;
        output.WriteLine(searchRolesManifest.BicepText);
        Assert.Equal(expectedBicep, searchRolesManifest.BicepText);
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureSearchResource()
    {
        // Arrange
        var searchResource = new AzureSearchResource("test-search", _ => { });
        var infrastructure = new AzureResourceInfrastructure(searchResource, "test-search");

        // Act - Call AddAsExistingResource twice
        var firstResult = searchResource.AddAsExistingResource(infrastructure);
        var secondResult = searchResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureSearchResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var existingName = builder.AddParameter("existing-search-name");
        var existingResourceGroup = builder.AddParameter("existing-search-rg");

        var search = builder.AddAzureSearch("test-search")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = search.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }
}