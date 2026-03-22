// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Foundry;
using Aspire.Hosting.Utils;
using Microsoft.AI.Foundry.Local;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

public class FoundryExtensionsTests
{
    [Fact]
    public void AddFoundry_ShouldAddResourceToBuilder()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resourceBuilder = builder.AddFoundry("myAIFoundry");
        Assert.NotNull(resourceBuilder);
        var resource = Assert.Single(builder.Resources.OfType<FoundryResource>());
        Assert.Equal("myAIFoundry", resource.Name);
    }

    [Fact]
    public void AddDeployment_ShouldAddDeploymentToResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resourceBuilder = builder.AddFoundry("myAIFoundry");
        var deploymentBuilder = resourceBuilder.AddDeployment("deployment1", "gpt-4", "1.0", "OpenAI");
        Assert.NotNull(deploymentBuilder);
        var resource = Assert.Single(builder.Resources.OfType<FoundryResource>());
        var deployment = Assert.Single(resource.Deployments);
        Assert.Equal("deployment1", deployment.Name);
        Assert.Equal("deployment1", deployment.DeploymentName);
        Assert.Equal("gpt-4", deployment.ModelName);
        Assert.Equal("1.0", deployment.ModelVersion);
        Assert.Equal("OpenAI", deployment.Format);
    }

    [Fact]
    public void WithProperties_ShouldApplyConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resourceBuilder = builder.AddFoundry("myAIFoundry");
        var deploymentBuilder = resourceBuilder.AddDeployment("deployment1", "gpt-4", "1.0", "OpenAI");
        bool configured = false;
        deploymentBuilder.WithProperties(d =>
        {
            configured = true;
            d.ModelName = "changed";
        });
        Assert.True(configured);
        var resource = Assert.Single(builder.Resources.OfType<FoundryResource>());
        var deployment = Assert.Single(resource.Deployments);
        Assert.Equal("changed", deployment.ModelName);
    }

    [Fact]
    public void AddFoundry_ConnectionString_IsCorrect()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resourceBuilder = builder.AddFoundry("myAIFoundry");
        var resource = Assert.Single(builder.Resources.OfType<FoundryResource>());
        // The connection string should reference the aiFoundryApiEndpoint output
        var expected = $"Endpoint={resource.Endpoint.ValueExpression};EndpointAIInference={resource.AIFoundryApiEndpoint.ValueExpression}models";
        var connectionString = resource.ConnectionStringExpression.ValueExpression;
        Assert.Equal(expected, connectionString);
    }

    [Fact]
    public async Task RunAsFoundryLocal_SetsIsEmulator()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        using var builder = TestDistributedApplicationBuilder.Create();
        var resourceBuilder = builder.AddFoundry("myAIFoundry");
        var resource = Assert.Single(builder.Resources.OfType<FoundryResource>());
        Assert.False(resource.IsEmulator);
        Assert.Null(resource.ApiKey);

        var localBuilder = resourceBuilder.RunAsFoundryLocal();

        var localResource = Assert.Single(builder.Resources.OfType<FoundryResource>());
        Assert.True(localResource.IsEmulator);

        using var app = builder.Build();

        await app.StartAsync(cts.Token);

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        // Wait until it's not in Starting state anymore (started or failed whether the Foundry Local service is setup or not)
        await rns.WaitForResourceAsync(resource.Name, [KnownResourceStates.FailedToStart, KnownResourceStates.Running], cts.Token);

        var foundryManager = app.Services.GetRequiredService<FoundryLocalManager>();

        Assert.Equal(foundryManager.ApiKey, localResource.ApiKey);
    }

    [Fact]
    public void RunAsFoundryLocal_DeploymentIsMarkedLocal()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resourceBuilder = builder.AddFoundry("myAIFoundry");
        resourceBuilder.AddDeployment("deployment1", "gpt-4", "1.0", "OpenAI");
        var localBuilder = resourceBuilder.RunAsFoundryLocal();
        var localResource = Assert.Single(builder.Resources.OfType<FoundryResource>());
        Assert.True(localResource.IsEmulator);

        foreach (var deployment in localResource.Deployments)
        {
            Assert.True(deployment.Parent.IsEmulator);
        }
    }

    [Fact]
    public void RunAsFoundryLocal_DeploymentConnectionString_HasModelProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var foundry = builder.AddFoundry("myAIFoundry");
        var deployment = foundry.AddDeployment("deployment1", "gpt-4", "1.0", "OpenAI");

        foundry.RunAsFoundryLocal();

        var resource = Assert.Single(builder.Resources.OfType<FoundryResource>());

        Assert.Single(resource.Deployments);

        // NB: The value of the ModelName property is updated with the downloaded model id when the resource is starting.
        // We are only testing that the value in the ModelName property is referenced in the connection string.

        Assert.Equal("{myAIFoundry.connectionString};Model=gpt-4", deployment.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void RunAsFoundryLocal_DeploymentConnectionString_UsesModelId()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var foundry = builder.AddFoundry("myAIFoundry");
        var deployment = foundry.AddDeployment("deployment1", "gpt-4", "1.0", "OpenAI");
        foundry.RunAsFoundryLocal();

        deployment.Resource.ModelId = "custom-model-id";

        Assert.Equal("{myAIFoundry.connectionString};Model=custom-model-id", deployment.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void AIFoundry_DeploymentConnectionString_HasDeploymentProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var foundry = builder.AddFoundry("myAIFoundry");
        var deployment = foundry.AddDeployment("deployment1", "gpt-4", "1.0", "OpenAI");

        var resource = Assert.Single(builder.Resources.OfType<FoundryResource>());

        Assert.Single(resource.Deployments);
        Assert.Equal("{myAIFoundry.connectionString};Deployment=deployment1", deployment.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task AddFoundry_GeneratesValidBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var foundry = builder.AddFoundry("foundry");
        var deployment1 = foundry.AddDeployment("deployment1", "gpt-4", "1.0", "OpenAI");
        var deployment2 = foundry.AddDeployment("deployment2", "Phi-4", "1.0", "Microsoft");
        var deployment3 = foundry.AddDeployment("my-model", "Phi-4", "1.0", "Microsoft");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var manifest = await AzureManifestUtils.GetManifestWithBicep(model, foundry.Resource);

        var roles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "foundry-roles");
        var rolesManifest = await AzureManifestUtils.GetManifestWithBicep(model, roles);

        Assert.Contains("name: 'foundry-caphost'", manifest.BicepText);

        await Verify(manifest.BicepText, extension: "bicep")
            .AppendContentAsFile(rolesManifest.BicepText, "bicep");
    }

    [Fact]
    public void AddProject_SetsParentFoundryForProvisioningOrdering()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var foundry = builder.AddFoundry("myAIFoundry");
        var project = foundry
            .AddProject("my-project");

        Assert.Same(foundry.Resource, project.Resource.Parent);
    }

    [Fact]
    public async Task AddProject_WithPublishAsExistingFoundry_GeneratesBicepThatReferencesExistingParent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var project = builder.AddFoundry("foundry")
            .PublishAsExisting("existing-foundry", "existing-rg")
            .AddProject("project");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var (_, bicepText) = await AzureManifestUtils.GetManifestWithBicep(model, project.Resource);

        Assert.Contains("resource foundry 'Microsoft.CognitiveServices/accounts@", bicepText);
        Assert.Contains("existing = {", bicepText);
        Assert.Contains("name: 'existing-foundry'", bicepText);
        Assert.Contains("scope: resourceGroup('existing-rg')", bicepText);
        Assert.DoesNotContain("kind: 'AIServices'", bicepText);
    }

    [Fact]
    public async Task AddFoundry_WithPublishAsExisting_UsesStableDefaultCapabilityHostName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var foundry = builder.AddFoundry("logical-foundry")
            .PublishAsExisting("existing-foundry", "existing-rg");

        foundry.AddDeployment("chat", "gpt-4", "1.0", "OpenAI");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var (_, bicepText) = await AzureManifestUtils.GetManifestWithBicep(model, foundry.Resource);

        Assert.Contains("name: 'foundry-caphost'", bicepText);
        Assert.DoesNotContain("logical-foundry-caphost", bicepText);
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForFoundryResource()
    {
        // Arrange
        var aiFoundryResource = new FoundryResource("test-foundry", _ => { });
        var infrastructure = new AzureResourceInfrastructure(aiFoundryResource, "test-foundry");

        // Act - Call AddAsExistingResource twice
        var firstResult = aiFoundryResource.AddAsExistingResource(infrastructure);
        var secondResult = aiFoundryResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

}
