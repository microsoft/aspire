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
    private readonly List<(string ResourceName, string[] EndpointNames)> _upgradedEndpoints = [];
    private bool _hasLoggedHttpsUpgrade;

    /// <summary>
    /// Records HTTP endpoints that were upgraded to HTTPS for a resource.
    /// </summary>
    public void RecordHttpsUpgrade(string resourceName, string[] endpointNames)
    {
        if (endpointNames.Length > 0)
        {
            _upgradedEndpoints.Add((resourceName, endpointNames));
        }
    }

    /// <summary>
    /// Logs a single message about all HTTP endpoints that were upgraded to HTTPS.
    /// </summary>
    public void LogHttpsUpgradeIfNeeded()
    {
        if (_hasLoggedHttpsUpgrade || _upgradedEndpoints.Count == 0)
        {
            return;
        }

        _hasLoggedHttpsUpgrade = true;

        var details = string.Join(", ", _upgradedEndpoints.Select(x =>
            x.EndpointNames.Length == 1
                ? $"{x.ResourceName}:{x.EndpointNames[0]}"
                : $"{x.ResourceName}:{{{string.Join(", ", x.EndpointNames)}}}"));

        Logger.LogInformation(
            "HTTP endpoints will use HTTPS (port 443) in Azure App Service: {Details}. " +
            "To opt out, use .WithHttpsUpgrade(false) on the app service environment.",
            details);
    }

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
