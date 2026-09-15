// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HealthModelPlayground;

// This is a simulation, not a translation of arbitrary AppHost health-check delegates.
// Both the local resources and the deployed producer register these same validators.
internal static class HealthModelScenario
{
    public static IReadOnlyList<HealthModelScenarioResource> Resources { get; } =
    [
        new("storefront", "Serving customers.", HealthStatus.Healthy, null, ["checkout-api", "catalog-api", "identity-api"], null),
        new("checkout-api", "Accepting orders.", HealthStatus.Healthy, null, ["orders-db-server", "payments-gateway"], null),
        new("orders-db-server", "Accepting connections.", HealthStatus.Healthy, null, [], null),
        new("orders-db", "Migrations applied.", HealthStatus.Healthy, "orders-db-server", [], null),
        new("payments-gateway", "Elevated latency from the payment provider.", HealthStatus.Degraded, null, [], null),
        new("catalog-api", "Serving product data.", HealthStatus.Healthy, null, ["catalog-db-server", "search-index"], null),
        new("catalog-db-server", "Accepting connections.", HealthStatus.Healthy, null, [], null),
        new("catalog-db", "Migrations applied.", HealthStatus.Healthy, "catalog-db-server", [], null),
        new("search-index", "Index rebuild failed.", HealthStatus.Unhealthy, null, [], "Shard 3 is offline."),
        new("identity-api", "Issuing tokens.", HealthStatus.Healthy, null, ["identity-cache"], null),
        new("identity-cache", "Cache warm.", HealthStatus.Healthy, null, [], null)
    ];

    public static HealthCheckResult Evaluate(string resourceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        var resource = Resources.FirstOrDefault(resource => string.Equals(resource.Name, resourceName, StringComparison.Ordinal));
        if (resource is null)
        {
            throw new ArgumentException($"Unknown health-model scenario resource '{resourceName}'.", nameof(resourceName));
        }

        return new HealthCheckResult(
            resource.HealthStatus,
            resource.Description,
            resource.ExceptionMessage is null ? null : new InvalidOperationException(resource.ExceptionMessage));
    }

    public static IHealthChecksBuilder AddHealthChecks(IServiceCollection services)
    {
        var builder = services.AddHealthChecks();
        foreach (var resource in Resources)
        {
            builder.AddCheck(resource.HealthCheckName, () => Evaluate(resource.Name));
        }

        return builder;
    }
}

internal sealed record HealthModelScenarioResource(
    string Name,
    string Description,
    HealthStatus HealthStatus,
    string? ParentName,
    IReadOnlyList<string> Dependencies,
    string? ExceptionMessage)
{
    public string HealthCheckName => $"{Name}_check";
}
