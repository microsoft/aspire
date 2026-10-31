// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Private networking APIs are experimental.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureFrontDoorAppServicePrivateLinkTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task PublishExposesWebsiteOutputsAsInfrastructureInputs()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish, testOutputHelper, workspace.Path);
        var environment = builder.AddAzureAppServiceEnvironment("env").WithDashboard(false);
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        environment.WithAzureFrontDoorPrivateLink(frontDoor);
        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();
        frontDoor.WithOrigin(api);

        using var app = builder.Build();
        await app.RunAsync();

        await Verify(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "main.bicep")), "bicep");
        Assert.True(File.Exists(Path.Combine(workspace.Path, "api", "api.bicep")));
        Assert.True(File.Exists(Path.Combine(workspace.Path, "frontdoor", "frontdoor.bicep")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrivateLinkTargetsEachWebsite(bool connectBeforeOrigins)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var environment = builder.AddAzureAppServiceEnvironment("env").WithDashboard(false);
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        if (connectBeforeOrigins)
        {
            environment.WithAzureFrontDoorPrivateLink(frontDoor);
        }

        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();
        var web = builder.AddProject<Project>("web", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();
        frontDoor.WithOrigin(api).WithOrigin(web);
        if (!connectBeforeOrigins)
        {
            environment.WithAzureFrontDoorPrivateLink(frontDoor);
        }

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, frontDoorBicep) = await GetManifestWithBicep(frontDoor.Resource);
        var (_, apiBicep) = await GetManifestWithBicep(GetWebsite(api.Resource, environment.Resource));
        var (_, webBicep) = await GetManifestWithBicep(GetWebsite(web.Resource, environment.Resource));

        await Verify(new { frontDoorBicep, apiBicep, webBicep })
            .UseParameters(connectBeforeOrigins);
    }

    [Fact]
    public async Task ExistingPlanCanHostPrivateWebsites()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var environment = builder.AddAzureAppServiceEnvironment("env")
            .PublishAsExisting("existing-plan", "existing-rg")
            .WithDashboard(false);
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        environment.WithAzureFrontDoorPrivateLink(frontDoor);
        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();
        frontDoor.WithOrigin(api);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(GetWebsite(api.Resource, environment.Resource));
        await Verify(bicep, "bicep");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeploymentSlotsAreRejected(bool parameterized)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var environment = builder.AddAzureAppServiceEnvironment("env");
        if (parameterized)
        {
            environment.WithDeploymentSlot(builder.AddParameter("slot"));
        }
        else
        {
            environment.WithDeploymentSlot("staging");
        }
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        environment.WithAzureFrontDoorPrivateLink(frontDoor);
        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();
        frontDoor.WithOrigin(api);

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));
        Assert.Contains("uses a deployment slot, which Azure Front Door Private Link does not support", exception.ToString());
    }

    [Fact]
    public async Task MixedComputeEnvironmentsUseTheirOwnPrivateLinkTargets()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var appService = builder.AddAzureAppServiceEnvironment("sites").WithDashboard(false);
        var containerApps = builder.AddAzureContainerAppEnvironment("apps");
        var publicEnvironment = builder.AddAzureAppServiceEnvironment("public-env").WithDashboard(false);
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        appService.WithAzureFrontDoorPrivateLink(frontDoor);
        containerApps.WithAzureFrontDoorPrivateLink(frontDoor);
        var web = builder.AddProject<Project>("web", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(appService);
        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(containerApps);
        var publicApp = builder.AddProject<Project>("public-app", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(publicEnvironment);
        frontDoor.WithOrigin(web).WithOrigin(api).WithOrigin(publicApp);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, frontDoorBicep) = await GetManifestWithBicep(frontDoor.Resource);
        var (_, publicWebsiteBicep) = await GetManifestWithBicep(GetWebsite(publicApp.Resource, publicEnvironment.Resource));
        await Verify(new { frontDoorBicep, publicWebsiteBicep });
    }

    [Fact]
    public async Task WebsitesWithoutOriginsAreAlsoPrivate()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var environment = builder.AddAzureAppServiceEnvironment("env").WithDashboard(false);
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        environment.WithAzureFrontDoorPrivateLink(frontDoor);
        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();
        var other = builder.AddProject<Project>("other", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();
        frontDoor.WithOrigin(api);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (_, bicep) = await GetManifestWithBicep(GetWebsite(other.Resource, environment.Resource));
        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task UnconnectedFrontDoorCannotReachPrivateWebsites()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var environment = builder.AddAzureAppServiceEnvironment("env");
        var connected = builder.AddAzureFrontDoor("connected");
        var unconnected = builder.AddAzureFrontDoor("unconnected");
        environment.WithAzureFrontDoorPrivateLink(connected);
        var api = builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();
        connected.WithOrigin(api);
        unconnected.WithOrigin(api);

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));
        Assert.Contains("Origin 'api' of Azure Front Door 'unconnected'", exception.ToString());
    }

    [Fact]
    public async Task UnusedConnectionIsRejected()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var environment = builder.AddAzureAppServiceEnvironment("env");
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        environment.WithAzureFrontDoorPrivateLink(frontDoor);

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));
        Assert.Contains("none of its origins run in that environment", exception.ToString());
    }

    [Fact]
    public void RunModeIsUnchanged()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run, testOutputHelper);
        var environment = builder.AddAzureAppServiceEnvironment("env").WithDeploymentSlot("staging");
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");

        Assert.Same(environment, environment.WithAzureFrontDoorPrivateLink(frontDoor));
        Assert.Empty(environment.Resource.Annotations.OfType<PrivateEndpointTargetAnnotation>());
        Assert.Empty(frontDoor.Resource.Annotations.OfType<AzureFrontDoorPrivateLinkAnnotation>());
    }

    private static AzureAppServiceWebSiteResource GetWebsite(IResource resource, AzureAppServiceEnvironmentResource environment) =>
        Assert.IsType<AzureAppServiceWebSiteResource>(resource.GetDeploymentTargetAnnotation(environment)?.DeploymentTarget);

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }
}
