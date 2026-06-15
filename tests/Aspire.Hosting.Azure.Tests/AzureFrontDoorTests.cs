// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIREPROBES001

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
    public async Task AddAzureFrontDoorWithSingleOriginGeneratesBicep()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var api = builder.AddProject<Project>("my-api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        await Verify(bicep, "bicep");

        // Verify GetEndpointUrl normalizes the dashed name to match the bicep output
        var endpointUrl = frontDoor.Resource.GetEndpointUrl("my-api");
        Assert.Equal("my_api_endpointUrl", endpointUrl.Name);
    }

    [Fact]
    public async Task AddAzureFrontDoorWithMultipleOriginsGeneratesBicep()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

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
    public async Task AddAzureFrontDoorThrowsWhenOriginHasNoExternalEndpoints()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var api = builder.AddProject<Project>("api", launchProfileName: null);

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api);

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("does not have an external HTTP or HTTPS endpoint", exception.ToString());
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

        Assert.Throws<ArgumentNullException>(() => frontDoor.WithOrigin((IResourceBuilder<ProjectResource>)null!));
    }

    [Fact]
    public async Task HealthProbePathUsesResourceProbeAnnotation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithHttpProbe(ProbeType.Liveness, "/health");

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        Assert.Contains("probePath: '/health'", bicep);
    }

    [Fact]
    public async Task HealthProbePathDefaultsToSlashWhenNoProbeAnnotation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        Assert.Contains("probePath: '/'", bicep);
    }

    [Fact]
    public async Task WithOriginSkipsNonHttpEndpoints()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        // Add a resource with a non-HTTP endpoint and an HTTP endpoint
        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithEndpoint(scheme: "tcp", name: "grpc")
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        // Should generate valid bicep (picked the HTTPS endpoint, not the TCP one)
        Assert.Contains("hostName: api_host", bicep);
    }

    [Fact]
    public void WithOriginThrowsOnDuplicateOrigin()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api);

        var exception = Assert.Throws<InvalidOperationException>(() => frontDoor.WithOrigin(api));

        Assert.Contains("has already been added", exception.Message);
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }
}
