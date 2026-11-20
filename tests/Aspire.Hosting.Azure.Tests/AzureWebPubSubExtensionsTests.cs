// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CS0618 // Retain legacy ProjectResource Azure publishing/provisioning contracts with synthetic metadata.
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Azure.Provisioning.WebPubSub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task EventHandlerEndpointHubIsProvisionedAfterComputeInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddAzureContainerAppEnvironment("env");
        var serviceA = builder.AddProject<ProjectA>("serviceA", o => o.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(environment);
        var wps = builder.AddAzureWebPubSub("wps1");
        var hub = wps.AddHub("abc").AddEventHandler($"{serviceA.GetEndpoint("https")}/eventhandler/{{event}}");
        wps.AddHub("literal").AddEventHandler($"https://example.com/eventhandler/{{event}}");
        serviceA.WithReference(hub);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var handlers = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "abc-handlers");
        var containerApp = (AzureProvisioningResource)serviceA.Resource.GetDeploymentTargetAnnotation()!.DeploymentTarget;

        var (wpsManifest, wpsBicep) = await AzureManifestUtils.GetManifestWithBicep(wps.Resource, skipPreparer: true);
        var (handlersManifest, handlersBicep) = await AzureManifestUtils.GetManifestWithBicep(handlers, skipPreparer: true);
        var (_, containerAppBicep) = await AzureManifestUtils.GetManifestWithBicep(containerApp, skipPreparer: true);

        // The app's deployed URL, not a prediction, and a dependency on both the service and the app.
        Assert.Equal("{wps1.outputs.name}", handlersManifest["params"]!["wps1_outputs_name"]!.ToString());
        Assert.Equal("{serviceA-containerapp.outputs.https_url}/eventhandler/{event}", handlersManifest["params"]!["abc_url_0"]!.ToString());
        Assert.Equal([containerApp, wps.Resource], handlers.GetAzureReferences().OrderBy(r => r.Name, StringComparer.Ordinal));

        await Verify(wpsBicep, "bicep")
            .AppendContentAsFile(handlersBicep, "bicep")
            .AppendContentAsFile(containerAppBicep, "bicep");
    }

    [Theory]
    [InlineData("aca", "serviceA-containerapp")]
    [InlineData("appservice", "serviceA-website")]
    public async Task EventHandlerHubModuleProvisioningDependsOnReferencedApp(string computeEnvironment, string deploymentTarget)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "inspect");
        if (computeEnvironment == "aca")
        {
            builder.AddAzureContainerAppEnvironment("env");
        }
        else
        {
            builder.AddAzureAppServiceEnvironment("env");
        }

        var serviceA = builder.AddProject<ProjectA>("serviceA", o => o.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var hub = builder.AddAzureWebPubSub("wps1").AddHub("abc").AddEventHandler($"{serviceA.GetEndpoint("https")}/eventhandler/{{event}}");
        serviceA.WithReference(hub);

        IReadOnlyList<PipelineStep> steps = [];
        builder.Pipeline.AddStep("inspect", _ => Task.CompletedTask);
        builder.Pipeline.AddPipelineConfiguration(context =>
        {
            steps = context.Steps.ToArray();
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        await app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(
            new PipelineContext(app.Services.GetRequiredService<DistributedApplicationModel>(),
                app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
                NullLogger.Instance, TestContext.Current.CancellationToken));

        // The module's Bicep is cached before deployment targets are prepared, so the dependency on the
        // app must not rely on parameters discovered from that template.
        var handlersStep = steps.Single(step => step.Name == "provision-abc-handlers");
        Assert.Contains($"provision-{deploymentTarget}", handlersStep.DependsOnSteps);
        Assert.Contains("provision-wps1", handlersStep.DependsOnSteps);
    }

    [Fact]
    public async Task EventHandlerUsesAppServiceDeployedUrl()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureAppServiceEnvironment("env");
        var serviceA = builder.AddProject<ProjectA>("serviceA", o => o.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var wps = builder.AddAzureWebPubSub("wps1");
        var hub = wps.AddHub("abc").AddEventHandler($"{serviceA.GetEndpoint("https")}/callbacks/{{hub}}/{{event}}?source=webpubsub");
        serviceA.WithReference(hub);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var handlers = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "abc-handlers");
        var website = (AzureProvisioningResource)serviceA.Resource.GetDeploymentTargetAnnotation()!.DeploymentTarget;
        var (_, websiteBicep) = await AzureManifestUtils.GetManifestWithBicep(website, skipPreparer: true);
        var (manifest, handlersBicep) = await AzureManifestUtils.GetManifestWithBicep(handlers, skipPreparer: true);

        Assert.Equal("{serviceA-website.outputs.https_url}/callbacks/{hub}/{event}?source=webpubsub", manifest["params"]!["abc_url_0"]!.ToString());
        Assert.Equal([website, wps.Resource], handlers.GetAzureReferences().OrderBy(r => r.Name, StringComparer.Ordinal));
        await Verify(websiteBicep, "bicep").AppendContentAsFile(handlersBicep, "bicep");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EventHandlerCanDependOnFrontDoorOutput(bool explicitUrl, bool configureFrontDoorFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");
        var serviceA = builder.AddProject<ProjectA>("serviceA", o => o.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var frontDoor = builder.AddAzureFrontDoor("frontdoor");
        if (configureFrontDoorFirst)
        {
            frontDoor.WithOrigin(serviceA);
        }

        var wps = builder.AddAzureWebPubSub("wps1");
        var url = explicitUrl
            ? ReferenceExpression.Create($"{frontDoor.Resource.GetEndpointUrl(serviceA.Resource.Name)}/callbacks/{{event}}?source=wps")
            : ReferenceExpression.Create($"{serviceA.GetEndpoint("https")}/callbacks/{{event}}?source=wps");
        var hub = wps.AddHub("abc").AddEventHandler(url);
        serviceA.WithReference(hub);
        if (!configureFrontDoorFirst)
        {
            frontDoor.WithOrigin(serviceA);
        }

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var handlers = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "abc-handlers");
        var (manifest, _) = await AzureManifestUtils.GetManifestWithBicep(handlers, skipPreparer: true);
        Assert.Equal("{frontdoor.outputs.serviceA_endpointUrl}/callbacks/{event}?source=wps", manifest["params"]!["abc_url_0"]!.ToString());
        Assert.Equal([frontDoor.Resource, wps.Resource], handlers.GetAzureReferences().OrderBy(r => r.Name, StringComparer.Ordinal));

        // Front Door must still use the backend address, not resolve its origin through itself.
        Assert.All(frontDoor.Resource.GetAzureReferences(), reference => Assert.NotSame(frontDoor.Resource, reference));
        var containerApp = (AzureProvisioningResource)serviceA.Resource.GetDeploymentTargetAnnotation()!.DeploymentTarget;
        Assert.Equal([wps.Resource], containerApp.GetAzureReferences().Where(r => r == wps.Resource || r == handlers || r == frontDoor.Resource));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleIngressResourcesRequireExplicitCallbackUrl(bool explicitUrl)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");
        var serviceA = builder.AddProject<ProjectA>("serviceA", o => o.ExcludeLaunchProfile = true)
            .WithHttpsEndpoint()
            .WithExternalHttpEndpoints();
        var first = builder.AddAzureFrontDoor("first").WithOrigin(serviceA);
        builder.AddAzureFrontDoor("second").WithOrigin(serviceA);
        var wps = builder.AddAzureWebPubSub("wps1");
        var url = explicitUrl
            ? ReferenceExpression.Create($"{first.Resource.GetEndpointUrl(serviceA.Resource.Name)}/callbacks")
            : ReferenceExpression.Create($"{serviceA.GetEndpoint("https")}/callbacks");
        var hub = wps.AddHub("abc").AddEventHandler(url);
        serviceA.WithReference(hub);

        using var app = builder.Build();
        if (!explicitUrl)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ExecuteBeforeStartHooksAsync(app, default));
            Assert.Contains("Configure the callback with an explicit URL", exception.ToString(), StringComparison.Ordinal);
            return;
        }

        await ExecuteBeforeStartHooksAsync(app, default);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var handlers = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "abc-handlers");
        Assert.Equal([first.Resource, wps.Resource], handlers.GetAzureReferences().OrderBy(r => r.Name, StringComparer.Ordinal));
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
    public async Task AddEventHandlerWithLiteralUrlTemplateKeepsPlaceholders()
    {
        // A URL template without references is written to Bicep as a literal. Its {hub} and {event}
        // placeholders must keep single braces, whether the expression comes from ReferenceExpression.Create
        // or from ReferenceExpressionBuilder, which polyglot app hosts use.
        using var builder = TestDistributedApplicationBuilder.Create(output);

        var urlBuilder = new ReferenceExpressionBuilder();
        urlBuilder.AppendLiteral("http://fake2.com/events/{hub}/{event}");

        var wps = builder.AddAzureWebPubSub("wps1");
        wps.AddHub("hub1")
            .AddEventHandler($"http://fake1.com/events/{{hub}}/{{event}}")
            .AddEventHandler(urlBuilder.Build());

        wps.Resource.Outputs["endpoint"] = "https://mywebpubsubendpoint";
        var manifest = await AzureManifestUtils.GetManifestWithBicep(wps.Resource);
        output.WriteLine(manifest.BicepText);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddEventHandlerWithConditionalUrlUsesParameter()
    {
        // A conditional expression has no manifest expressions either, but it is not a literal:
        // its URL must be passed as a parameter, not written as a URL template.
        using var builder = TestDistributedApplicationBuilder.Create(output);

        var useProduction = builder.AddParameter("use-production");
        var url = ReferenceExpression.CreateConditional(
            useProduction.Resource,
            bool.TrueString,
            ReferenceExpression.Create($"https://prod.example.com/events"),
            ReferenceExpression.Create($"https://test.example.com/events"));

        var wps = builder.AddAzureWebPubSub("wps1");
        wps.AddHub("hub1").AddEventHandler(url);

        wps.Resource.Outputs["endpoint"] = "https://mywebpubsubendpoint";
        var manifest = await AzureManifestUtils.GetManifestWithBicep(wps.Resource);
        output.WriteLine(manifest.BicepText);

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

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);

    private sealed class ProjectA : IProjectMetadata
    {
        public string ProjectPath => "projectA";
    }
}
#pragma warning restore CS0618
