// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.AppService;

internal sealed class AzureAppServiceInfrastructure(
    ILogger<AzureAppServiceInfrastructure> logger,
    IOptions<AzureProvisioningOptions> provisioningOptions,
    DistributedApplicationExecutionContext executionContext)
{
    internal async Task PrepareInfrastructureAsync(DistributedApplicationModel model, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (!executionContext.IsPublishMode)
        {
            return;
        }

        var appServiceEnvironments = model.Resources.OfType<AzureAppServiceEnvironmentResource>().ToArray();

        if (appServiceEnvironments.Length == 0)
        {
            EnsureNoPublishAsAzureAppServiceWebsiteAnnotations(model);
            return;
        }

        foreach (var appServiceEnvironment in appServiceEnvironments)
        {
            // Remove the default container registry from the model if an explicit registry is configured
            if (appServiceEnvironment.HasAnnotationOfType<ContainerRegistryReferenceAnnotation>() &&
                appServiceEnvironment.DefaultContainerRegistry is not null)
            {
                model.Resources.Remove(appServiceEnvironment.DefaultContainerRegistry);
            }

            var appServiceEnvironmentContext = new AzureAppServiceEnvironmentContext(
                logger,
                executionContext,
                appServiceEnvironment,
                services);

            // Annotate the environment with its context
            appServiceEnvironment.Annotations.Add(new AzureAppServiceEnvironmentContextAnnotation(appServiceEnvironmentContext));

            foreach (var resource in model.GetComputeResources())
            {
                // Support project resources and containers with Dockerfile
                if (resource is not ProjectResource && !(resource.IsContainer() && resource.TryGetAnnotationsOfType<DockerfileBuildAnnotation>(out _)))
                {
                    continue;
                }

                // Skip resources that are explicitly targeted to a different compute environment
                var resourceComputeEnvironment = resource.GetComputeEnvironment();
                if (resourceComputeEnvironment is not null && resourceComputeEnvironment != appServiceEnvironment)
                {
                    continue;
                }

                var website = await appServiceEnvironmentContext.CreateAppServiceAsync(resource, provisioningOptions.Value, cancellationToken).ConfigureAwait(false);

                resource.Annotations.Add(new DeploymentTargetAnnotation(website)
                {
                    ContainerRegistry = appServiceEnvironment,
                    ComputeEnvironment = appServiceEnvironment
                });
            }
        }
    }

    private static void EnsureNoPublishAsAzureAppServiceWebsiteAnnotations(DistributedApplicationModel appModel)
    {
        foreach (var r in appModel.GetComputeResources())
        {
            if (r.HasAnnotationOfType<AzureAppServiceWebsiteCustomizationAnnotation>())
            {
                throw new InvalidOperationException($"Resource '{r.Name}' is configured to publish as an Azure AppService Website, but there are no '{nameof(AzureAppServiceEnvironmentResource)}' resources. Ensure you have added one by calling '{nameof(AzureAppServiceEnvironmentExtensions.AddAzureAppServiceEnvironment)}'.");
            }
        }
    }
}
