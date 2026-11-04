// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Azure.Provisioning.WebPubSub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.Tests;

public class AzureWebPubSubEmulatorTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(10001)]
    [InlineData(18080)]
    public async Task RunAsEmulatorProvidesLocalConnectionStrings(int port)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var webPubSub = builder.AddAzureWebPubSub("webpubsub").RunAsEmulator(emulator =>
            emulator.WithEndpoint("emulator", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", port)));
        var hub = webPubSub.AddHub("chat-resource", "chat");
        var consumer = builder.AddExecutable("consumer", "unused", ".")
            .WithReference(webPubSub)
            .WithReference(hub);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, serviceProvider: TestServiceProvider.Instance);
        var expectedConnectionString = $"Endpoint=http://localhost:{port};AccessKey=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGH;Version=1.0";
        Assert.Equal(expectedConnectionString, env["ConnectionStrings__webpubsub"]);
        Assert.Equal($"{expectedConnectionString};Hub=chat", env["ConnectionStrings__chat-resource"]);
        Assert.Equal($"http://localhost:{port}", await webPubSub.Resource.UriExpression.GetValueAsync(CancellationToken.None));

        var properties = ((IResourceWithConnectionString)webPubSub.Resource).GetConnectionProperties().ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal($"http://localhost:{port}", await properties["Uri"].GetValueAsync(CancellationToken.None));

        var image = Assert.Single(webPubSub.Resource.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("mcr.microsoft.com/webpubsub/emulator:1.0.0-beta.2", $"{image.Registry}/{image.Image}:{image.Tag}");
        Assert.Equal(8080, webPubSub.GetEndpoint("emulator").TargetPort);
    }

    [Fact]
    public async Task RunAsEmulatorConfiguresEventHandlersRegardlessOfCallOrder()
    {
        var emulatorFirst = await CreateEventHandlerConfigurationAsync(emulatorFirst: true);
        var emulatorLast = await CreateEventHandlerConfigurationAsync(emulatorFirst: false);
        Assert.Equal(emulatorFirst, emulatorLast);
        await VerifyJson(emulatorFirst);
    }

    private async Task<string> CreateEventHandlerConfigurationAsync(bool emulatorFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var webPubSub = builder.AddAzureWebPubSub("webpubsub");
        var api = builder.AddExecutable("api", "unused", ".").WithHttpEndpoint();
        var endpoint = api.GetEndpoint("http").EndpointAnnotation;
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 5000);
        endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(
            KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
            new AllocatedEndpoint(endpoint, "host.docker.internal", 5000, EndpointBindingMode.SingleAddress,
                targetPortExpression: null, networkId: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));

        if (emulatorFirst)
        {
            webPubSub.RunAsEmulator();
        }

        webPubSub.AddHub("chat-resource", "chat")
            .AddEventHandler($"{api.GetEndpoint("http")}/events/{{hub}}/{{event}}", systemEvents: ["connect", "connected", "disconnected"])
            .AddEventHandler($"http://example.com/events", userEventPattern: "message");
        webPubSub.AddHub("notifications").AddEventHandler($"http://example.com/notifications");
        webPubSub.AddHub("special-hub", "chat__room").AddEventHandler($"http://example.com/special");

        if (!emulatorFirst)
        {
            webPubSub.RunAsEmulator();
        }

        var dependencies = await webPubSub.Resource.GetResourceDependenciesAsync(
            builder.ExecutionContext, ResourceDependencyDiscoveryMode.DirectOnly);
        Assert.Contains(api.Resource, dependencies);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(webPubSub.Resource, serviceProvider: TestServiceProvider.Instance);
        Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGH", env["WebPubSub__AccessKey"]);
        return await GetEmulatorConfigurationAsync(webPubSub.Resource);
    }

    [Fact]
    public async Task RunAsEmulatorRejectsUnsupportedEventHandlerAuthentication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var webPubSub = builder.AddAzureWebPubSub("webpubsub").RunAsEmulator();
        webPubSub.AddHub("chat").AddEventHandler($"http://example.com/events", authSettings: new UpstreamAuthSettings());

        var exception = await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await GetEmulatorConfigurationAsync(webPubSub.Resource));

        Assert.Contains("does not support event handler authentication for hub 'chat'", exception.Message);
    }

    [Fact]
    public async Task RunAsEmulatorPreservesAzureManifestWhenPublishing()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var webPubSub = builder.AddAzureWebPubSub("webpubsub");
        var hub = webPubSub.AddHub("chat").AddEventHandler($"https://example.com/events", systemEvents: ["connected"]);
        var original = await AzureManifestUtils.GetManifestWithBicep(webPubSub.Resource);

        webPubSub.RunAsEmulator();

        var manifest = await AzureManifestUtils.GetManifestWithBicep(webPubSub.Resource);
        Assert.Equal(original.ManifestNode.ToString(), manifest.ManifestNode.ToString());
        Assert.Equal(original.BicepText, manifest.BicepText);
        Assert.Equal("Endpoint={webpubsub.outputs.endpoint};Hub=chat", hub.Resource.ConnectionStringExpression.ValueExpression);
        Assert.Empty(webPubSub.Resource.Annotations.OfType<HealthCheckAnnotation>());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, HealthStatus.Healthy)]
    [InlineData(HttpStatusCode.ServiceUnavailable, HealthStatus.Unhealthy)]
    public async Task RunAsEmulatorChecksHealthWithGet(HttpStatusCode statusCode, HealthStatus expectedStatus)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var webPubSub = builder.AddAzureWebPubSub("webpubsub").RunAsEmulator(emulator =>
            emulator.WithEndpoint("emulator", endpoint => endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 18080)));
        var healthCheckKey = Assert.Single(webPubSub.Resource.Annotations.OfType<HealthCheckAnnotation>()).Key;
        builder.Services.AddHttpClient(healthCheckKey).ConfigurePrimaryHttpMessageHandler(() => new EmulatorHealthHandler(statusCode));
        using var app = builder.Build();

        var health = await app.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(expectedStatus, health.Entries[healthCheckKey].Status);
    }

    [Fact]
    public async Task RunAsEmulatorHealthWaitsForEndpointAllocation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var webPubSub = builder.AddAzureWebPubSub("webpubsub").RunAsEmulator();
        var healthCheckKey = Assert.Single(webPubSub.Resource.Annotations.OfType<HealthCheckAnnotation>()).Key;
        builder.Services.AddHttpClient(healthCheckKey).ConfigurePrimaryHttpMessageHandler(() => new EmulatorHealthHandler(HttpStatusCode.OK));
        using var app = builder.Build();
        var registration = Assert.Single(app.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations,
            registration => registration.Name == healthCheckKey);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var healthTask = registration.Factory(app.Services).CheckHealthAsync(new HealthCheckContext { Registration = registration }, cts.Token);
        Assert.False(healthTask.IsCompleted);

        var endpoint = webPubSub.GetEndpoint("emulator").EndpointAnnotation;
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 18080);

        var health = await healthTask;
        Assert.Equal(HealthStatus.Healthy, health.Status);
    }

    private sealed class EmulatorHealthHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var matches = request.Method == HttpMethod.Get && request.RequestUri == new Uri("http://localhost:18080/api/health");
            return Task.FromResult(new HttpResponseMessage(matches ? statusCode : HttpStatusCode.MethodNotAllowed));
        }
    }

    private static async Task<string> GetEmulatorConfigurationAsync(AzureWebPubSubResource resource)
    {
        var annotation = Assert.Single(resource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>());
        Assert.Equal("/app", annotation.DestinationPath);
        var files = await annotation.Callback(new ContainerFileSystemCallbackContext
        {
            Model = resource,
            Services = TestServiceProvider.Instance
        }, CancellationToken.None);
        var file = Assert.IsType<ContainerFile>(Assert.Single(files));
        Assert.Equal("appsettings.json", file.Name);
        return Assert.IsType<string>(file.Contents);
    }
}
