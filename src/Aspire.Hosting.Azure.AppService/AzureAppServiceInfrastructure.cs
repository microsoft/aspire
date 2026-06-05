// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.AppService;

internal sealed class AzureAppServiceInfrastructure(
    ILogger<AzureAppServiceInfrastructure> logger,
    IOptions<AzureProvisioningOptions> provisioningOptions,
    DistributedApplicationExecutionContext executionContext) :
    IDistributedApplicationEventingSubscriber
{
    private async Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken = default)
    {
        if (!executionContext.IsPublishMode)
        {
            return;
        }

        var appServiceEnvironments = @event.Model.Resources.OfType<AzureAppServiceEnvironmentResource>().ToArray();

        if (appServiceEnvironments.Length == 0)
        {
            EnsureNoPublishAsAzureAppServiceWebsiteAnnotations(@event.Model);
            return;
        }

        foreach (var appServiceEnvironment in appServiceEnvironments)
        {
            // Remove the default container registry from the model if an explicit registry is configured
            if (appServiceEnvironment.HasAnnotationOfType<ContainerRegistryReferenceAnnotation>() &&
                appServiceEnvironment.DefaultContainerRegistry is not null)
            {
                @event.Model.Resources.Remove(appServiceEnvironment.DefaultContainerRegistry);
            }

            var appServiceEnvironmentContext = new AzureAppServiceEnvironmentContext(
                logger,
                executionContext,
                appServiceEnvironment,
                @event.Services);

            // Annotate the environment with its context
            appServiceEnvironment.Annotations.Add(new AzureAppServiceEnvironmentContextAnnotation(appServiceEnvironmentContext));

            foreach (var resource in @event.Model.GetComputeResources())
            {
                // Support project resources and containers with Dockerfile
                if (resource is not ProjectResource && !(resource.IsContainer() && resource.TryGetAnnotationsOfType<DockerfileBuildAnnotation>(out _)))
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

    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        return Task.CompletedTask;
    }
}
