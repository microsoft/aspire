// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureSignalRExtensionsTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task AddAzureSignalR()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var signalr = builder.AddAzureSignalR("signalr");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await GetManifestWithBicep(signalr.Resource, skipPreparer: true);
        var signalrRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "signalr-roles");
        var (signalrRolesManifest, signalrRolesBicep) = await GetManifestWithBicep(signalrRoles, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep")
              .AppendContentAsFile(signalrRolesManifest.ToString(), "json")
              .AppendContentAsFile(signalrRolesBicep, "bicep");
              
    }

    [Fact]
    public async Task AddServerlessAzureSignalR()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var signalr = builder.AddAzureSignalR("signalr", AzureSignalRServiceMode.Serverless);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, default);

        var (manifest, bicep) = await GetManifestWithBicep(signalr.Resource, skipPreparer: true);
        var signalrRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "signalr-roles");
        var (signalrRolesManifest, signalrRolesBicep) = await GetManifestWithBicep(signalrRoles, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep")
              .AppendContentAsFile(signalrRolesManifest.ToString(), "json")
              .AppendContentAsFile(signalrRolesBicep, "bicep");
              
    }

    [Fact]
    public void RunAsEmulatorAppliesEmulatorResourceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var signalR = builder.AddAzureSignalR("signalr")
                            .RunAsEmulator();

        // Verify that the EmulatorResourceAnnotation is applied
        Assert.True(signalR.Resource.IsEmulator());
        Assert.Contains(signalR.Resource.Annotations, a => a is EmulatorResourceAnnotation);
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureSignalRResource()
    {
        // Arrange
        var signalRResource = new AzureSignalRResource("test-signalr", _ => { });
        var infrastructure = new AzureResourceInfrastructure(signalRResource, "test-signalr");

        // Act - Call AddAsExistingResource twice
        var firstResult = signalRResource.AddAsExistingResource(infrastructure);
        var secondResult = signalRResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureSignalRResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var existingName = builder.AddParameter("existing-signalr-name");
        var existingResourceGroup = builder.AddParameter("existing-signalr-rg");

        var signalR = builder.AddAzureSignalR("test-signalr")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = signalR.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }
}
