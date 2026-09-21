// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Azure.Provisioning.WebPubSub;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureWebPubSubExtensionsTests(ITestOutputHelper output)
{
    [Fact]
    public void InvalidWebPubSubHubNameThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var wps = builder.AddAzureWebPubSub("wps1");
        var ex = Assert.Throws<ArgumentException>(() => wps.AddHub("a_b_c"));
        Assert.StartsWith("Resource name 'a_b_c' is invalid.", ex.Message);
    }

    [Fact]
    public async Task AddWebPubSubHubNameWithSpecialChars()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);
        WebPubSubHub? realHub = null;
        var wps = builder.AddAzureWebPubSub("wps1").ConfigureInfrastructure(infrastructure =>
        {
            realHub = infrastructure.GetProvisionableResources().OfType<WebPubSubHub>().Single();
        });
        var hubName = "a-b-c";
        var hub = wps.AddHub(hubName);

        Assert.Equal(hubName, hub.Resource.Name);
        var manifest = await AzureManifestUtils.GetManifestWithBicep(wps.Resource);
        Assert.NotNull(realHub);
        Assert.Equal(hubName, realHub.Name.Value);
        Assert.Equal("a_b_c", realHub.BicepIdentifier);
    }

    [Fact]
    public async Task AddAzureWebPubSubHubWorks()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var wps = builder.AddAzureWebPubSub("wps1");
        wps.AddHub("abc");

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{wps1.outputs.endpoint}",
              "path": "wps1.module.bicep"
            }
            """;

        var manifest = await AzureManifestUtils.GetManifestWithBicep(wps.Resource);
        var manifestString = manifest.ManifestNode.ToString();
        output.WriteLine(manifestString);
        output.WriteLine(manifest.BicepText);

        Assert.Equal(expectedManifest, manifestString);
        Assert.Equal("wps1", wps.Resource.Name);
        await Verify(manifest.BicepText, extension: "bicep");
            
    }

    [Fact]
    public void AddAzureWebPubSub_HasCorrectConnectionExpressions()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var wps = builder.AddAzureWebPubSub("wps1");
        var hub = wps.AddHub("abc");
        var otherHub = wps.AddHub("def", "hij");

        Assert.Equal("{wps1.outputs.endpoint}", wps.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("Endpoint={wps1.outputs.endpoint};Hub=abc", hub.Resource.ConnectionStringExpression.ValueExpression);
        // Uses hub name instead of resource name since it was explicitly provided
        Assert.Equal("Endpoint={wps1.outputs.endpoint};Hub=hij", otherHub.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public async Task AddWebPubSubWithHubConfigure()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var hubName = "abc";
        var wps = builder.AddAzureWebPubSub("wps1").ConfigureInfrastructure(infrastructure =>
        {
            var hub = infrastructure.GetProvisionableResources().OfType<WebPubSubHub>().First(i => i.BicepIdentifier == hubName);
            hub.Properties.AnonymousConnectPolicy = "allow";
        });
        wps.AddHub(hubName);

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{wps1.outputs.endpoint}",
              "path": "wps1.module.bicep"
            }
            """;

        var manifest = await AzureManifestUtils.GetManifestWithBicep(wps.Resource);
        var manifestString = manifest.ManifestNode.ToString();
        output.WriteLine(manifestString);
        output.WriteLine(manifest.BicepText);

        Assert.Equal(expectedManifest, manifestString);
        Assert.Equal("wps1", wps.Resource.Name);
        await Verify(manifest.BicepText, extension: "bicep");
            
    }

    [Fact]
    public async Task AddAzureWebPubSubHubWithEventHandlerExpressionWorks()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var serviceA = builder.AddProject<ProjectA>("serviceA", o => o.ExcludeLaunchProfile = true).WithHttpsEndpoint();
        var wps = builder.AddAzureWebPubSub("wps1");
        wps.AddHub("abc").AddEventHandler($"{serviceA.GetEndpoint("https")}/eventhandler/");

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{wps1.outputs.endpoint}",
              "path": "wps1.module.bicep",
              "params": {
                "abc_url_0": "{serviceA.bindings.https.url}/eventhandler/"
              }
            }
            """;

        var manifest = await AzureManifestUtils.GetManifestWithBicep(wps.Resource);
        var manifestString = manifest.ManifestNode.ToString();
        output.WriteLine(manifestString);
        output.WriteLine(manifest.BicepText);

        Assert.Equal(expectedManifest, manifestString);
        Assert.Equal("wps1", wps.Resource.Name);
        await Verify(manifest.BicepText, extension: "bicep");
            
    }

    [Fact]
    public async Task ConfigureConstructOverridesAddEventHandler()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        var serviceA = builder.AddProject<ProjectA>("serviceA", o => o.ExcludeLaunchProfile = true).WithHttpsEndpoint();
        var wps = builder.AddAzureWebPubSub("wps1").ConfigureInfrastructure(infrastructure =>
        {
            var hub = infrastructure.GetProvisionableResources().OfType<WebPubSubHub>().First(i => string.Equals(i.BicepIdentifier, "abc", StringComparison.OrdinalIgnoreCase));
            hub.Properties.EventHandlers.Add(new WebPubSubEventHandler() { UrlTemplate = "http://fake.com" });
        });
        wps.AddHub("ABC").AddEventHandler($"http://fake1.com");
        // Hub name is case insensitive
        wps.AddHub("abc");

        wps.Resource.Outputs["endpoint"] = "https://mywebpubsubendpoint";
        var manifest = await AzureManifestUtils.GetManifestWithBicep(wps.Resource);
        var manifestString = manifest.ManifestNode.ToString();
        output.WriteLine(manifestString);
        output.WriteLine(manifest.BicepText);

        Assert.Equal("wps1", wps.Resource.Name);
        await Verify(manifest.BicepText, extension: "bicep");
            
    }

    [Fact]
    public async Task AddAzureWebPubSubHubSettings()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        var serviceA = builder.AddProject<ProjectA>("serviceA", o => o.ExcludeLaunchProfile = true).WithHttpsEndpoint();
        var url1 = "fake3.com";
        var wps = builder.AddAzureWebPubSub("wps1").ConfigureInfrastructure(infrastructure =>
        {
            var hub = infrastructure.GetProvisionableResources().OfType<WebPubSubHub>().First(i => i.BicepIdentifier == "hub1");
            hub.Properties.AnonymousConnectPolicy = "allow";
            // allow directly event handler set
            hub.Properties.EventHandlers.Add(new WebPubSubEventHandler() { UrlTemplate = "http://fake1.com" });
        });
        // allow event handler set using a separate call
        // allow mulitple calls, and order matters
        wps.AddHub("hub1")
            .AddEventHandler($"http://fake2.com", "event1", authSettings: new UpstreamAuthSettings())
            .AddEventHandler($"http://{url1}", systemEvents: ["connect"], authSettings: new UpstreamAuthSettings { AuthType = UpstreamAuthType.ManagedIdentity, ManagedIdentityResource = "abc" });
        wps.AddHub("hub2")
            .AddEventHandler($"{serviceA.GetEndpoint("https")}/hub/eventhandler1")
            .AddEventHandler($"{serviceA.GetEndpoint("https")}/eventhandler2")
            .AddEventHandler($"{serviceA.GetEndpoint("https")}/eventhandler3", "event1", ["connect", "connected"]);

        wps.Resource.Outputs["endpoint"] = "https://mywebpubsubendpoint";

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{wps1.outputs.endpoint}",
              "path": "wps1.module.bicep",
              "params": {
                "hub2_url_0": "{serviceA.bindings.https.url}/hub/eventhandler1",
                "hub2_url_1": "{serviceA.bindings.https.url}/eventhandler2",
                "hub2_url_2": "{serviceA.bindings.https.url}/eventhandler3"
              }
            }
            """;

        var connectionStringResource = (IResourceWithConnectionString)wps.Resource;

        Assert.Equal("https://mywebpubsubendpoint", await connectionStringResource.GetConnectionStringAsync());
        var manifest = await AzureManifestUtils.GetManifestWithBicep(wps.Resource);
        var manifestString = manifest.ManifestNode.ToString();
        output.WriteLine(manifestString);
        output.WriteLine(manifest.BicepText);

        Assert.Equal(expectedManifest, manifestString);

        Assert.Equal("wps1", wps.Resource.Name);
        await Verify(manifest.BicepText, extension: "bicep");
            
    }

    [Fact]
    public void AddHub_WithDifferentNameAndHubName_SetsPropertiesCorrectly()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var wps = builder.AddAzureWebPubSub("wps1");

        var hub1 = wps.AddHub("hub1");
        var hub2 = wps.AddHub("resource2", "hub2");
        var hub3 = wps.AddHub("hub3", "hub3");
        var hub4 = wps.AddHub("hub4", null);

        Assert.Equal("hub1", hub1.Resource.Name);
        Assert.Equal("resource2", hub2.Resource.Name);
        Assert.Equal("hub3", hub3.Resource.Name);
        Assert.Equal("hub4", hub4.Resource.Name);

        Assert.Equal("hub1", hub1.Resource.HubName);
        Assert.Equal("hub2", hub2.Resource.HubName);
        Assert.Equal("hub3", hub3.Resource.HubName);
        Assert.Equal("hub4", hub4.Resource.HubName);

        Assert.Equal("Endpoint={wps1.outputs.endpoint};Hub=hub1", hub1.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("Endpoint={wps1.outputs.endpoint};Hub=hub2", hub2.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("Endpoint={wps1.outputs.endpoint};Hub=hub3", hub3.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Equal("Endpoint={wps1.outputs.endpoint};Hub=hub4", hub4.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void AddHub_CalledTwiceWithSameHubName_ReturnsSameResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var wps = builder.AddAzureWebPubSub("wps1");

        // Call AddHub twice with the same hub name but different resource names
        var hub1 = wps.AddHub("resource1", "same-hub");
        var hub2 = wps.AddHub("resource2", "same-hub");

        // Verify both calls return the same hub resource (only the first one is registered)
        Assert.Same(hub1.Resource, hub2.Resource);
        Assert.Equal("resource1", hub1.Resource.Name);
        Assert.Equal("same-hub", hub1.Resource.HubName);
    }

    [Fact]
    public async Task AddDefaultAzureWebPubSub()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var wps = builder.AddAzureWebPubSub("wps1");

        wps.Resource.Outputs["endpoint"] = "https://mywebpubsubendpoint";

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{wps1.outputs.endpoint}",
              "path": "wps1.module.bicep"
            }
            """;

        var connectionStringResource = (IResourceWithConnectionString)wps.Resource;

        Assert.Equal("https://mywebpubsubendpoint", await connectionStringResource.GetConnectionStringAsync());

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var manifest = await GetManifestWithBicep(model, wps.Resource);

        Assert.Equal(expectedManifest, manifest.ManifestNode.ToString());

        Assert.Equal("wps1", wps.Resource.Name);
        await Verify(manifest.BicepText, extension: "bicep");

        var wpsRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "wps1-roles");
        var wpsRolesManifest = await GetManifestWithBicep(wpsRoles, skipPreparer: true);
        var expectedBicep = """
            @description('The location for the resource(s) to be deployed.')
            param location string = resourceGroup().location

            param wps1_outputs_name string

            param principalType string

            param principalId string

            resource wps1 'Microsoft.SignalRService/webPubSub@2024-03-01' existing = {
              name: wps1_outputs_name
            }

            resource wps1_WebPubSubServiceOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
              name: guid(wps1.id, principalId, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12cf5a90-567b-43ae-8102-96cf46c7d9b4'))
              properties: {
                principalId: principalId
                roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12cf5a90-567b-43ae-8102-96cf46c7d9b4')
                principalType: principalType
              }
              scope: wps1
            }
            """;
        Assert.Equal(expectedBicep, wpsRolesManifest.BicepText);
    }

    [Fact]
    public async Task AddAzureWebPubSubWithParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var wps = builder.AddAzureWebPubSub("wps1")
        .WithParameter("sku", "Standard_S1")
        .WithParameter("capacity", 2);

        wps.Resource.Outputs["endpoint"] = "https://mywebpubsubendpoint";

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{wps1.outputs.endpoint}",
              "path": "wps1.module.bicep",
              "params": {
                "sku": "Standard_S1",
                "capacity": 2
              }
            }
            """;
        var manifest = await AzureManifestUtils.GetManifestWithBicep(wps.Resource);
        Assert.Equal(expectedManifest, manifest.ManifestNode.ToString());

        Assert.Equal("wps1", wps.Resource.Name);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureWebPubSubResource()
    {
        // Arrange
        var webPubSubResource = new AzureWebPubSubResource("test-webpubsub", _ => { });
        var infrastructure = new AzureResourceInfrastructure(webPubSubResource, "test-webpubsub");

        // Act - Call AddAsExistingResource twice
        var firstResult = webPubSubResource.AddAsExistingResource(infrastructure);
        var secondResult = webPubSubResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureWebPubSubResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var existingName = builder.AddParameter("existing-webpubsub-name");
        var existingResourceGroup = builder.AddParameter("existing-webpubsub-rg");

        var webPubSub = builder.AddAzureWebPubSub("test-webpubsub")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = webPubSub.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }

    private sealed class ProjectA : IProjectMetadata
    {
        public string ProjectPath => "projectA";
    }
}
