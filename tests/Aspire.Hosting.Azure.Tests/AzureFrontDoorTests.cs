// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureFrontDoorTests
{
    [Fact]
    public void AddAzureFrontDoorCreatesResource()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var frontDoor = builder.AddAzureFrontDoor("frontdoor");

        Assert.NotNull(frontDoor);
        Assert.Equal("frontdoor", frontDoor.Resource.Name);
        Assert.IsType<AzureFrontDoorResource>(frontDoor.Resource);
    }

    [Fact]
    public void WithOriginAddsAnnotation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint();

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api);

        var annotations = frontDoor.Resource.Annotations.OfType<AzureFrontDoorOriginAnnotation>().ToList();
        Assert.Single(annotations);
    }

    [Fact]
    public void WithOriginSupportsMultipleOrigins()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint();
        var web = builder.AddProject<Project>("web", launchProfileName: null)
            .WithHttpsEndpoint();

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api)
            .WithOrigin(web);

        var annotations = frontDoor.Resource.Annotations.OfType<AzureFrontDoorOriginAnnotation>().ToList();
        Assert.Equal(2, annotations.Count);
    }

    [Fact]
    public async Task AddAzureFrontDoorWithAppServiceGeneratesBicep()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task AddAzureFrontDoorWithMultipleOriginsGeneratesBicep()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var web = builder.AddProject<Project>("web", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api)
            .WithOrigin(web);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task AddAzureFrontDoorThrowsWhenOriginHasNoEndpoints()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureAppServiceEnvironment("env");

        var api = builder.AddProject<Project>("api", launchProfileName: null);

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => GetManifestWithBicep(frontDoor.Resource));

        Assert.Equal(
            "Resource 'api' does not have any endpoints. Azure Front Door requires a resource to expose at least one endpoint before it can be added as an origin.",
            exception.Message);
    }

    [Fact]
    public void EndpointUrlOutputReferenceIsAvailable()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var frontDoor = builder.AddAzureFrontDoor("frontdoor");

        var endpointUrl = frontDoor.Resource.GetEndpointUrl("api");
        Assert.NotNull(endpointUrl);
        Assert.Equal("api_endpointUrl", endpointUrl.Name);
    }

    [Fact]
    public void AddAzureFrontDoorThrowsOnNullName()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        Assert.Throws<ArgumentNullException>(() => builder.AddAzureFrontDoor(null!));
    }

    [Fact]
    public void WithOriginThrowsOnNullResource()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");

        Assert.Throws<ArgumentNullException>(() => frontDoor.WithOrigin((IResourceBuilder<IResourceWithEndpoints>)null!));
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }
}
