// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppService;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

internal sealed class AzureAppServiceEnvironmentContext(
    ILogger logger,
    DistributedApplicationExecutionContext executionContext,
    AzureAppServiceEnvironmentResource environment,
    IServiceProvider serviceProvider)
{
    public ILogger Logger => logger;

    public DistributedApplicationExecutionContext ExecutionContext => executionContext;

    public AzureAppServiceEnvironmentResource Environment => environment;

    public IServiceProvider ServiceProvider => serviceProvider;

    private readonly Dictionary<IResource, AzureAppServiceWebsiteContext> _appServices = new(new ResourceNameComparer());

    public AzureAppServiceWebsiteContext GetAppServiceContext(IResource resource)
    {
        if (!_appServices.TryGetValue(resource, out var context))
        {
            throw new InvalidOperationException($"App Service context not found for resource {resource.Name}.");
        }

        return context;
    }

    public async Task<AzureBicepResource> CreateAppServiceAsync(IResource resource, AzureProvisioningOptions provisioningOptions, CancellationToken cancellationToken)
    {
        if (!_appServices.TryGetValue(resource, out var context))
        {
            _appServices[resource] = context = new AzureAppServiceWebsiteContext(resource, this);
            await context.ProcessAsync(cancellationToken).ConfigureAwait(false);
        }

        var provisioningResource = new AzureAppServiceWebSiteResource(resource.Name + "-website", context.BuildWebSite, resource)
        {
            ProvisioningBuildOptions = provisioningOptions.ProvisioningBuildOptions
        };

        // Add references to any prerequisite resources to ensure they are provisioned first
        if (resource.TryGetAnnotationsOfType<DeploymentPrerequisitesAnnotation>(out var prereqs))
        {
            foreach (var prereq in prereqs.SelectMany(p => p.Resources))
            {
                provisioningResource.References.Add(prereq);
            }
        }

        return provisioningResource;
    }
}
