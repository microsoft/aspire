// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "6")]
public class WithHttpCommandTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void WithHttpCommand_AddsHttpClientFactory()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();

        // Act
        var resourceBuilder = builder.AddContainer("name", "image")
            .WithHttpEndpoint()
            .WithHttpCommand("/some-path", "Do The Thing");

        using var app = builder.Build();

        // Assert
        var httpClientFactoryServiceDescriptor = builder.Services.FirstOrDefault(sd => sd.ServiceType == typeof(IHttpClientFactory));
        Assert.NotNull(httpClientFactoryServiceDescriptor);

        var httpClientFactory = app.Services.GetService<IHttpClientFactory>();
        Assert.NotNull(httpClientFactory);
    }

    [Fact]
    public void WithHttpCommand_Throws_WhenEndpointByNameIsNotHttp()
    {
        // Arrange
        using var builder = CreateTestDistributedApplicationBuilder();

        var container = builder.AddContainer("name", "image")
                .WithEndpoint(targetPort: 9999, scheme: "tcp", name: "nonhttp");

        // Act
        var ex = Assert.Throws<DistributedApplicationException>(() =>
        {
            container.WithHttpCommand("/some-path", "Do The Thing", endpointName: "nonhttp");
        });

        // Assert
        Assert.Equal(
            "Could not create HTTP command for resource 'name' as the endpoint with name 'nonhttp' and scheme 'tcp' is not an HTTP endpoint.",
            ex.Message
        );
    }

    [Fact]
    public void WithHttpCommand_Throws_WhenEndpointIsNotHttp()
    {
        // Arrange
        using var builder = CreateTestDistributedApplicationBuilder();

        var container = builder.AddContainer("name", "image")
                .WithEndpoint(targetPort: 9999, scheme: "tcp", name: "nonhttp");

        // Act
        var ex = Assert.Throws<DistributedApplicationException>(() =>
        {
            container.WithHttpCommand("/some-path", "Do The Thing", () => container.GetEndpoint("nonhttp"));
        });

        // Assert
        Assert.Equal(
            "Could not create HTTP command for resource 'name' as the endpoint with name 'nonhttp' and scheme 'tcp' is not an HTTP endpoint.",
            ex.Message
        );
    }

    [Fact]
    public void WithHttpCommand_AddsResourceCommandAnnotation_WithDefaultValues()
    {
        // Arrange
        using var builder = CreateTestDistributedApplicationBuilder();
        var resourceBuilder = builder.AddContainer("name", "image")
            .WithHttpEndpoint()
            .WithHttpCommand("/some-path", "Do The Thing");

        // Act
        var command = resourceBuilder.Resource.Annotations.OfType<ResourceCommandAnnotation>().FirstOrDefault();

        // Assert
        Assert.NotNull(command);
        Assert.Equal("Do The Thing", command.DisplayName);
        // Expected name format: "{endpoint.Resource.Name}-{endpoint.EndpointName}-http-{httpMethod}"
        Assert.Equal($"{resourceBuilder.Resource.Name}-http-http-post-/some-path", command.Name);
        Assert.Null(command.DisplayDescription);
        Assert.Null(command.ConfirmationMessage);
        Assert.Null(command.IconName);
        Assert.Null(command.IconVariant);
        Assert.False(command.IsHighlighted);
    }

    [Fact]
    public void WithHttpCommand_AddsResourceCommandAnnotation_WithCustomValues()
    {
        // Arrange
        using var builder = CreateTestDistributedApplicationBuilder();
        var resourceBuilder = builder.AddContainer("name", "image")
            .WithHttpEndpoint()
            .WithHttpCommand("/some-path", "Do The Thing",
                commandName: "my-command-name",
                commandOptions: new()
                {
                    Description = "Command description",
                    ConfirmationMessage = "Are you sure?",
                    IconName = "DatabaseLightning",
                    IconVariant = IconVariant.Filled,
                    IsHighlighted = true
                });

        // Act
        var command = resourceBuilder.Resource.Annotations.OfType<ResourceCommandAnnotation>().FirstOrDefault();

        // Assert
        Assert.NotNull(command);
        Assert.Equal("Do The Thing", command.DisplayName);
        Assert.Equal("my-command-name", command.Name);
        Assert.Equal("Command description", command.DisplayDescription);
        Assert.Equal("Are you sure?", command.ConfirmationMessage);
        Assert.Equal("DatabaseLightning", command.IconName);
        Assert.Equal(IconVariant.Filled, command.IconVariant);
        Assert.True(command.IsHighlighted);
    }

    [Fact]
    public void WithHttpCommand_AddsResourceCommandAnnotations_WithUniqueCommandNames()
    {
        // Arrange
        using var builder = CreateTestDistributedApplicationBuilder();
        var resourceBuilder = builder.AddContainer("name", "image")
            .WithHttpEndpoint()
            .WithHttpEndpoint(name: "custom-endpoint")
            .WithHttpCommand("/some-path", "Do The Thing")
            .WithHttpCommand("/some-path", "Do The Thing", endpointName: "custom-endpoint")
            .WithHttpCommand("/some-path", "Do The Get Thing", commandOptions: new() { Method = HttpMethod.Get })
            .WithHttpCommand("/some-path", "Do The Get Thing", endpointName: "custom-endpoint", commandOptions: new() { Method = HttpMethod.Get })
            .WithHttpCommand("/some-other-path", "Do The Other Thing")
            // Call it again but just change display name, it should override the previous one with the same path
            .WithHttpCommand("/some-other-path", "Do The Other Thing CHANGED");

        // Act
        var commands = resourceBuilder.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToList();
        var command1 = commands.FirstOrDefault(c => c.DisplayName == "Do The Thing");
        var command2 = commands.FirstOrDefault(c => c.DisplayName == "Do The Thing" && c.Name.Contains("custom-endpoint"));
        var command3 = commands.FirstOrDefault(c => c.DisplayName == "Do The Get Thing");
        var command4 = commands.FirstOrDefault(c => c.DisplayName == "Do The Get Thing" && c.Name.Contains("custom-endpoint"));
        var command5 = commands.FirstOrDefault(c => c.DisplayName == "Do The Other Thing");
        var command6 = commands.FirstOrDefault(c => c.DisplayName == "Do The Other Thing CHANGED");

        // Assert
        Assert.True(commands.Count >= 5);
        Assert.NotNull(command1);
        Assert.NotNull(command2);
        Assert.NotNull(command3);
        Assert.NotNull(command4);
        Assert.Null(command5); // This one is overridden by the last one
        Assert.NotNull(command6);
    }

    [InlineData(200, true)]
    [InlineData(201, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(500, false)]
    [Theory]
    public async Task WithHttpCommand_ResultsInExpectedResultForStatusCode(int statusCode, bool expectSuccess)
    {
        using var builder = CreateTestDistributedApplicationBuilder();

        var fakeHandler = new FakeHttpMessageHandler((HttpStatusCode)statusCode);
        builder.Services.AddHttpClient("commandclient")
            .ConfigurePrimaryHttpMessageHandler(() => fakeHandler);

        var service = CreateResourceWithAllocatedEndpoint(builder, "service");
        service.WithHttpCommand($"/status/{statusCode}", "Do The Thing", commandName: "mycommand", commandOptions: new() { HttpClientName = "commandclient" });

        // Act
        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, service.Resource);

        var result = await app.ResourceCommands.ExecuteCommandAsync(service.Resource, "mycommand").DefaultTimeout();

        // Assert
        Assert.True(fakeHandler.Called, "Expected the HTTP handler to be called");
        Assert.Equal(expectSuccess, result.Success);
    }

    [InlineData(null, false)] // Default method is POST
    [InlineData("get", true)]
    [InlineData("post", false)]
    [Theory]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/9725")]
    public async Task WithHttpCommand_ResultsInExpectedResultForHttpMethod(string? httpMethod, bool expectSuccess)
    {
        // Arrange
        var method = httpMethod is not null ? new HttpMethod(httpMethod) : HttpCommandOptions.Default.Method;
        using var builder = CreateTestDistributedApplicationBuilder();

        // Return 405 Method Not Allowed for POST, 200 OK for GET
        var fakeHandler = new FakeHttpMessageHandler(expectSuccess ? HttpStatusCode.OK : HttpStatusCode.MethodNotAllowed);
        builder.Services.AddHttpClient("commandclient")
            .ConfigurePrimaryHttpMessageHandler(() => fakeHandler);

        var service = CreateResourceWithAllocatedEndpoint(builder, "service");
        service.WithHttpCommand("/get-only", "Do The Thing", commandName: "mycommand", commandOptions: new() { Method = method, HttpClientName = "commandclient" });

        // Act
        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, service.Resource);

        var result = await app.ResourceCommands.ExecuteCommandAsync(service.Resource, "mycommand").DefaultTimeout();

        // Assert
        Assert.True(fakeHandler.Called, "Expected the HTTP handler to be called");
        Assert.Equal(expectSuccess, result.Success);
    }

    [Fact]
    public async Task WithHttpCommand_UsesNamedHttpClient()
    {
        // Arrange
        using var builder = CreateTestDistributedApplicationBuilder();
        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        builder.Services.AddHttpClient("commandclient")
            .ConfigurePrimaryHttpMessageHandler(() => fakeHandler);

        var service = CreateResourceWithAllocatedEndpoint(builder, "service");
        service.WithHttpCommand("/get-only", "Do The Thing", commandName: "mycommand", commandOptions: new() { HttpClientName = "commandclient" });

        // Act
        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, service.Resource);

        var result = await app.ResourceCommands.ExecuteCommandAsync(service.Resource, "mycommand").DefaultTimeout();

        // Assert
        Assert.True(fakeHandler.Called);
    }

    private sealed class FakeHttpMessageHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public bool Called { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Called = true;
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    [Fact]
    public async Task WithHttpCommand_UsesEndpointSelector()
    {
        // Arrange
        using var builder = CreateTestDistributedApplicationBuilder();

        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        builder.Services.AddHttpClient("commandclient")
            .ConfigurePrimaryHttpMessageHandler(() => fakeHandler);

        var serviceA = CreateResourceWithAllocatedEndpoint(builder, "servicea");
        var callbackCalled = false;
        var serviceB = builder.AddResource(new CustomResource("serviceb"))
            .WithHttpEndpoint(targetPort: 8081)
            .WithHttpCommand("/status/200", "Do The Thing", commandName: "mycommand",
                endpointSelector: () =>
                {
                    callbackCalled = true;
                    return serviceA.GetEndpoint("http");
                },
                commandOptions: new() { HttpClientName = "commandclient" });

        // Act
        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        // Move serviceA to running (which the command's endpoint selector points to)
        await app.ResourceNotifications.PublishUpdateAsync(serviceA.Resource, s => s with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();
        await app.ResourceNotifications.WaitForResourceAsync(serviceA.Resource.Name, KnownResourceStates.Running).DefaultTimeout();

        // Move serviceB to running and wait for command to become enabled
        await MoveResourceToRunningStateAsync(app, serviceB.Resource);

        var result = await app.ResourceCommands.ExecuteCommandAsync(serviceB.Resource, "mycommand").DefaultTimeout();

        // Assert
        Assert.True(callbackCalled);
        Assert.True(fakeHandler.Called);
    }

    [Fact]
    public async Task WithHttpCommand_CallsPrepareRequestCallback_BeforeSendingRequest()
    {
        // Arrange
        var callbackCalled = false;
        using var builder = CreateTestDistributedApplicationBuilder();

        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        builder.Services.AddHttpClient("commandclient")
            .ConfigurePrimaryHttpMessageHandler(() => fakeHandler);

        var service = CreateResourceWithAllocatedEndpoint(builder, "service");
        service.WithHttpCommand("/status/200", "Do The Thing",
            commandName: "mycommand",
            commandOptions: new()
            {
                HttpClientName = "commandclient",
                PrepareRequest = requestContext =>
                {
                    Assert.NotNull(requestContext);
                    Assert.NotNull(requestContext.ServiceProvider);
                    Assert.Equal(service.Resource.Name, requestContext.ResourceName);
                    Assert.NotNull(requestContext.Endpoint);
                    Assert.NotNull(requestContext.HttpClient);
                    Assert.NotNull(requestContext.Request);

                    callbackCalled = true;
                    return Task.CompletedTask;
                }
            });

        // Act
        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, service.Resource);

        var result = await app.ResourceCommands.ExecuteCommandAsync(service.Resource, "mycommand").DefaultTimeout();

        // Assert
        Assert.True(callbackCalled);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task WithHttpCommand_CallsGetResponseCallback_AfterSendingRequest()
    {
        // Arrange
        var callbackCalled = false;
        using var builder = CreateTestDistributedApplicationBuilder();

        var fakeHandler = new FakeHttpMessageHandler(HttpStatusCode.OK);
        builder.Services.AddHttpClient("commandclient")
            .ConfigurePrimaryHttpMessageHandler(() => fakeHandler);

        var service = CreateResourceWithAllocatedEndpoint(builder, "service");
        service.WithHttpCommand("/status/200", "Do The Thing",
            commandName: "mycommand",
            commandOptions: new()
            {
                HttpClientName = "commandclient",
                GetCommandResult = resultContext =>
                {
                    Assert.NotNull(resultContext);
                    Assert.NotNull(resultContext.ServiceProvider);
                    Assert.Equal(service.Resource.Name, resultContext.ResourceName);
                    Assert.NotNull(resultContext.Endpoint);
                    Assert.NotNull(resultContext.HttpClient);
                    Assert.NotNull(resultContext.Response);

                    callbackCalled = true;
                    return Task.FromResult(CommandResults.Failure("A test error message"));
                }
            });

        // Act
        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        await MoveResourceToRunningStateAsync(app, service.Resource);

        var result = await app.ResourceCommands.ExecuteCommandAsync(service.Resource, "mycommand").DefaultTimeout();

        // Assert
        Assert.True(callbackCalled);
        Assert.False(result.Success);
        Assert.Equal("A test error message", result.ErrorMessage);
    }

    [Fact]
    public async Task WithHttpCommand_EnablesCommandOnceResourceIsRunning()
    {
        // Arrange
        using var builder = CreateTestDistributedApplicationBuilder();

        builder.Configuration["CODESPACES"] = "false";

        var service = builder.AddResource(new CustomResource("service"))
            .WithHttpEndpoint()
            .WithHttpCommand("/dothing", "Do The Thing", commandName: "mycommand");

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        // Move the resource to the starting state and verify command is disabled
        await app.ResourceNotifications.PublishUpdateAsync(service.Resource, s => s with
        {
            State = KnownResourceStates.Starting
        }).DefaultTimeout();

        var startingEvent = await app.ResourceNotifications.WaitForResourceAsync(
            service.Resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.Starting).DefaultTimeout();

        Assert.Equal(ResourceCommandState.Disabled, startingEvent.Snapshot.Commands.First(c => c.Name == "mycommand").State);

        // Move the resource to the running state
        await app.ResourceNotifications.PublishUpdateAsync(service.Resource, s => s with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        // Wait for the command to become enabled (this happens after resource becomes ready)
        var runningEvent = await app.ResourceNotifications.WaitForResourceAsync(
            service.Resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.Running &&
                 e.Snapshot.Commands.First(c => c.Name == "mycommand").State == ResourceCommandState.Enabled).DefaultTimeout();

        Assert.Equal(ResourceCommandState.Enabled, runningEvent.Snapshot.Commands.First(c => c.Name == "mycommand").State);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task WithHttpCommand_EnablesCommandUsingCustomUpdateStateCallback()
    {
        // Arrange
        using var builder = CreateTestDistributedApplicationBuilder();

        builder.Configuration["CODESPACES"] = "false";

        var enableCommand = false;
        var callbackCalled = false;
        var service = builder.AddResource(new CustomResource("service"))
            .WithHttpEndpoint()
            .WithHttpCommand("/dothing", "Do The Thing",
                commandName: "mycommand",
                commandOptions: new()
                {
                    UpdateState = usc =>
                    {
                        callbackCalled = true;
                        return enableCommand ? ResourceCommandState.Enabled : ResourceCommandState.Hidden;
                    }
                });

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();

        // Move the resource to the running state
        await app.ResourceNotifications.PublishUpdateAsync(service.Resource, s => s with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        // Wait for the resource to be running and verify command is hidden (custom UpdateState returns Hidden)
        var runningEvent = await app.ResourceNotifications.WaitForResourceAsync(
            service.Resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.Running &&
                 e.Snapshot.Commands.First(c => c.Name == "mycommand").State == ResourceCommandState.Hidden).DefaultTimeout();

        Assert.Equal(ResourceCommandState.Hidden, runningEvent.Snapshot.Commands.First(c => c.Name == "mycommand").State);

        // Enable the command and publish an update to force reevaluation
        enableCommand = true;
        await app.ResourceNotifications.PublishUpdateAsync(service.Resource, s => s with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        // Wait for the command to become enabled
        var enabledEvent = await app.ResourceNotifications.WaitForResourceAsync(
            service.Resource.Name,
            e => e.Snapshot.Commands.First(c => c.Name == "mycommand").State == ResourceCommandState.Enabled).DefaultTimeout();

        Assert.True(callbackCalled);
        Assert.Equal(ResourceCommandState.Enabled, enabledEvent.Snapshot.Commands.First(c => c.Name == "mycommand").State);

        await app.StopAsync().DefaultTimeout();
    }

    private IDistributedApplicationTestingBuilder CreateTestDistributedApplicationBuilder()
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        // Disable retries for the commandclient HTTP client to make test faster and deterministic.
        // TestDistributedApplicationBuilder adds a default resilience handler via ConfigureHttpClientDefaults.
        // The handler pipeline is named "{clientName}-standard" so for "commandclient" it's "commandclient-standard".
        builder.Services.Configure<HttpStandardResilienceOptions>("commandclient-standard", options =>
        {
            options.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
        });
        builder.Services.Configure<HttpStandardResilienceOptions>("-standard", options =>
        {
            options.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
        });
        return builder;
    }

    /// <summary>
    /// Moves a resource to the running state and waits for the HTTP command to become enabled.
    /// </summary>
    private static async Task MoveResourceToRunningStateAsync(DistributedApplication app, IResource resource, string commandName = "mycommand")
    {
        await app.ResourceNotifications.PublishUpdateAsync(resource, s => s with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        // Wait for resource to be running AND for the command to become enabled
        // The command state is updated synchronously when PublishUpdateAsync is called, but we still need to wait
        // for the notification to propagate through the ResourceNotificationService event system
        await app.ResourceNotifications.WaitForResourceAsync(
            resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.Running &&
                 e.Snapshot.Commands.FirstOrDefault(c => c.Name == commandName)?.State == ResourceCommandState.Enabled).DefaultTimeout();
    }

    /// <summary>
    /// Creates a CustomResource with an HTTP endpoint and pre-allocates the endpoint
    /// so HTTP commands can resolve the URL without requiring DCP allocation.
    /// </summary>
    private static IResourceBuilder<CustomResource> CreateResourceWithAllocatedEndpoint(IDistributedApplicationBuilder builder, string name, int port = 8080)
    {
        var service = builder.AddResource(new CustomResource(name))
            .WithHttpEndpoint(targetPort: port);

        var endpointAnnotation = service.Resource.Annotations.OfType<EndpointAnnotation>().Single();
        endpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(endpointAnnotation, "localhost", port);

        return service;
    }

    private sealed class CustomResource(string name) : Resource(name), IResourceWithEndpoints, IResourceWithWaitSupport
    {

    }
}
