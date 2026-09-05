// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.RabbitMQ.Provisioning;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RabbitMQ.Tests.TestServices;

// Shared test host for the RabbitMQ topology lifecycle/command tests. It builds the single
// service graph those tests need and exposes the small set of runtime helpers they all repeat,
// so setup, state publishing, and event firing are defined once.
//
// The critical detail this centralizes: the topology lifecycle (StopCore/StartCore) and the
// custom Start command's updateState both call into ResourceNotificationService — and
// PublishUpdateAsync re-runs every command's updateState via its internal UpdateCommands step.
// The Start command's updateState resolves ResourceNotificationService from the service provider
// the notification service was constructed with (the Defect 4 parent-liveness gate). So the
// ResourceNotificationService MUST be registered in the SAME provider it is built from. A plain
// ResourceNotificationServiceTestHelpers.Create() uses an empty provider and throws once a command
// re-evaluates. Building the container first and registering the notification service via a factory
// that captures that same container mirrors the production wiring (the notification service comes
// from the app's full container) and keeps every test self-consistent.
//
// The IRabbitMQProvisioningClient is registered keyed by the server name, matching how the
// lifecycle handlers and commands resolve it in production (GetRequiredKeyedService<...>(serverName)).
//
// Pure static factories (BuildVhost, MakeHealthCheck, MakeHealthCheckContext, PublishServerRunningAsync)
// live in RabbitMQTopologyTestFactory so this type holds only instance/DI-fixture concerns.
internal sealed class RabbitMQTopologyTestHost
{
    private RabbitMQTopologyTestHost(IServiceProvider services, ResourceNotificationService notifications, FakeRabbitMQProvisioningClient client)
    {
        Services = services;
        Notifications = notifications;
        Client = client;
    }

    // The self-consistent service provider (contains the notification service, keyed client, logging).
    public IServiceProvider Services { get; }

    // The notification service the test observes; the same instance the lifecycle/commands publish through.
    public ResourceNotificationService Notifications { get; }

    // The in-memory provisioning client whose recorded Calls the tests assert on.
    public FakeRabbitMQProvisioningClient Client { get; }

    // A logger suitable for hand-built ExecuteCommandContext instances.
    public ILogger Logger => Services.GetRequiredService<ILoggerFactory>().CreateLogger("test");

    // Number of recorded broker delete calls (any Delete*), used to assert deletes were/weren't issued.
    public int DeleteCallCount => Client.Calls.Count(c => c.StartsWith("Delete", StringComparison.Ordinal));

    // Builds a test host wired for the given serverName (the key under which the provisioning client is
    // resolved). Pass an existing client to seed drift state before building, or let the host create a
    // fresh one.
    public static RabbitMQTopologyTestHost Create(string serverName, FakeRabbitMQProvisioningClient? client = null)
    {
        client ??= new FakeRabbitMQProvisioningClient();

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton(new ResourceLoggerService());
        collection.AddKeyedSingleton<IRabbitMQProvisioningClient>(serverName, client);
        collection.AddSingleton<ResourceNotificationService>(sp => new ResourceNotificationService(
            sp.GetRequiredService<ILogger<ResourceNotificationService>>(),
            new TestHostApplicationLifetime(),
            sp,
            sp.GetRequiredService<ResourceLoggerService>()));

        var services = collection.BuildServiceProvider();
        var notifications = services.GetRequiredService<ResourceNotificationService>();
        return new RabbitMQTopologyTestHost(services, notifications, client);
    }

    // Publishes resource in the KnownResourceStates.Running state via this host's notification service.
    public Task PublishRunningAsync(IResource resource)
        => Notifications.PublishUpdateAsync(resource, s => s with { State = KnownResourceStates.Running });

    // Reads back the current framework state text for resourceName (null if none published).
    public string? CurrentState(string resourceName)
        => Notifications.TryGetCurrentState(resourceName, out var evt) ? evt.Snapshot.State?.Text : null;

    // Fires a ResourceReadyEvent for resource through the builder's eventing pipeline, using this
    // host's service provider as the event's service scope.
    public Task PublishReadyAsync(IDistributedApplicationBuilder builder, IResource resource)
        => builder.Eventing.PublishAsync(new ResourceReadyEvent(resource, Services)).DefaultTimeout();

    // Fires a ResourceStoppedEvent for resource through the builder's eventing pipeline, using this
    // host's service provider as the event's service scope.
    public Task PublishStoppedAsync(IDistributedApplicationBuilder builder, IResource resource, string resourceType)
        => builder.Eventing.PublishAsync(new ResourceStoppedEvent(resource, Services,
            new ResourceEvent(resource, resource.Name,
                new CustomResourceSnapshot { ResourceType = resourceType, Properties = [], State = KnownResourceStates.Exited }))).DefaultTimeout();
}
