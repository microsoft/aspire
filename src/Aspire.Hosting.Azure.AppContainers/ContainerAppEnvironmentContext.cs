// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppContainers;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

internal sealed class ContainerAppEnvironmentContext(
    ILogger logger,
    DistributedApplicationExecutionContext executionContext,
    AzureContainerAppEnvironmentResource environment,
    IServiceProvider serviceProvider)
{
    public ILogger Logger => logger;

    public DistributedApplicationExecutionContext ExecutionContext => executionContext;

    public AzureContainerAppEnvironmentResource Environment => environment;

    public IServiceProvider ServiceProvider => serviceProvider;

    private readonly Dictionary<IResource, BaseContainerAppContext> _containerApps = new(new ResourceNameComparer());
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
            "HTTP endpoints will use HTTPS (port 443) in Azure Container Apps: {Details}. " +
            "To opt out, use .WithHttpsUpgrade(false) on the container app environment.",
            details);
    }

    public BaseContainerAppContext GetContainerAppContext(IResource resource)
    {
        if (!_containerApps.TryGetValue(resource, out var context))
        {
            throw new InvalidOperationException($"Container app context not found for resource {resource.Name}.");
        }

        return context;
    }

    public async Task<AzureBicepResource> CreateContainerAppAsync(IResource resource, AzureProvisioningOptions provisioningOptions, CancellationToken cancellationToken)
    {
        if (!_containerApps.TryGetValue(resource, out var context))
        {
            _containerApps[resource] = context = CreateContainerAppContext(resource);
            await context.ProcessResourceAsync(cancellationToken).ConfigureAwait(false);
        }

        var provisioningResource = new AzureContainerAppResource(resource.Name + "-containerapp", context.BuildContainerApp, resource)
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

    private BaseContainerAppContext CreateContainerAppContext(IResource resource)
    {
        bool hasJobCustomization = resource.HasAnnotationOfType<AzureContainerAppJobCustomizationAnnotation>();
        bool hasAppCustomization = resource.HasAnnotationOfType<AzureContainerAppCustomizationAnnotation>();

        if (hasJobCustomization && hasAppCustomization)
        {
            throw new InvalidOperationException($"Resource '{resource.Name}' cannot have both AzureContainerAppCustomizationAnnotation and AzureContainerAppJobCustomizationAnnotation.");
        }

        if (hasJobCustomization)
        {
            return new ContainerAppJobContext(resource, this);
        }

        return new ContainerAppContext(resource, this);
    }
}
