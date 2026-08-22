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

    [Fact]
    public async Task AddAzureFrontDoorWithStampedOriginGeneratesOneOriginPerStamp()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var eastus = builder.AddAzureContainerAppEnvironment("aca-eastus").WithLocation("eastus");
        var westeu = builder.AddAzureContainerAppEnvironment("aca-westeu").WithLocation("westeurope");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironments(eastus, westeu);

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task AddAzureFrontDoorWithFailoverRoutingAssignsAscendingPriorities()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var eastus = builder.AddAzureContainerAppEnvironment("aca-eastus").WithLocation("eastus");
        var westeu = builder.AddAzureContainerAppEnvironment("aca-westeu").WithLocation("westeurope");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironments(eastus, westeu);

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOriginGroup(api, g => g.WithRouting(FrontDoorOriginRouting.Failover));

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task AddAzureFrontDoorWithWeightedRoutingAndCustomDomainGeneratesBicep()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var eastus = builder.AddAzureContainerAppEnvironment("aca-eastus").WithLocation("eastus");
        var westeu = builder.AddAzureContainerAppEnvironment("aca-westeu").WithLocation("westeurope");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironments(eastus, westeu);

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOriginGroup(api, g => g
                .WithRouting(FrontDoorOriginRouting.Weighted)
                .WithStampWeight(eastus, 900)
                .WithStampWeight(westeu, 100)
                .WithHealthProbe("/health", FrontDoorHealthProbeProtocol.Https, TimeSpan.FromSeconds(30))
                .WithLoadBalancing(sampleSize: 8, successfulSamplesRequired: 5, additionalLatencyMilliseconds: 100)
                .WithSessionAffinity(true)
                .WithTrafficRestorationTime(TimeSpan.FromMinutes(10))
                .WithCustomDomain("www.contoso.com"));

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task StampedOriginsUseShortStampNamesFromWithStamp()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var eastus = builder.AddAzureContainerAppEnvironment("aca-eastus").WithLocation("eastus");
        var westeu = builder.AddAzureContainerAppEnvironment("aca-westeu").WithLocation("westeurope");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithStamp(eastus, "eus")
            .WithStamp(westeu, "weu");

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        Assert.Contains("param api_eus_host string", bicep);
        Assert.Contains("param api_weu_host string", bicep);
        Assert.Contains("api_eusOrigin", bicep);
        Assert.Contains("api_weuOrigin", bicep);
    }

    [Fact]
    public async Task StampedOriginsShareASingleEndpointAndOriginGroup()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var eastus = builder.AddAzureContainerAppEnvironment("aca-eastus").WithLocation("eastus");
        var westeu = builder.AddAzureContainerAppEnvironment("aca-westeu").WithLocation("westeurope");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironments(eastus, westeu);

        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        // One global entry point: a single endpoint, origin group, and route fronting both regions.
        Assert.Equal(1, CountOccurrences(bicep, "'Microsoft.Cdn/profiles/afdEndpoints@"));
        Assert.Equal(1, CountOccurrences(bicep, "'Microsoft.Cdn/profiles/originGroups@"));
        Assert.Equal(1, CountOccurrences(bicep, "'Microsoft.Cdn/profiles/afdEndpoints/routes@"));
        Assert.Equal(2, CountOccurrences(bicep, "'Microsoft.Cdn/profiles/originGroups/origins@"));
        Assert.Equal(1, CountOccurrences(bicep, "output api_endpointUrl string"));
    }

    [Fact]
    public void WithStampPriorityRejectsOutOfRangeValues()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var eastus = builder.AddAzureContainerAppEnvironment("aca-eastus");
        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();

        var frontDoor = builder.AddAzureFrontDoor("frontdoor");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            frontDoor.WithOriginGroup(api, g => g.WithStampPriority(eastus, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            frontDoor.WithOriginGroup(api, g => g.WithStampPriority(eastus, 6)));
    }

    [Fact]
    public void WithStampWeightRejectsOutOfRangeValues()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var eastus = builder.AddAzureContainerAppEnvironment("aca-eastus");
        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();

        var frontDoor = builder.AddAzureFrontDoor("frontdoor");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            frontDoor.WithOriginGroup(api, g => g.WithStampWeight(eastus, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            frontDoor.WithOriginGroup(api, g => g.WithStampWeight(eastus, 1001)));
    }

    [Fact]
    public void WithOriginGroupThrowsOnNullConfigure()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();

        var frontDoor = builder.AddAzureFrontDoor("frontdoor");

        Assert.Throws<ArgumentNullException>(() => frontDoor.WithOriginGroup(api, null!));
    }

    [Fact]
    public async Task EachStampIsDeployedToItsOwnComputeEnvironmentRegion()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var eastus = builder.AddAzureContainerAppEnvironment("aca-eastus").WithLocation("eastus");
        var westeu = builder.AddAzureContainerAppEnvironment("aca-westeu").WithLocation("westeurope");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironments(eastus, westeu);

        builder.AddAzureFrontDoor("frontdoor").WithOrigin(api);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        // Azure requires a container app to live in the region of its managed environment, so each stamp's
        // deployment target must carry its own environment's region rather than the app-wide one.
        var targets = api.Resource.GetDeploymentTargetAnnotations();
        Assert.Equal(2, targets.Count);

        var locations = targets
            .Select(t => ((AzureBicepResource)t.DeploymentTarget).Parameters[AzureBicepResource.KnownParameters.Location])
            .ToArray();

        Assert.Equal(["eastus", "westeurope"], locations);
    }

    [Fact]
    public async Task SingleEnvironmentDeploymentTargetDoesNotPinALocation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddAzureContainerAppEnvironment("env");

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();

        builder.AddAzureFrontDoor("frontdoor").WithOrigin(api);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        // Without an explicit WithLocation the deployment target must keep resolving to the shared Azure
        // environment region, so no location is pinned on it.
        var target = Assert.Single(api.Resource.GetDeploymentTargetAnnotations());
        Assert.DoesNotContain(
            AzureBicepResource.KnownParameters.Location,
            ((AzureBicepResource)target.DeploymentTarget).Parameters.Keys);
    }

    private static int CountOccurrences(string value, string substring)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(substring, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += substring.Length;
        }

        return count;
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }
}
