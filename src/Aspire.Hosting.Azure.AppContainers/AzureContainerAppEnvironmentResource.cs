// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.AppContainers;

/// <summary>
/// Represents an Azure Container App Environment resource.
/// </summary>
#pragma warning disable CS0618 // Type or member is obsolete
public class AzureContainerAppEnvironmentResource :
    AzureProvisioningResource, IAzureComputeEnvironmentResource, IAzureContainerRegistry, IAzureDelegatedSubnetResource
#pragma warning restore CS0618 // Type or member is obsolete
{
    /// <inheritdoc />
    string IAzureDelegatedSubnetResource.DelegatedSubnetServiceName => "Microsoft.App/environments";

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureContainerAppEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the Container App Environment.</param>
    /// <param name="configureInfrastructure">The callback to configure the Azure infrastructure for this resource.</param>
    public AzureContainerAppEnvironmentResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
        : base(name, configureInfrastructure)
    {
        // Add pipeline step annotation to create steps and expand deployment target steps
        Annotations.Add(new PipelineStepAnnotation(async (factoryContext) =>
        {
            var model = factoryContext.PipelineContext.Model;
            var steps = new List<PipelineStep>();

            // Add print-dashboard-url step
            var printDashboardUrlStep = new PipelineStep
            {
                Name = $"print-dashboard-url-{name}",
                Description = $"Prints the deployment summary and dashboard URL for {name}.",
                Action = ctx => PrintDashboardUrlAsync(ctx),
                Tags = ["print-summary"],
                DependsOnSteps = [AzureEnvironmentResource.ProvisionInfrastructureStepName],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy]
            };

            steps.Add(printDashboardUrlStep);

            // Expand deployment target steps for all compute resources
            // This ensures the push/provision steps from deployment targets are included in the pipeline
            foreach (var computeResource in model.GetComputeResources())
            {
                var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(this)?.DeploymentTarget;

                if (deploymentTarget != null && deploymentTarget.TryGetAnnotationsOfType<PipelineStepAnnotation>(out var annotations))
                {
                    // Resolve the deployment target's PipelineStepAnnotation and expand its steps
                    // We do this because the deployment target is not in the model
                    foreach (var annotation in annotations)
                    {
                        var childFactoryContext = new PipelineStepFactoryContext
                        {
                            PipelineContext = factoryContext.PipelineContext,
                            Resource = deploymentTarget
                        };

                        var deploymentTargetSteps = await annotation.CreateStepsAsync(childFactoryContext).ConfigureAwait(false);

                        foreach (var step in deploymentTargetSteps)
                        {
                            // Ensure the step is associated with the deployment target resource
                            step.Resource ??= deploymentTarget;
                        }

                        steps.AddRange(deploymentTargetSteps);
                    }
                }
            }

            return steps;
        }));

        // Add pipeline configuration annotation to wire up dependencies
        // This is where we wire up the build steps created by the resources
        Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            // Wire up build step dependencies
            // Build steps are created by ProjectResource and ContainerResource
            foreach (var computeResource in context.Model.GetComputeResources())
            {
                var deploymentTarget = computeResource.GetDeploymentTargetAnnotation(this)?.DeploymentTarget;

                if (deploymentTarget is null)
                {
                    continue;
                }

                // Execute the PipelineConfigurationAnnotation callbacks on the deployment target
                if (deploymentTarget.TryGetAnnotationsOfType<PipelineConfigurationAnnotation>(out var annotations))
                {
                    foreach (var annotation in annotations)
                    {
                        annotation.Callback(context);
                    }
                }
            }

            // This ensures that resources that have to be built before deployments are handled
            foreach (var computeResource in context.Model.GetBuildResources())
            {
                context.GetSteps(computeResource, WellKnownPipelineTags.BuildCompute)
                        .RequiredBy(WellKnownPipelineSteps.Deploy)
                        .DependsOn(WellKnownPipelineSteps.DeployPrereq);
            }

            // Make print-summary step depend on provisioning of this environment
            var provisionSteps = context.GetSteps(this, WellKnownPipelineTags.ProvisionInfrastructure);
            var printDashboardUrlSteps = context.GetSteps(this, "print-summary");
            printDashboardUrlSteps.DependsOn(provisionSteps);
        }));
    }

    private async Task PrintDashboardUrlAsync(PipelineStepContext context)
    {
        var domainValue = await ContainerAppDomain.GetValueAsync(context.CancellationToken).ConfigureAwait(false);

        var dashboardUrl = $"https://aspire-dashboard.ext.{domainValue}";

        context.Summary.Add("📊 Dashboard", dashboardUrl);

        await context.ReportingStep.CompleteAsync(
            $"Dashboard available at [{dashboardUrl}]({dashboardUrl})",
            CompletionState.Completed,
            context.CancellationToken).ConfigureAwait(false);
    }
    internal bool UseAzdNamingConvention { get; set; }

    internal bool UseCompactResourceNaming { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Aspire dashboard should be included in the container app environment.
    /// Default is true.
    /// </summary>
    internal bool EnableDashboard { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether HTTP endpoints should be preserved as HTTP instead of being upgraded to HTTPS.
    /// Default is false (HTTP endpoints are upgraded to HTTPS).
    /// </summary>
    internal bool PreserveHttpEndpoints { get; set; }

    /// <summary>
    /// Gets the unique identifier of the Container App Environment.
    /// </summary>
    internal BicepOutputReference ContainerAppEnvironmentId => new("AZURE_CONTAINER_APPS_ENVIRONMENT_ID", this);

    /// <summary>
    /// Gets the default domain associated with the Container App Environment.
    /// </summary>
    internal BicepOutputReference ContainerAppDomain => new("AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN", this);

    /// <summary>
    /// Gets the name of the associated Azure Container Registry.
    /// </summary>
    internal BicepOutputReference ContainerRegistryName => new("AZURE_CONTAINER_REGISTRY_NAME", this);

    /// <summary>
    /// Gets the URL endpoint of the associated Azure Container Registry.
    /// </summary>
    internal BicepOutputReference ContainerRegistryUrl => new("AZURE_CONTAINER_REGISTRY_ENDPOINT", this);

    /// <summary>
    /// Gets the managed identity ID associated with the Azure Container Registry.
    /// </summary>
    internal BicepOutputReference ContainerRegistryManagedIdentityId => new("AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID", this);

    /// <summary>
    /// Gets the name of the Container App Environment.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("AZURE_CONTAINER_APPS_ENVIRONMENT_NAME", this);

    internal Dictionary<string, (IResource resource, ContainerMountAnnotation volume, int index, BicepOutputReference outputReference)> VolumeNames { get; } = [];

    /// <summary>
    /// Gets the default container registry for this environment.
    /// </summary>
    internal AzureContainerRegistryResource? DefaultContainerRegistry { get; set; }

    ReferenceExpression IContainerRegistry.Name => GetContainerRegistry()?.Name ?? ReferenceExpression.Create($"{ContainerRegistryName}");

    ReferenceExpression IContainerRegistry.Endpoint => GetContainerRegistry()?.Endpoint ?? ReferenceExpression.Create($"{ContainerRegistryUrl}");

    IAzureContainerRegistryResource? IAzureComputeEnvironmentResource.ContainerRegistry => ContainerRegistry;

    private IContainerRegistry? GetContainerRegistry()
    {
        // Check for explicit container registry reference annotation
        if (this.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var annotation))
        {
            return annotation.Registry;
        }

        // Fall back to default container registry
        return DefaultContainerRegistry;
    }

    /// <summary>
    /// Gets the Azure Container Registry resource used by this Azure Container App Environment resource.
    /// </summary>
    public AzureContainerRegistryResource? ContainerRegistry
    {
        get
        {
            var registry = GetContainerRegistry();

            if (registry is null)
            {
                return null;
            }

            if (registry is not AzureContainerRegistryResource azureRegistry)
            {
                throw new InvalidOperationException(
                    $"The container registry configured for the Azure Container App Environment '{Name}' is not an Azure Container Registry. " +
                    $"Only Azure Container Registry resources are supported. Use '.WithAzureContainerRegistry()' to configure an Azure Container Registry.");

            }

            return azureRegistry;
        }
    }

#pragma warning disable CS0618 // Type or member is obsolete
    ReferenceExpression IAzureContainerRegistry.ManagedIdentityId => ReferenceExpression.Create($"{ContainerRegistryManagedIdentityId}");
#pragma warning restore CS0618 // Type or member is obsolete

    /// <inheritdoc/>
    [Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
    {
        var resource = endpointReference.Resource;

        var builder = new ReferenceExpressionBuilder();
        builder.AppendLiteral(resource.Name.ToLowerInvariant());
        if (!endpointReference.EndpointAnnotation.IsExternal)
        {
            builder.AppendLiteral(".internal");
        }
        builder.Append($".{ContainerAppDomain}");

        return builder.Build();
    }

    internal BicepOutputReference GetVolumeStorage(IResource resource, ContainerMountAnnotation volume, int volumeIndex)
    {
        var prefix = volume.Type switch
        {
            ContainerMountType.BindMount => "bindmounts",
            ContainerMountType.Volume => "volumes",
            _ => throw new NotSupportedException()
        };

        // REVIEW: Should we use the same naming algorithm as azd?
        // Normalize the resource name to ensure it's compatible with Bicep identifiers (only letters, numbers, and underscores)
        var normalizedResourceName = Infrastructure.NormalizeBicepIdentifier(resource.Name);
        var outputName = $"{prefix}_{normalizedResourceName}_{volumeIndex}";

        if (!VolumeNames.TryGetValue(outputName, out var volumeName))
        {
            volumeName = (resource, volume, volumeIndex, new BicepOutputReference(outputName, this));

            VolumeNames[outputName] = volumeName;
        }

        return volumeName.outputReference;
    }

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if a ContainerAppManagedEnvironment with the same identifier already exists
        var existingCae = resources.OfType<ContainerAppManagedEnvironment>().SingleOrDefault(cae => cae.BicepIdentifier == bicepIdentifier);

        if (existingCae is not null)
        {
            return existingCae;
        }

        // Create and add new resource if it doesn't exist
        // Even though it's a compound resource, we'll only expose the managed environment
        var cae = ContainerAppManagedEnvironment.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            cae))
        {
            cae.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(cae);
        return cae;
    }
}
