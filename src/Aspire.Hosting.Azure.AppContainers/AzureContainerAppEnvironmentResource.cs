// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning;
using Azure.Provisioning.AppContainers;
using Azure.Provisioning.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

            // Add prepare-azure-container-apps-{name} step that materializes deployment targets
            // for compute resources targeted to this environment. Runs during BeforeStart so that
            // BeforeStartEvent subscribers (and downstream code) can observe the deployment targets.
            var prepareStep = new PipelineStep
            {
                Name = $"prepare-azure-container-apps-{name}",
                Description = $"Prepares Azure Container Apps deployment targets for {name}.",
                Action = ctx => PrepareDeploymentTargetsAsync(ctx),
                DependsOnSteps = [AzureEnvironmentResource.PrepareResourcesStepName, WellKnownPipelineSteps.ValidateComputeEnvironments],
                RequiredBySteps = [WellKnownPipelineSteps.BeforeStart]
            };

            steps.Add(prepareStep);

            if (EnableDashboard)
            {
                // The dashboard URL is only meaningful when the dashboard is provisioned.
                // Avoid registering the summary step when WithDashboard(false) is used, otherwise
                // we would emit a link to a dashboard that does not exist in the environment.
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
            }

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

        context.Summary.Add("📊 Dashboard", new MarkdownString($"[{dashboardUrl}]({dashboardUrl})"));

        await context.ReportingStep.CompleteAsync(
            new MarkdownString($"Dashboard available at [{dashboardUrl}]({dashboardUrl})"),
            CompletionState.Completed,
            context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Materializes Azure Container Apps deployment targets for compute resources targeted to this
    /// environment. Invoked by the per-environment <c>prepare-azure-container-apps-{name}</c>
    /// pipeline step, which runs after <c>azure-prepare-resources</c> so role-assignment resources
    /// are present in the model and can be referenced by the generated
    /// <see cref="DeploymentTargetAnnotation"/> instances.
    /// </summary>
    private async Task PrepareDeploymentTargetsAsync(PipelineStepContext context)
    {
        var appModel = context.Model;
        var executionContext = context.ExecutionContext;
        var services = context.Services;
        var cancellationToken = context.CancellationToken;

        if (!executionContext.IsPublishMode)
        {
            return;
        }

        // Remove the default container registry from the model if an explicit registry is configured
        if (this.HasAnnotationOfType<ContainerRegistryReferenceAnnotation>() &&
            DefaultContainerRegistry is not null)
        {
            appModel.Resources.Remove(DefaultContainerRegistry);
            DefaultContainerRegistry = null;
        }

        var logger = services.GetRequiredService<ILogger<AzureContainerAppEnvironmentResource>>();
        var options = services.GetRequiredService<IOptions<AzureProvisioningOptions>>();

        var containerAppEnvironmentContext = new ContainerAppEnvironmentContext(
            logger,
            executionContext,
            this,
            services);

        // Container apps targeted to this environment, in model order. Collected so that after all
        // deployment targets are materialized we can chain them into a serial deployment order.
        var targets = new List<IResource>();

        foreach (var r in appModel.GetComputeResources())
        {
            // Skip resources that are explicitly targeted to a different compute environment
            var resourceComputeEnvironment = r.GetComputeEnvironment();
            if (resourceComputeEnvironment is not null && resourceComputeEnvironment != this)
            {
                continue;
            }

            var containerApp = await containerAppEnvironmentContext.CreateContainerAppAsync(r, options.Value, cancellationToken).ConfigureAwait(false);

            // Capture information about the container registry used by the
            // container app environment in the deployment target information
            // associated with each compute resource that needs an image
            r.Annotations.Add(new DeploymentTargetAnnotation(containerApp)
            {
                ContainerRegistry = this,
                ComputeEnvironment = this
            });

            targets.Add(r);
        }

        AddSerialDeploymentOrdering(targets);

        // Log once about all HTTP endpoints upgraded to HTTPS
        containerAppEnvironmentContext.LogHttpsUpgradeIfNeeded();
    }

    // Relationship type used to serialize deployment of container apps that share a managed
    // environment. Defined locally because KnownRelationshipTypes is internal to Aspire.Hosting and
    // not visible to this assembly.
    private const string DependsOnRelationshipType = "DependsOn";

    // Relationship types that express an ordering dependency between resources. These mirror the
    // internal KnownRelationshipTypes values added by WithReference/WaitFor and the DependsOn
    // relationship used to serialize deployment.
    private const string ReferenceRelationshipType = "Reference";
    private const string WaitForRelationshipType = "WaitFor";

    /// <summary>
    /// Chains the container apps targeted to this managed environment into a single serial
    /// deployment order using <c>DependsOn</c> relationships.
    /// </summary>
    /// <remarks>
    /// Azure Container Apps serializes write operations within a single managed environment:
    /// creating or updating two container apps in the same environment concurrently fails with
    /// <c>ContainerAppOperationInProgress</c>. The application model otherwise has no edge telling a
    /// model-graph-driven deployer that these apps must not deploy in parallel. To make the
    /// constraint explicit, the apps are linearized into one total order so that at most one app in
    /// the environment deploys at a time. Existing ordering dependencies (<c>Reference</c>,
    /// <c>WaitFor</c>, or already materialized <c>DependsOn</c>) are honored: the total order is a
    /// topological sort of those dependencies, and a synthetic edge is only added between two
    /// consecutive apps when the existing graph does not already order them, avoiding redundant
    /// relationships and cycles. See
    /// https://github.com/microsoft/aspire/issues/18682.
    /// </remarks>
    private static void AddSerialDeploymentOrdering(IReadOnlyList<IResource> targets)
    {
        if (targets.Count < 2)
        {
            return;
        }

        var comparer = new ResourceNameComparer();

        // dependencies[x] = the apps (within this environment) that x already depends on, so those
        // apps must deploy before x. Only same-environment targets participate; a dependency on a
        // resource outside this set doesn't affect ordering within the environment.
        var targetSet = new HashSet<IResource>(targets, comparer);
        var dependencies = new Dictionary<IResource, HashSet<IResource>>(comparer);
        foreach (var target in targets)
        {
            dependencies[target] = GetExistingDependencies(target, targetSet, comparer);
        }

        // Linearize the apps into a total order consistent with the existing dependencies. Model
        // order is used as a stable tie-break so the chain is deterministic.
        var order = TopologicalSort(targets, dependencies, comparer);

        // Add a DependsOn edge between each consecutive pair, unless the existing dependency graph
        // already orders the later app after the earlier one. Because the pairs follow the
        // topological order, the earlier app never already depends on the later one, so no cycle can
        // be introduced.
        for (var i = 1; i < order.Count; i++)
        {
            var previous = order[i - 1];
            var current = order[i];

            if (!DependsOnTransitively(current, previous, dependencies, comparer))
            {
                current.Annotations.Add(new ResourceRelationshipAnnotation(previous, DependsOnRelationshipType));
            }
        }
    }

    /// <summary>
    /// Returns the set of resources in <paramref name="candidates"/> that <paramref name="resource"/>
    /// already depends on via an ordering relationship.
    /// </summary>
    private static HashSet<IResource> GetExistingDependencies(IResource resource, HashSet<IResource> candidates, IEqualityComparer<IResource?> comparer)
    {
        var result = new HashSet<IResource>(comparer);

        if (!resource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships))
        {
            return result;
        }

        foreach (var relationship in relationships)
        {
            if (relationship.Type is not (ReferenceRelationshipType or WaitForRelationshipType or DependsOnRelationshipType))
            {
                continue;
            }

            // A self-relationship does not order the app relative to any other app.
            if (comparer.Equals(relationship.Resource, resource))
            {
                continue;
            }

            if (candidates.Contains(relationship.Resource))
            {
                result.Add(relationship.Resource);
            }
        }

        return result;
    }

    /// <summary>
    /// Produces a topological ordering of <paramref name="targets"/> where each app appears after the
    /// apps it depends on, using model order as a stable tie-break. If the dependencies contain a
    /// cycle (which shouldn't occur for valid models), the remaining apps are appended in model order.
    /// </summary>
    private static List<IResource> TopologicalSort(IReadOnlyList<IResource> targets, Dictionary<IResource, HashSet<IResource>> dependencies, IEqualityComparer<IResource?> comparer)
    {
        var order = new List<IResource>(targets.Count);
        var added = new HashSet<IResource>(comparer);

        while (order.Count < targets.Count)
        {
            IResource? next = null;

            // Pick the first app (in model order) whose remaining dependencies have all been added.
            foreach (var target in targets)
            {
                if (added.Contains(target))
                {
                    continue;
                }

                if (dependencies[target].All(added.Contains))
                {
                    next = target;
                    break;
                }
            }

            if (next is null)
            {
                // A dependency cycle prevents further progress; append the rest in model order so a
                // total order is still produced.
                foreach (var target in targets)
                {
                    if (added.Add(target))
                    {
                        order.Add(target);
                    }
                }

                break;
            }

            added.Add(next);
            order.Add(next);
        }

        return order;
    }

    /// <summary>
    /// Determines whether <paramref name="from"/> already depends on <paramref name="to"/>, directly
    /// or transitively, through the existing dependency graph.
    /// </summary>
    private static bool DependsOnTransitively(IResource from, IResource to, Dictionary<IResource, HashSet<IResource>> dependencies, IEqualityComparer<IResource?> comparer)
    {
        var visited = new HashSet<IResource>(comparer);
        var stack = new Stack<IResource>();
        stack.Push(from);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (!dependencies.TryGetValue(current, out var deps))
            {
                continue;
            }

            foreach (var dep in deps)
            {
                if (comparer.Equals(dep, to))
                {
                    return true;
                }

                if (visited.Add(dep))
                {
                    stack.Push(dep);
                }
            }
        }

        return false;
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

    /// <inheritdoc/>
    [Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public ReferenceExpression GetEndpointPropertyExpression(EndpointReferenceExpression endpointReferenceExpression)
    {
        ArgumentNullException.ThrowIfNull(endpointReferenceExpression);

        var endpointReference = endpointReferenceExpression.Endpoint;
        var property = endpointReferenceExpression.Property;
        var endpoint = endpointReference.EndpointAnnotation;
        var scheme = PreserveHttpEndpoints ? endpoint.UriScheme : "https";
        var port = string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) ? 80 : 443;
        var tlsEnabled = string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) || endpoint.TlsEnabled;
        var host = GetHostAddressExpression(endpointReference);

        return property switch
        {
            EndpointProperty.Url => ReferenceExpression.Create($"{scheme}://{host}"),
            EndpointProperty.Host or EndpointProperty.IPV4Host => host,
            EndpointProperty.Port => ReferenceExpression.Create($"{port.ToString(CultureInfo.InvariantCulture)}"),
            EndpointProperty.TargetPort => endpoint.TargetPort is int targetPort
                ? ReferenceExpression.Create($"{targetPort.ToString(CultureInfo.InvariantCulture)}")
                : ReferenceExpression.Create($"{new ContainerPortReference(endpointReference.Resource)}"),
            EndpointProperty.Scheme => ReferenceExpression.Create($"{scheme}"),
            EndpointProperty.HostAndPort => ReferenceExpression.Create($"{host}:{port.ToString(CultureInfo.InvariantCulture)}"),
            EndpointProperty.TlsEnabled => ReferenceExpression.Create($"{(tlsEnabled ? bool.TrueString : bool.FalseString)}"),
            _ => throw new InvalidOperationException($"The property '{property}' is not supported for the endpoint '{endpoint.Name}'.")
        };
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
