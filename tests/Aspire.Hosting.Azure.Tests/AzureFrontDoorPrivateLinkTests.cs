// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates.
#pragma warning disable ASPIREACAEXPRESS001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Utils;
using Azure.Provisioning.Cdn;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureFrontDoorPrivateLinkTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task WithAzureFrontDoorPrivateLinkConvertsOriginsToPrivateLinkOrigins()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var environment = builder.AddAzureContainerAppEnvironment("env");
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        environment.WithAzureFrontDoorPrivateLink(frontDoor);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var web = builder.AddProject<Project>("web", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        frontDoor.WithOrigin(api).WithOrigin(web);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task WithAzureFrontDoorPrivateLinkDisablesPublicNetworkAccessOnTheEnvironment()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var environment = builder.AddAzureContainerAppEnvironment("env");
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        environment.WithAzureFrontDoorPrivateLink(frontDoor);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        frontDoor.WithOrigin(api);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(environment.Resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task WithAzureFrontDoorPrivateLinkOrderDoesNotMatter()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .WithOrigin(api);
        builder.AddAzureContainerAppEnvironment("env")
            .WithAzureFrontDoorPrivateLink(frontDoor);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        Assert.Contains("name: 'Premium_AzureFrontDoor'", bicep);
        Assert.Contains("groupId: 'managedEnvironments'", bicep);
    }

    [Fact]
    public async Task WithAzureFrontDoorPrivateLinkLocationCanBeOverriddenWithConfigureInfrastructure()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var environment = builder.AddAzureContainerAppEnvironment("env");
        var publicEnvironment = builder.AddAzureContainerAppEnvironment("public-env");
        var privateLinkLocation = builder.AddParameter("privateLinkLocation");
        var frontDoor = builder.AddAzureFrontDoor("frontdoor")
            .ConfigureInfrastructure(infrastructure =>
            {
                var location = privateLinkLocation.AsProvisioningParameter(infrastructure);
                foreach (var origin in infrastructure.GetProvisionableResources().OfType<FrontDoorOrigin>())
                {
                    var privateLink = origin.SharedPrivateLinkResource;
                    if (!privateLink.PrivateLinkId.IsEmpty)
                    {
                        privateLink.PrivateLinkLocation = location;
                    }
                }
            });
        environment.WithAzureFrontDoorPrivateLink(frontDoor);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(environment);
        var publicApi = builder.AddProject<Project>("public-api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(publicEnvironment);
        frontDoor.WithOrigin(api).WithOrigin(publicApi);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        Assert.Equal(1, CountOccurrences(bicep, "sharedPrivateLinkResource:"));
        Assert.Equal(1, CountOccurrences(bicep, "privateLinkLocation: privateLinkLocation"));
        Assert.DoesNotContain("privateLinkLocation: env_location", bicep);
    }

    [Fact]
    public async Task WithAzureFrontDoorPrivateLinkKeepsSameEnvironmentReferencesOnIngressHosts()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var environment = builder.AddAzureContainerAppEnvironment("env");
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        environment.WithAzureFrontDoorPrivateLink(frontDoor);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var web = builder.AddProject<Project>("web", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithReference(api)
            .WithEnvironment("API_HOST", api.GetEndpoint("https").Property(EndpointProperty.Host));
        frontDoor.WithOrigin(api).WithOrigin(web);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(GetContainerApp(web.Resource));

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task WithAzureFrontDoorPrivateLinkLeavesOriginsInOtherEnvironmentsPublic()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var privateEnvironment = builder.AddAzureContainerAppEnvironment("private-env");
        var publicEnvironment = builder.AddAzureContainerAppEnvironment("public-env");
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        privateEnvironment.WithAzureFrontDoorPrivateLink(frontDoor);

        var privateApi = builder.AddProject<Project>("private-api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(privateEnvironment);
        var publicApi = builder.AddProject<Project>("public-api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(publicEnvironment);
        frontDoor.WithOrigin(privateApi).WithOrigin(publicApi);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, frontDoorBicep) = await GetManifestWithBicep(frontDoor.Resource);
        var (_, publicEnvironmentBicep) = await GetManifestWithBicep(publicEnvironment.Resource);

        Assert.Equal(1, CountOccurrences(frontDoorBicep, "sharedPrivateLinkResource:"));
        Assert.DoesNotContain("publicNetworkAccess", publicEnvironmentBicep);
    }

    [Fact]
    public async Task WithAzureFrontDoorPrivateLinkConnectsEachFrontDoorToTheEnvironment()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var environment = builder.AddAzureContainerAppEnvironment("env");
        var first = builder.AddAzureFrontDoor("first");
        var second = builder.AddAzureFrontDoor("second");
        environment.WithAzureFrontDoorPrivateLink(first).WithAzureFrontDoorPrivateLink(second);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var web = builder.AddProject<Project>("web", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        first.WithOrigin(api);
        second.WithOrigin(web);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, firstBicep) = await GetManifestWithBicep(first.Resource);
        var (_, secondBicep) = await GetManifestWithBicep(second.Resource);

        Assert.Contains("requestMessage: 'Azure Front Door first requests access to env.'", firstBicep);
        Assert.Contains("requestMessage: 'Azure Front Door second requests access to env.'", secondBicep);
    }

    [Fact]
    public async Task WithAzureFrontDoorPrivateLinkConnectsOneFrontDoorToSeveralEnvironments()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var west = builder.AddAzureContainerAppEnvironment("env-west");
        var east = builder.AddAzureContainerAppEnvironment("env-east");
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        west.WithAzureFrontDoorPrivateLink(frontDoor);
        east.WithAzureFrontDoorPrivateLink(frontDoor);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(west);
        var admin = builder.AddProject<Project>("admin", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(west);
        var web = builder.AddProject<Project>("web", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(east);
        frontDoor.WithOrigin(api).WithOrigin(admin).WithOrigin(web);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        // Each environment has its own resource ID, region and request message, so Front Door creates one private
        // endpoint for each environment. The two origins in the same environment have the same settings, so they share one.
        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task WithAzureFrontDoorPrivateLinkThrowsWhenAnotherFrontDoorHasAnOriginInTheEnvironment()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var environment = builder.AddAzureContainerAppEnvironment("env");
        var connected = builder.AddAzureFrontDoor("connected");
        var unconnected = builder.AddAzureFrontDoor("unconnected");
        environment.WithAzureFrontDoorPrivateLink(connected);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var web = builder.AddProject<Project>("web", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        connected.WithOrigin(api);
        unconnected.WithOrigin(web);

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("Origin 'web' of Azure Front Door 'unconnected'", exception.ToString());
        Assert.Contains("WithAzureFrontDoorPrivateLink", exception.ToString());
    }

    [Fact]
    public async Task WithAzureFrontDoorPrivateLinkThrowsWhenNoOriginRunsInTheEnvironment()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var privateEnvironment = builder.AddAzureContainerAppEnvironment("private-env");
        var publicEnvironment = builder.AddAzureContainerAppEnvironment("public-env");
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        privateEnvironment.WithAzureFrontDoorPrivateLink(frontDoor);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(publicEnvironment);
        frontDoor.WithOrigin(api);

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("none of its origins run in that environment", exception.ToString());
    }

    [Fact]
    public async Task WithAzureFrontDoorPrivateLinkThrowsForExpressEnvironments()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var environment = builder.AddAzureContainerAppEnvironment("env").AsExpress();
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        environment.WithAzureFrontDoorPrivateLink(frontDoor);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        frontDoor.WithOrigin(api);

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("Express environment 'env' cannot be the target of a private link", exception.ToString());
    }

    [Fact]
    public async Task WithAzureFrontDoorPrivateLinkThrowsForExistingEnvironments()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var environment = builder.AddAzureContainerAppEnvironment("env")
            .PublishAsExisting("existing-env", "existing-rg");
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        environment.WithAzureFrontDoorPrivateLink(frontDoor);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        frontDoor.WithOrigin(api);

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("is marked as existing but is also the target of a private link", exception.ToString());
    }

    [Fact]
    public void WithAzureFrontDoorPrivateLinkIsIdempotent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var environment = builder.AddAzureContainerAppEnvironment("env");
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");

        environment.WithAzureFrontDoorPrivateLink(frontDoor);
        environment.WithAzureFrontDoorPrivateLink(frontDoor);

        Assert.Single(frontDoor.Resource.Annotations.OfType<AzureFrontDoorPrivateLinkAnnotation>());
        Assert.Single(environment.Resource.Annotations.OfType<PrivateEndpointTargetAnnotation>());
    }

    [Fact]
    public void WithAzureFrontDoorPrivateLinkDoesNothingInRunMode()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run, testOutputHelper);

        var environment = builder.AddAzureContainerAppEnvironment("env");
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");

        var result = environment.WithAzureFrontDoorPrivateLink(frontDoor);

        Assert.Same(environment, result);
        Assert.Empty(environment.Resource.Annotations.OfType<PrivateEndpointTargetAnnotation>());
        Assert.Empty(frontDoor.Resource.Annotations.OfType<AzureFrontDoorPrivateLinkAnnotation>());
    }

    [Fact]
    public void WithAzureFrontDoorPrivateLinkValidatesArguments()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var environment = builder.AddAzureContainerAppEnvironment("env");
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");

        Assert.Throws<ArgumentNullException>(() => ((IResourceBuilder<AzureContainerAppEnvironmentResource>)null!).WithAzureFrontDoorPrivateLink(frontDoor));
        Assert.Throws<ArgumentNullException>(() => environment.WithAzureFrontDoorPrivateLink(null!));
    }

    [Fact]
    public void RequestMessageStaysWithinTheFrontDoorLimit()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var environment = builder.AddAzureContainerAppEnvironment(new string('e', 55));
        var frontDoor = builder.AddAzureFrontDoor(new string('f', 55));
        environment.WithAzureFrontDoorPrivateLink(frontDoor);

        var connection = Assert.Single(frontDoor.Resource.Annotations.OfType<AzureFrontDoorPrivateLinkAnnotation>());

        Assert.Equal(140, connection.RequestMessage.Length);
    }

    [Fact]
    public void RemappedRegionsPointToRegionsWithFrontDoorPrivateLink()
    {
        // The regions of https://learn.microsoft.com/azure/frontdoor/private-link#region-availability, normalized.
        string[] supportedRegions =
        [
            "brazilsouth", "canadacentral", "centralus", "eastus", "eastus2", "southcentralus", "westus2", "westus3",
            "usgovarizona", "usgovtexas", "usgovvirginia", "usnateast", "usnatwest", "usseceast", "ussecwest",
            "francecentral", "germanywestcentral", "northeurope", "norwayeast", "uksouth", "westeurope", "swedencentral",
            "southafricanorth", "uaenorth",
            "australiaeast", "centralindia", "japaneast", "koreacentral", "eastasia", "southeastasia", "chinaeast3", "chinanorth3"
        ];

        foreach (var (region, privateLinkRegion) in FrontDoorPrivateLinkRegions.Remapped)
        {
            Assert.Matches("^[a-z0-9]+$", region);
            Assert.DoesNotContain(region, supportedRegions);
            Assert.Contains(privateLinkRegion, supportedRegions);
        }
    }

    [Fact]
    public async Task WithAzureFrontDoorPrivateLinkDeclaresTheRegionMapOnce()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var west = builder.AddAzureContainerAppEnvironment("west");
        var east = builder.AddAzureContainerAppEnvironment("east");
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        west.WithAzureFrontDoorPrivateLink(frontDoor);
        east.WithAzureFrontDoorPrivateLink(frontDoor);

        var westApi = builder.AddProject<Project>("west-api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(west);
        var eastApi = builder.AddProject<Project>("east-api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(east);
        frontDoor.WithOrigin(westApi).WithOrigin(eastApi);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        Assert.Equal(1, CountOccurrences(bicep, "var privateLinkRegions = {"));
        Assert.Contains("privateLinkLocation: (privateLinkRegions[?west_location] ?? west_location)", bicep);
        Assert.Contains("privateLinkLocation: (privateLinkRegions[?east_location] ?? east_location)", bicep);
    }

    [Fact]
    public async Task WithAzureFrontDoorPrivateLinkSupportsAnyPrivateLinkTarget()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var target = builder.AddResource(new TestPrivateLinkTarget("target"));
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        target.WithAzureFrontDoorPrivateLink(frontDoor);

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(target);
        frontDoor.WithOrigin(api);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(frontDoor.Resource);

        Assert.Contains("name: 'Premium_AzureFrontDoor'", bicep);
        Assert.Contains("id: target_environment_id", bicep);
        Assert.Contains("privateLinkLocation: (privateLinkRegions[?target_location] ?? target_location)", bicep);
        Assert.Contains("groupId: 'sites'", bicep);
        Assert.Contains("requestMessage: 'Azure Front Door frontdoor requests access to target.'", bicep);
        Assert.Single(target.Resource.Annotations.OfType<PrivateEndpointTargetAnnotation>());
    }

    [Fact]
    public void AzureContainerAppEnvironmentIsAPrivateLinkTarget()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var environment = builder.AddAzureContainerAppEnvironment("env");

        var target = Assert.IsAssignableFrom<IAzureCdnPrivateLinkTarget>(environment.Resource);
        Assert.Equal("managedEnvironments", target.PrivateLinkGroupId);
        Assert.Equal("AZURE_CONTAINER_APPS_ENVIRONMENT_ID", target.Id.Name);
        Assert.Equal("AZURE_CONTAINER_APPS_ENVIRONMENT_LOCATION", target.Location.Name);
    }

    private static IResource GetContainerApp(IResource resource)
    {
        var target = resource.GetDeploymentTargetAnnotation();
        Assert.NotNull(target);
        return Assert.IsAssignableFrom<AzureProvisioningResource>(target.DeploymentTarget);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }

    // A compute environment other than Azure Container Apps that Front Door can reach over Private Link.
    private sealed class TestPrivateLinkTarget(string name)
        : AzureProvisioningResource(name, _ => { }), IAzureCdnPrivateLinkTarget
    {
        public BicepOutputReference Id => new("id", this);

        public BicepOutputReference Location => new("location", this);

        public string PrivateLinkGroupId => "sites";

        public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
            => ReferenceExpression.Create($"{endpointReference.Resource.Name}.{new BicepOutputReference("domain", this)}");
    }
}
