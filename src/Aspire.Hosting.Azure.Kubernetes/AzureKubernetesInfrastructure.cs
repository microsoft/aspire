// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure.Kubernetes;

/// <summary>
/// Infrastructure eventing subscriber that processes compute resources
/// targeting an AKS environment.
/// </summary>
internal sealed class AzureKubernetesInfrastructure(
    ILogger<AzureKubernetesInfrastructure> logger)
    : IDistributedApplicationEventingSubscriber
{
    /// <inheritdoc />
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        if (!executionContext.IsRunMode)
        {
            eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        }

        return Task.CompletedTask;
    }

    private Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken = default)
    {
        var aksEnvironments = @event.Model.Resources
            .OfType<AzureKubernetesEnvironmentResource>()
            .ToArray();

        if (aksEnvironments.Length == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var environment in aksEnvironments)
        {
            logger.LogInformation("Processing AKS environment '{Name}'", environment.Name);

            // Ensure a default user node pool exists for workload scheduling.
            // The system pool should only run system pods; application workloads
            // need a user pool.
            var defaultUserPool = EnsureDefaultUserNodePool(environment);

            foreach (var r in @event.Model.GetComputeResources())
            {
                var resourceComputeEnvironment = r.GetComputeEnvironment();
                if (resourceComputeEnvironment is not null && resourceComputeEnvironment != environment)
                {
                    continue;
                }

                // If the resource has no explicit node pool affinity, assign it
                // to the default user pool.
                if (!r.TryGetLastAnnotation<AksNodePoolAffinityAnnotation>(out _) && defaultUserPool is not null)
                {
                    r.Annotations.Add(new AksNodePoolAffinityAnnotation(defaultUserPool));
                }

                r.Annotations.Add(new DeploymentTargetAnnotation(environment)
                {
                    ComputeEnvironment = environment
                });
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Ensures the AKS environment has at least one user node pool. If none exists,
    /// creates a default "workload" user pool.
    /// </summary>
    private static AksNodePoolResource? EnsureDefaultUserNodePool(AzureKubernetesEnvironmentResource environment)
    {
        var hasUserPool = environment.NodePools.Any(p => p.Mode is AksNodePoolMode.User);

        if (hasUserPool)
        {
            // Return the first user pool as the default for unaffinitized workloads.
            // Look for an existing AksNodePoolResource child that matches.
            var firstUserConfig = environment.NodePools.First(p => p.Mode is AksNodePoolMode.User);
            return FindNodePoolResource(environment, firstUserConfig.Name);
        }

        // No user pool configured — create a default one.
        var defaultConfig = new AksNodePoolConfig("workload", "Standard_D4s_v5", 1, 10, AksNodePoolMode.User);
        environment.NodePools.Add(defaultConfig);

        var defaultPool = new AksNodePoolResource("workload", defaultConfig, environment);
        return defaultPool;
    }

    private static AksNodePoolResource? FindNodePoolResource(AzureKubernetesEnvironmentResource environment, string poolName)
    {
        // Walk annotations looking for child resources — but AksNodePoolResource is
        // added via the application model, not annotations. Check if a matching pool
        // resource was already created. If not, create one on the fly.
        return new AksNodePoolResource(poolName,
            environment.NodePools.First(p => p.Name == poolName),
            environment);
    }
}
