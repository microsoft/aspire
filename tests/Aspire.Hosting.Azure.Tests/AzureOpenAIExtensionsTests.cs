// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.CognitiveServices;
using Aspire.Hosting.Utils;
using Azure.Provisioning.CognitiveServices;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureOpenAIExtensionsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AddAzureOpenAI(bool overrideLocalAuthDefault, bool useObsoleteApis)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        IEnumerable<CognitiveServicesAccountDeployment>? aiDeployments = null;
        var openai = builder.AddAzureOpenAI("openai")
            .ConfigureInfrastructure(infrastructure =>
            {
                aiDeployments = infrastructure.GetProvisionableResources().OfType<CognitiveServicesAccountDeployment>();

                if (overrideLocalAuthDefault)
                {
                    var account = infrastructure.GetProvisionableResources().OfType<CognitiveServicesAccount>().Single();
                    account.Properties.DisableLocalAuth = false;
                }
            });

        if (useObsoleteApis)
        {
#pragma warning disable CS0618 // Type or member is obsolete
            openai.AddDeployment(new("mymodel", "gpt-35-turbo", "0613", "Basic", 4))
                .AddDeployment(new("embedding-model", "text-embedding-ada-002", "2", "Basic", 4));
#pragma warning restore CS0618 // Type or member is obsolete
        }
        else
        {
            openai.AddDeployment("mymodel", "gpt-35-turbo", "0613")
                .WithProperties(d =>
                {
                    d.SkuName = "Basic";
                    d.SkuCapacity = 4;
                });
            openai.AddDeployment("embedding-model", "text-embedding-ada-002", "2")
                .WithProperties(d =>
                {
                    d.SkuName = "Basic";
                    d.SkuCapacity = 4;
                });
        }

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var manifest = await GetManifestWithBicep(model, openai.Resource);

        Assert.NotNull(aiDeployments);
        Assert.Collection(
            aiDeployments,
            deployment => Assert.Equal("mymodel", deployment.Name.Value),
            deployment => Assert.Equal("embedding-model", deployment.Name.Value));

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{openai.outputs.connectionString}",
              "path": "openai.module.bicep"
            }
            """;
        Assert.Equal(expectedManifest, manifest.ManifestNode.ToString());

        await Verify(manifest.BicepText, extension: "bicep");

        var openaiRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "openai-roles");
        var openaiRolesManifest = await GetManifestWithBicep(openaiRoles, skipPreparer: true);
        var expectedBicep = """
            @description('The location for the resource(s) to be deployed.')
            param location string = resourceGroup().location

            param openai_outputs_name string

            param principalType string

            param principalId string

            resource openai 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
              name: openai_outputs_name
            }

            resource openai_CognitiveServicesOpenAIUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
              name: guid(openai.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'))
              properties: {
                principalId: principalId
                roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
                principalType: principalType
              }
              scope: openai
            }
            """;
        output.WriteLine(openaiRolesManifest.BicepText);
        Assert.Equal(expectedBicep, openaiRolesManifest.BicepText);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_ValidRoles_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var openai = builder.AddAzureOpenAI("openai");
        var container = builder.AddContainer("myContainer", "nginx");

        var exception = Record.Exception(() =>
            container.WithRoleAssignments(openai, AzureOpenAIRole.CognitiveServicesOpenAIUser, AzureOpenAIRole.CognitiveServicesOpenAIContributor));

        Assert.Null(exception);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_SingleRole_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var openai = builder.AddAzureOpenAI("openai");
        var container = builder.AddContainer("myContainer", "nginx");

        var exception = Record.Exception(() =>
            container.WithRoleAssignments(openai, AzureOpenAIRole.CognitiveServicesUser));

        Assert.Null(exception);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_InvalidRole_ThrowsArgumentException()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var openai = builder.AddAzureOpenAI("openai");
        var container = builder.AddContainer("myContainer", "nginx");
        var invalidRole = (AzureOpenAIRole)(-1);

        var exception = Assert.Throws<ArgumentException>(() =>
            container.WithRoleAssignments(openai, invalidRole));

        Assert.Contains("is not a valid AzureOpenAIRole value", exception.Message);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_MixedValidAndInvalidRoles_ThrowsOnInvalid()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var openai = builder.AddAzureOpenAI("openai");
        var container = builder.AddContainer("myContainer", "nginx");
        var invalidRole = (AzureOpenAIRole)123;

        var exception = Assert.Throws<ArgumentException>(() =>
            container.WithRoleAssignments(openai, AzureOpenAIRole.CognitiveServicesOpenAIUser, invalidRole));

        Assert.Contains("is not a valid AzureOpenAIRole value", exception.Message);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_DuplicateRoles_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var openai = builder.AddAzureOpenAI("openai");
        var container = builder.AddContainer("myContainer", "nginx");

        var exception = Record.Exception(() =>
            container.WithRoleAssignments(openai, AzureOpenAIRole.CognitiveServicesOpenAIUser, AzureOpenAIRole.CognitiveServicesOpenAIUser));

        Assert.Null(exception);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_AllBuiltInRoles_AreAccepted()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var openai = builder.AddAzureOpenAI("openai");
        var container = builder.AddContainer("myContainer", "nginx");

        var roles = new[]
        {
            AzureOpenAIRole.CognitiveServicesOpenAIContributor,
            AzureOpenAIRole.CognitiveServicesOpenAIUser,
            AzureOpenAIRole.CognitiveServicesUser
        };

        foreach (var role in roles)
        {
            var exception = Record.Exception(() =>
                container.WithRoleAssignments(openai, role));

            Assert.Null(exception);
        }
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_EmptyRoles_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var openai = builder.AddAzureOpenAI("openai");
        var container = builder.AddContainer("myContainer", "nginx");

        var exception = Record.Exception(() =>
            container.WithRoleAssignments(openai, Array.Empty<AzureOpenAIRole>()));

        Assert.Null(exception);
    }

    [Fact]
    public void WithRoleAssignments_EnumOverload_NullRoles_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var openai = builder.AddAzureOpenAI("openai");
        var container = builder.AddContainer("myContainer", "nginx");

        var exception = Record.Exception(() =>
            container.WithRoleAssignments(openai, (AzureOpenAIRole[]?)null!));

        Assert.Null(exception);
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureOpenAIResource()
    {
        // Arrange
        var openAIResource = new AzureOpenAIResource("test-openai", _ => { });
        var infrastructure = new AzureResourceInfrastructure(openAIResource, "test-openai");

        // Act - Call AddAsExistingResource twice
        var firstResult = openAIResource.AddAsExistingResource(infrastructure);
        var secondResult = openAIResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureOpenAIResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-openai-name");
        var existingResourceGroup = builder.AddParameter("existing-openai-rg");

        var openAI = builder.AddAzureOpenAI("test-openai")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = openAI.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }
}
