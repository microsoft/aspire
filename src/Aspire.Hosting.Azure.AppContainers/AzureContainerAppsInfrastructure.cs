// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppContainers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Computes Azure Container Apps deployment targets for compute resources in the application model.
/// </summary>
/// <remarks>
/// Invoked by the <c>prepare-azure-container-apps</c> pipeline step, which runs after
/// <c>azure-prepare-resources</c> so that role-assignment resources produced by
/// <see cref="Aspire.Hosting.Azure.AzureResourcePreparer"/> are present in the model when this
/// runs and can be referenced by the generated <see cref="DeploymentTargetAnnotation"/> instances.
/// </remarks>
internal sealed class AzureContainerAppsInfrastructure(
    ILogger<AzureContainerAppsInfrastructure> logger,
    DistributedApplicationExecutionContext executionContext,
    IOptions<AzureProvisioningOptions> options)
{
    public async Task PrepareDeploymentTargetsAsync(
        DistributedApplicationModel appModel,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var caes = appModel.Resources.OfType<AzureContainerAppEnvironmentResource>().ToArray();

        if (caes.Length == 0)
        {
            EnsureNoPublishAsAcaAnnotations(appModel);
            return;
        }

        foreach (var environment in caes)
        {
            // Remove the default container registry from the model if an explicit registry is configured
            if (environment.HasAnnotationOfType<ContainerRegistryReferenceAnnotation>() &&
                environment.DefaultContainerRegistry is not null)
            {
                appModel.Resources.Remove(environment.DefaultContainerRegistry);
            }

            var containerAppEnvironmentContext = new ContainerAppEnvironmentContext(
                logger,
                executionContext,
                environment,
                services);

            foreach (var r in appModel.GetComputeResources())
            {
                // Skip resources that are explicitly targeted to a different compute environment
                var resourceComputeEnvironment = r.GetComputeEnvironment();
                if (resourceComputeEnvironment is not null && resourceComputeEnvironment != environment)
                {
                    continue;
                }

                var containerApp = await containerAppEnvironmentContext.CreateContainerAppAsync(r, options.Value, cancellationToken).ConfigureAwait(false);

                // Capture information about the container registry used by the
                // container app environment in the deployment target information
                // associated with each compute resource that needs an image
                r.Annotations.Add(new DeploymentTargetAnnotation(containerApp)
                {
                    ContainerRegistry = environment,
                    ComputeEnvironment = environment
                });
            }

            // Log once about all HTTP endpoints upgraded to HTTPS
            containerAppEnvironmentContext.LogHttpsUpgradeIfNeeded();
        }
    }

    private static void EnsureNoPublishAsAcaAnnotations(DistributedApplicationModel appModel)
    {
        foreach (var r in appModel.GetComputeResources())
        {
            if (r.HasAnnotationOfType<AzureContainerAppCustomizationAnnotation>() ||
#pragma warning disable ASPIREAZURE002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                r.HasAnnotationOfType<AzureContainerAppJobCustomizationAnnotation>())
#pragma warning restore ASPIREAZURE002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            {
                throw new InvalidOperationException($"Resource '{r.Name}' is configured to publish as an Azure Container App, but there are no '{nameof(AzureContainerAppEnvironmentResource)}' resources. Ensure you have added one by calling '{nameof(AzureContainerAppExtensions.AddAzureContainerAppEnvironment)}'.");
            }
        }
    }
}
