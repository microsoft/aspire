// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZUREHEALTH001

using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using HealthModelPlayground;

// Playground for the health model and the resource graph.
//
// The topology below is a strict tree: every resource has exactly one parent, and nothing is shared between
// branches. That is deliberate. A shared dependency turns the model into a graph, which is legitimate but
// makes it much harder to see at a glance whether health is rolling up correctly, because a single unhealthy
// leaf lights up several unrelated-looking paths at once.
//
//   storefront                        (Healthy)   web front end
//   ├── checkout-api                  (Healthy)
//   │   ├── orders-db-server          (Healthy)
//   │   │   └── orders-db             (Healthy)   child of its server
//   │   └── payments-gateway          (Degraded)  <- degrades the checkout branch
//   ├── catalog-api                   (Healthy)
//   │   ├── catalog-db-server         (Healthy)
//   │   │   └── catalog-db            (Healthy)   child of its server
//   │   └── search-index              (Unhealthy) <- fails the catalog branch
//   └── identity-api                  (Healthy)   fully healthy branch
//       └── identity-cache            (Healthy)
//
// Only two resources are anything other than healthy, so every other colour in the graph has been
// inherited. The expected result once everything has started:
//
//   storefront    Unhealthy  (worst of its branches, via catalog-api -> search-index)
//   checkout-api  Degraded   (via payments-gateway)
//   catalog-api   Unhealthy  (via search-index)
//   identity-api  Healthy    (nothing unhealthy underneath it)

var builder = DistributedApplication.CreateBuilder(args);

HealthModelScenario.AddHealthChecks(builder.Services);
var resources = HealthModelScenario.Resources.ToDictionary(resource => resource.Name, AddTestResource, StringComparer.Ordinal);
foreach (var resource in HealthModelScenario.Resources)
{
    if (resource.ParentName is { } parent)
    {
        resources[resource.Name].WithParentRelationship(resources[parent]);
    }
    foreach (var dependency in resource.Dependencies)
    {
        resources[resource.Name].WithReferenceRelationship(resources[dependency]);
    }
}

if (builder.ExecutionContext.IsPublishMode)
{
    builder.AddAzureContainerAppEnvironment("azure");
    var health = builder.AddAzureHealthModel("health", "aspire-healthmodel.json");
    var metrics = builder.AddProject<Projects.HealthModel_Metrics>("health-metrics")
        .WithHttpEndpoint(name: "http", targetPort: 8080)
        .WithHttpHealthCheck("/alive")
        .PublishAsAzureContainerApp((_, app) =>
        {
            app.Template.Scale.MinReplicas = 1;
            app.Template.Scale.MaxReplicas = 1;
        });

    builder.AddAzureContainerAppsHealthModelCollector("health-collector", health, metrics.GetEndpoint("http"));
}
else
{
    builder.Services.TryAddEventingSubscriber<TestResourceLifecycle>();
}

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
if (builder.ExecutionContext.IsRunMode)
{
    builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
}
#endif

builder.Build().Run();

IResourceBuilder<TestResource> AddTestResource(HealthModelScenarioResource resource)
{
    return builder
        .AddResource(new TestResource(resource.Name))
        .WithHealthCheck(resource.HealthCheckName)
        .WithInitialState(new()
        {
            ResourceType = "Test Resource",
            State = "Starting",
            Properties = [],
        })
        .ExcludeFromManifest();
}

internal sealed class TestResource(string name) : Resource(name), IResourceWithEndpoints;

/// <summary>
/// Moves the test resources to the running state shortly after startup.
/// </summary>
/// <remarks>
/// The delay is intentional. It leaves the resources in an unknown state for long enough to see that a
/// starting resource does not colour the graph, which is the behaviour that keeps the model from flashing
/// red every time the app host boots.
/// </remarks>
internal sealed class TestResourceLifecycle(ResourceNotificationService notificationService) : IDistributedApplicationEventingSubscriber
{
    public async Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        // Keep startup work owned by the lifecycle event so cancellation and publication failures are
        // observed instead of escaping from fire-and-forget tasks after the AppHost has stopped.
        await Task.WhenAll(@event.Model.Resources.OfType<TestResource>().Select(resource =>
            notificationService.PublishUpdateAsync(
                resource,
                state => state with { State = new("Running", "success") })));
    }

    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        return Task.CompletedTask;
    }
}
