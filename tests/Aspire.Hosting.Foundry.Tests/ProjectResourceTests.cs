// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Foundry.Tests;

public class ProjectResourceTests
{
    [Fact]
    public void AddProject_CreatesProjectWithCorrectParent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var account = builder.AddFoundry("account");
        var project = account.AddProject("my-project");

        Assert.IsType<AzureCognitiveServicesProjectResource>(project.Resource);
        Assert.Equal("my-project", project.Resource.Name);
        Assert.Same(account.Resource, project.Resource.Parent);
    }

    [Fact]
    public void AddProject_ReferencesDefaultContainerRegistryForProvisioningOrdering()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var registry = project.Resource.ContainerRegistry;
        Assert.NotNull(registry);
        Assert.Same(project.Resource.DefaultContainerRegistry, registry);
    }

    [Fact]
    public void AddProject_InRunMode_ModelsDefaultContainerRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var registry = Assert.Single(builder.Resources.OfType<AzureContainerRegistryResource>());
        Assert.Equal("my-project-acr", registry.Name);
        Assert.Same(project.Resource.DefaultContainerRegistry, registry);
        Assert.Same(project.Resource.DefaultContainerRegistry, project.Resource.ContainerRegistry);
    }

    [Fact]
    public void WithContainerRegistry_ReferencesExplicitContainerRegistryForProvisioningOrdering()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registry = builder.AddAzureContainerRegistry("registry");
        var project = builder.AddFoundry("account")
            .AddProject("my-project")
            .WithContainerRegistry(registry);

        Assert.Same(registry.Resource, project.Resource.ContainerRegistry);
        Assert.True(project.Resource.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var annotation));
        Assert.Same(registry.Resource, annotation.Registry);
    }

    [Fact]
    public void ConnectionStringExpression_HasCorrectFormat()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var expr = project.Resource.ConnectionStringExpression;
        Assert.Equal("Endpoint={my-project.outputs.endpoint}", expr.ValueExpression);
    }

    [Fact]
    public void UriExpression_ReferencesEndpointOutput()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var expr = project.Resource.UriExpression;
        Assert.Equal("{my-project.outputs.endpoint}", expr.ValueExpression);
    }

    [Fact]
    public void GetConnectionProperties_ReturnsUriAndConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var properties = ((IResourceWithConnectionString)project.Resource)
            .GetConnectionProperties()
            .ToArray();

        Assert.Equal(3, properties.Length);
        Assert.Equal("Uri", properties[0].Key);
        Assert.Equal("{my-project.outputs.endpoint}", properties[0].Value.ValueExpression);
        Assert.Equal("ConnectionString", properties[1].Key);
        Assert.Equal("ApplicationInsightsConnectionString", properties[2].Key);
        Assert.Equal("{my-project.outputs.APPLICATION_INSIGHTS_CONNECTION_STRING}", properties[2].Value.ValueExpression);
    }

    [Fact]
    public void WithAppInsights_SetsAppInsightsResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var appInsights = builder.AddAzureApplicationInsights("ai");
        var project = builder.AddFoundry("account")
            .AddProject("my-project")
            .WithAppInsights(appInsights);

        Assert.Same(appInsights.Resource, project.Resource.AppInsights);
    }

    [Fact]
    public void AddModelDeployment_AddsDeploymentToParentFoundry()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var deployment = project.AddModelDeployment("chat", "gpt-4", "1.0", "OpenAI");

        Assert.NotNull(deployment);
        Assert.Equal("chat", deployment.Resource.Name);
        Assert.Equal("gpt-4", deployment.Resource.ModelName);
    }

    [Fact]
    public void AddModelDeployment_WithModel_AddsDeployment()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var model = new FoundryModel { Name = "gpt-4", Version = "1.0", Format = "OpenAI" };
        var deployment = project.AddModelDeployment("chat", model);

        Assert.NotNull(deployment);
        Assert.Equal("chat", deployment.Resource.Name);
    }

    [Fact]
    public void AddCapabilityHost_SetsCapabilityHostConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmosDb = builder.AddAzureCosmosDB("cosmos");
        var storage = builder.AddAzureStorage("storage");
        var search = builder.AddAzureSearch("search");

        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        project.AddCapabilityHost("cap-host")
            .WithCosmosDB(cosmosDb)
            .WithStorage(storage)
            .WithSearch(search);

        Assert.NotNull(project.Resource.CapabilityHostConfiguration);
        Assert.Equal("cap-host", project.Resource.CapabilityHostConfiguration.Name);
        Assert.Same(cosmosDb.Resource, project.Resource.CapabilityHostConfiguration.CosmosDB);
        Assert.Same(storage.Resource, project.Resource.CapabilityHostConfiguration.Storage);
        Assert.Same(search.Resource, project.Resource.CapabilityHostConfiguration.Search);
    }

    [Fact]
    public void AddCapabilityHost_WithOptionalOpenAI_SetsAzureOpenAI()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmosDb = builder.AddAzureCosmosDB("cosmos");
        var storage = builder.AddAzureStorage("storage");
        var search = builder.AddAzureSearch("search");
        var foundry = builder.AddFoundry("account");

        var project = foundry.AddProject("my-project");

        project.AddCapabilityHost("cap-host")
            .WithCosmosDB(cosmosDb)
            .WithStorage(storage)
            .WithSearch(search)
            .WithAzureOpenAI(foundry);

        Assert.NotNull(project.Resource.CapabilityHostConfiguration);
        Assert.Same(foundry.Resource, project.Resource.CapabilityHostConfiguration.AzureOpenAI);
    }

}
