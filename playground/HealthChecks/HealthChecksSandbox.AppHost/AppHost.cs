// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = DistributedApplication.CreateBuilder(args);

builder.Services.TryAddEventingSubscriber<TestResourceLifecycle>();

AddTestResource("healthy", HealthStatus.Healthy, "I'm fine, thanks for asking.");
AddTestResource("unhealthy", HealthStatus.Unhealthy, "I can't do that, Dave.", exceptionMessage: "Feeling unhealthy.");
AddTestResource("degraded", HealthStatus.Degraded, "Had better days.", exceptionMessage: "Feeling degraded.");

// -----------------------------------------------------------------------
// External services with HTTP health checks to test friendly error messages
// (PR #14072: Add friendly error messages to health check failures)
//
// Friendly message cases from HttpHealthCheckHelpers.GetFriendlyErrorMessage:
//   1. Connection refused  → "Failed to connect to {uri}."
//   2. Timeout             → "Request to {uri} timed out."
//   3. Wrong status code   → "Request to {uri} returned {code} {name}."
//   4. Canceled            → "Health check for {uri} was canceled."
//   5. Invalid URL param   → resource goes to FailedToStart
//   6. Deferred URI (null) → "The URI for the health check is not set..."
//   7. Generic failure     → "Health check failed for {uri}."
// -----------------------------------------------------------------------

// Case 1 (StaticUriHealthCheck): Connection refused — nothing listening
//   → "Failed to connect to http://localhost:19999/."
builder.AddExternalService("http-connection-refused", "http://localhost:19999/")
    .WithHttpHealthCheck();

// Case 2 (StaticUriHealthCheck): Timeout — non-routable TEST-NET-1 address
//   → "Request to http://192.0.2.1/ timed out."
builder.AddExternalService("http-timeout", "http://192.0.2.1/")
    .WithHttpHealthCheck();

// Case 3 (StaticUriHealthCheck): Wrong status code — expects 200 on a port with nothing listening
//   → "Failed to connect to http://localhost:19998/." (or status code message if a server runs there)
//   To test the actual status-code-mismatch message, start a server on 19998 that returns non-200:
//     python3 -m http.server 19998
//   Then you'll see → "Request to http://localhost:19998/ returned 404 NotFound."
builder.AddExternalService("http-wrong-status", "http://localhost:19998/")
    .WithHttpHealthCheck(statusCode: 200);

// Case 4 (ParameterUriHealthCheck): Connection refused — parameterized URL
//   → "Failed to connect to http://localhost:19997/."
builder.Configuration["Parameters:unhealthy-url"] = "http://localhost:19997/";
var unhealthyUrlParam = builder.AddParameter("unhealthy-url");
builder.AddExternalService("http-param-refused", unhealthyUrlParam)
    .WithHttpHealthCheck();

// Case 5 (ParameterUriHealthCheck): Invalid URL → FailedToStart
//   → resource immediately enters FailedToStart state (URL validation fails before health check)
builder.Configuration["Parameters:invalid-url"] = "not-a-valid-url";
var invalidUrlParam = builder.AddParameter("invalid-url");
builder.AddExternalService("http-param-invalid-url", invalidUrlParam)
    .WithHttpHealthCheck();

// Case 6 (DeferredUriHealthCheck): Resource with endpoint, nothing listening
//   → "Failed to connect to http://localhost:19996/." once URI resolves
//   This exercises the generic WithHttpHealthCheck<T> for resources with endpoints.
builder.AddResource(new TestResource("http-deferred"))
    .WithHttpEndpoint(port: 19996, name: "http")
    .WithHttpHealthCheck(endpointName: "http")
    .WithInitialState(new()
    {
        ResourceType = "Test Resource",
        State = "Starting",
        Properties = [],
    })
    .ExcludeFromManifest();

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();

void AddTestResource(string name, HealthStatus status, string? description = null, string? exceptionMessage = null)
{
    var hasHealthyAfterFirstRunCheckRun = false;
    builder.Services.AddHealthChecks()
                    .AddCheck(
                        $"{name}_check",
                        () => new HealthCheckResult(status, description, new InvalidOperationException(exceptionMessage))
                        )
                        .AddCheck($"{name}_resource_healthy_after_first_run_check", () =>
                        {
                            if (!hasHealthyAfterFirstRunCheckRun)
                            {
                                hasHealthyAfterFirstRunCheckRun = true;
                                return new HealthCheckResult(HealthStatus.Unhealthy, "Initial failure state.");
                            }

                            return new HealthCheckResult(HealthStatus.Healthy, "Healthy beginning second health check run.");
                        });

    builder
        .AddResource(new TestResource(name))
        .WithHealthCheck($"{name}_check")
        .WithHealthCheck($"{name}_resource_healthy_after_first_run_check")
        .WithInitialState(new()
        {
            ResourceType = "Test Resource",
            State = "Starting",
            Properties = [],
        })
        .ExcludeFromManifest();
    return;
}

internal sealed class TestResource(string name) : Resource(name), IResourceWithEndpoints;

internal sealed class TestResourceLifecycle(ResourceNotificationService notificationService) : IDistributedApplicationEventingSubscriber
{
    public Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken)
    {
        foreach (var resource in @event.Model.Resources.OfType<TestResource>())
        {
            Task.Run(
                async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(10));

                    await notificationService.PublishUpdateAsync(
                        resource,
                        state => state with { State = new("Running", "success") });
                },
                cancellationToken);
        }

        return Task.CompletedTask;
    }

    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        return Task.CompletedTask;
    }
}
