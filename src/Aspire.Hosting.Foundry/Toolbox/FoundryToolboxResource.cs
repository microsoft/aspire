// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel.Primitives;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Azure.AI.Projects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Represents a Microsoft Foundry Toolbox endpoint associated with a Foundry project.
/// </summary>
/// <remarks>
/// Toolboxes are Foundry data-plane resources with no ARM or Bicep representation. Aspire
/// reconciles the desired tool definitions after the parent project is provisioned, reuses the
/// current default version when its configuration matches, and promotes a new immutable version
/// when the configuration changes.
/// </remarks>
[AspireExport]
public sealed class FoundryToolboxResource : Resource, IResourceWithParent<AzureCognitiveServicesProjectResource>, IResourceWithConnectionString
{
    internal const string DefaultApiVersion = "v1";
    internal const string PreviewFeatureHeaderValue = "Toolboxes=V1Preview";
    internal const string AuthorizationScopeValue = "https://ai.azure.com/.default";

    private const string BeforeStartStepName = "before-start";
    private readonly List<FoundryToolboxToolDefinition> _tools = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="FoundryToolboxResource"/> class.
    /// </summary>
    /// <param name="name">The Toolbox name.</param>
    /// <param name="parent">The parent Microsoft Foundry project resource.</param>
    /// <param name="version">The optional existing Toolbox version to reference.</param>
    public FoundryToolboxResource(
        [ResourceName] string name,
        AzureCognitiveServicesProjectResource parent,
        string? version = null)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(parent);

        Parent = parent;
        Version = version;

        Annotations.Add(new PipelineStepAnnotation(context =>
        {
            var steps = new List<PipelineStep>();

            if (context.PipelineContext.ExecutionContext.IsRunMode)
            {
                steps.Add(new PipelineStep
                {
                    Name = $"deploy-{Name}-before-start",
                    Description = IsExisting
                        ? $"Validates existing Toolbox {Name} after the application starts."
                        : $"Reconciles Toolbox {Name} after the application starts.",
                    Action = DeployBeforeStartAsync,
                    RequiredBySteps = [BeforeStartStepName],
                    Resource = this,
                    DependsOnSteps = [AzureEnvironmentResource.PrepareResourcesStepName]
                });
            }

            steps.Add(new PipelineStep
            {
                Name = $"deploy-{Name}",
                Description = IsExisting
                    ? $"Validates existing Toolbox {Name}."
                    : $"Reconciles Toolbox {Name}.",
                Action = async stepContext =>
                {
                    var result = await DeployAsync(
                        stepContext,
                        message => stepContext.Logger.LogWarning("{Message}", message),
                        stepContext.CancellationToken).ConfigureAwait(false);
                    stepContext.ReportingStep.Log(
                        LogLevel.Information,
                        new MarkdownString(
                            IsExisting
                                ? $"Successfully validated **{Name}** as an existing Foundry Toolbox (version {result.Version})"
                                : $"Successfully reconciled **{Name}** as Foundry Toolbox (version {result.Version}, action {result.Action})"));
                },
                Tags = [WellKnownPipelineTags.DeployCompute],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Resource = this,
                DependsOnSteps =
                [
                    WellKnownPipelineSteps.DeployPrereq,
                    AzureEnvironmentResource.ProvisionInfrastructureStepName
                ]
            });

            return Task.FromResult<IEnumerable<PipelineStep>>(steps);
        }));

        // The Foundry data plane must be able to reach every sibling compute resource referenced
        // by an MCP endpoint before the Toolbox version is created.
        Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            if (IsExisting)
            {
                return Task.CompletedTask;
            }

            var toolboxDeploySteps = context.GetSteps(this, WellKnownPipelineTags.DeployCompute);

            foreach (var referencedResource in GetMcpReferencedResources(context.Model))
            {
                toolboxDeploySteps.DependsOn(
                    context.GetSteps(referencedResource, WellKnownPipelineTags.DeployCompute));
            }

            return Task.CompletedTask;
        }));
    }

    /// <summary>
    /// Gets the parent Microsoft Foundry project resource.
    /// </summary>
    public AzureCognitiveServicesProjectResource Parent { get; }

    /// <summary>
    /// Gets or sets the Toolbox version used by consumers.
    /// </summary>
    /// <remarks>
    /// When unset, consumers use the default Toolbox endpoint. Set this only to target a specific
    /// existing immutable version for testing. Reconciliation always updates the default version
    /// independently of this consumer-side pin.
    /// </remarks>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the API version used by the Toolbox MCP endpoint.
    /// </summary>
    public string ApiVersion { get; set; } = DefaultApiVersion;

    /// <summary>
    /// Gets or sets the description persisted with each Toolbox version.
    /// </summary>
    public string Description { get; set; } = "Foundry Toolbox";

    /// <summary>
    /// Gets metadata persisted with each Toolbox version.
    /// </summary>
    /// <remarks>
    /// Aspire adds reserved ownership and configuration metadata during deployment. User metadata
    /// participates in change detection and therefore creates a new version when modified.
    /// </remarks>
    public IDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the version selected by the most recent reconciliation.
    /// </summary>
    public StaticValueProvider<string> DeployedVersion { get; } = new();

    /// <summary>
    /// Gets the tool definitions modeled for this Toolbox.
    /// </summary>
    internal IReadOnlyList<FoundryToolboxToolDefinition> Tools => _tools;

    internal bool IsExisting =>
        Annotations.OfType<FoundryToolboxExistingResourceAnnotation>().LastOrDefault() is not null;

    /// <summary>
    /// Gets the Toolbox MCP endpoint URI expression.
    /// </summary>
    public ReferenceExpression UriExpression => Version is { Length: > 0 } version
        ? ReferenceExpression.Create($"{Parent.Endpoint}/toolboxes/{Name}/versions/{version}/mcp?api-version={ApiVersion}")
        : ReferenceExpression.Create($"{Parent.Endpoint}/toolboxes/{Name}/mcp?api-version={ApiVersion}");

    /// <summary>
    /// Gets the connection string expression for the Toolbox MCP endpoint.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => UriExpression;

    internal void AddTool(FoundryToolboxToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (_tools.Any(existing => string.Equals(existing.Name, tool.Name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Toolbox '{Name}' already contains a tool named '{tool.Name}'.");
        }

        _tools.Add(tool);
    }

    internal async Task<FoundryToolboxDeploymentDefinition> CreateDeploymentDefinitionAsync(
        CancellationToken cancellationToken)
    {
        var tools = new List<ResolvedFoundryToolboxTool>(_tools.Count);
        foreach (var tool in _tools)
        {
            tools.Add(await tool.ResolveAsync(cancellationToken).ConfigureAwait(false));
        }

        return FoundryToolboxDeploymentDefinition.Create(
            Name,
            Description,
            tools,
            new Dictionary<string, string>(Metadata, StringComparer.Ordinal));
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Name", ReferenceExpression.Create($"{Name}"));
        yield return new("ProjectEndpoint", ReferenceExpression.Create($"{Parent.Endpoint}"));
        yield return new("Uri", UriExpression);
        yield return new("ApiVersion", ReferenceExpression.Create($"{ApiVersion}"));
        yield return new("FoundryFeatures", ReferenceExpression.Create($"{PreviewFeatureHeaderValue}"));
        yield return new("AuthorizationScope", ReferenceExpression.Create($"{AuthorizationScopeValue}"));

        if (Version is { Length: > 0 } version)
        {
            yield return new("Version", ReferenceExpression.Create($"{version}"));
        }
    }

    private async Task<FoundryToolboxReconcileResult> DeployAsync(
        PipelineStepContext context,
        Action<string> logRetry,
        CancellationToken cancellationToken)
    {
        var administration = await CreateAdministrationAsync(
            context,
            logRetry,
            cancellationToken).ConfigureAwait(false);

        if (IsExisting)
        {
            var version = await new FoundryToolboxExistingResourceValidator(administration)
                .ValidateAsync(Name, Version, cancellationToken).ConfigureAwait(false);
            DeployedVersion.Set(version);

            return new(version, FoundryToolboxReconcileAction.ValidatedExisting);
        }

        var definition = await CreateDeploymentDefinitionAsync(cancellationToken).ConfigureAwait(false);
        var result = await new FoundryToolboxReconciler(administration)
            .ReconcileAsync(definition, cancellationToken).ConfigureAwait(false);
        DeployedVersion.Set(result.Version);

        return result;
    }

    private async Task<IFoundryToolboxAdministration> CreateAdministrationAsync(
        PipelineStepContext context,
        Action<string> logRetry,
        CancellationToken cancellationToken)
    {
        var projectEndpoint = await Parent.Endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (!Uri.TryCreate(projectEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"Foundry project '{Parent.Name}' did not resolve to an absolute HTTPS endpoint.");
        }

        endpoint = new Uri(endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/'));

        var administration = context.Services.GetService<IFoundryToolboxAdministration>();
        if (administration is null)
        {
            var credential = context.Services.GetRequiredService<ITokenCredentialProvider>().TokenCredential;
            var clientOptions = new AIProjectClientOptions();
            clientOptions.AddPolicy(new FoundryToolboxFeaturesPolicy(), PipelinePosition.PerCall);
            var projectClient = new AIProjectClient(endpoint, credential, clientOptions);
            administration = new AzureFoundryToolboxAdministration(
                projectClient.AgentAdministrationClient.GetAgentToolboxes(),
                logRetry);
        }

        return administration;
    }

    private Task DeployBeforeStartAsync(PipelineStepContext context)
    {
        if (context.ExecutionContext.IsRunMode)
        {
            StartRunModeDeployment(context);
        }

        return Task.CompletedTask;
    }

    private void StartRunModeDeployment(PipelineStepContext context)
    {
        // MCP endpoints can reference local compute that starts after the before-start pipeline.
        // Reconcile in the background so the application can start and make those endpoints reachable.
        var lifetime = context.Services.GetRequiredService<IHostApplicationLifetime>();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken,
            lifetime.ApplicationStopping);

        _ = Task.Run(async () =>
        {
            try
            {
                await DeployForRunModeAsync(context, linkedCts.Token).ConfigureAwait(false);
            }
            finally
            {
                linkedCts.Dispose();
            }
        }, CancellationToken.None);
    }

    private async Task DeployForRunModeAsync(
        PipelineStepContext context,
        CancellationToken cancellationToken)
    {
        var notificationService = context.Services.GetRequiredService<ResourceNotificationService>();
        var model = context.Services.GetRequiredService<DistributedApplicationModel>();
        var logger = context.Services.GetRequiredService<ResourceLoggerService>().GetLogger(this);

        try
        {
            await notificationService.PublishUpdateAsync(this, snapshot => snapshot with
            {
                State = new("Waiting for dependencies", KnownResourceStateStyles.Info)
            }).ConfigureAwait(false);

            await WaitForProjectAndToolsAsync(
                notificationService,
                model,
                cancellationToken).ConfigureAwait(false);

            await notificationService.PublishUpdateAsync(this, snapshot => snapshot with
            {
                State = new(
                    IsExisting ? "Validating existing Toolbox" : "Reconciling Toolbox",
                    KnownResourceStateStyles.Info)
            }).ConfigureAwait(false);

            var result = await DeployAsync(
                context,
                message => logger.LogWarning("{Message}", message),
                cancellationToken).ConfigureAwait(false);

            if (IsExisting)
            {
                logger.LogInformation(
                    "Validated existing Toolbox '{ToolboxName}' at version {Version}.",
                    Name,
                    result.Version);
            }
            else
            {
                logger.LogInformation(
                    "Reconciled Toolbox '{ToolboxName}' at version {Version} with action {Action}.",
                    Name,
                    result.Version,
                    result.Action);
            }

            await notificationService.PublishUpdateAsync(this, snapshot => snapshot with
            {
                State = new(KnownResourceStates.Running, KnownResourceStateStyles.Success),
                Properties =
                [
                    new("Toolbox version", result.Version),
                    new(IsExisting ? "Validation action" : "Reconciliation action", result.Action.ToString())
                ]
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsExisting)
            {
                logger.LogError(ex, "Failed to validate existing Toolbox '{ToolboxName}'.", Name);
            }
            else
            {
                logger.LogError(ex, "Failed to reconcile Toolbox '{ToolboxName}'.", Name);
            }

            await notificationService.PublishUpdateAsync(this, snapshot => snapshot with
            {
                State = new(KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error)
            }).ConfigureAwait(false);
        }
    }

    private async Task WaitForProjectAndToolsAsync(
        ResourceNotificationService notificationService,
        DistributedApplicationModel model,
        CancellationToken cancellationToken)
    {
        if (Parent is IAzureResource { ProvisioningTaskCompletionSource: { } projectProvisioning })
        {
            await projectProvisioning.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await notificationService.WaitForResourceAsync(
                Parent.Name,
                KnownResourceStates.Running,
                cancellationToken).ConfigureAwait(false);
        }

        if (IsExisting)
        {
            return;
        }

        var connectionProvisioningTasks = _tools
            .OfType<FoundryToolboxAzureAISearchToolDefinition>()
            .Select(tool => tool.Connection.ProvisioningTaskCompletionSource?.Task)
            .OfType<Task>()
            .Select(task => task.WaitAsync(cancellationToken));

        var mcpResourceWaits = GetMcpReferencedResources(model)
            .Where(resource => resource is IComputeResource and IResourceWithWaitSupport)
            .Select(resource => notificationService.WaitForResourceAsync(
                resource.Name,
                KnownResourceStates.Running,
                cancellationToken));

        await Task.WhenAll(connectionProvisioningTasks.Concat(mcpResourceWaits)).ConfigureAwait(false);
    }

    private IEnumerable<IResource> GetMcpReferencedResources(DistributedApplicationModel model)
    {
        // Publish transformations can replace a referenced compute resource while endpoint
        // expressions still point at the original instance. Match by resource name so the
        // Toolbox deploy step retains its dependency on the replacement resource.
        var modelResourceNames = new HashSet<string>(
            model.Resources.Select(resource => resource.Name),
            StringComparers.ResourceName);
        var seenResourceNames = new HashSet<string>(StringComparers.ResourceName);

        foreach (var tool in _tools.OfType<FoundryToolboxMcpToolDefinition>())
        {
            foreach (var referencedResource in WalkValueReferences(tool.EndpointExpression).OfType<IResource>())
            {
                if (!ReferenceEquals(referencedResource, this) &&
                    modelResourceNames.Contains(referencedResource.Name) &&
                    seenResourceNames.Add(referencedResource.Name))
                {
                    yield return referencedResource;
                }
            }
        }
    }

    private static IEnumerable<object> WalkValueReferences(object root)
    {
        var stack = new Stack<object>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            yield return current;

            if (current is IValueWithReferences valueWithReferences)
            {
                foreach (var reference in valueWithReferences.References)
                {
                    if (reference is not null)
                    {
                        stack.Push(reference);
                    }
                }
            }
        }
    }
}

internal sealed class FoundryToolboxExistingResourceAnnotation : IResourceAnnotation;

internal sealed class FoundryToolboxFeaturesPolicy : PipelinePolicy
{
    private const string HeaderName = "Foundry-Features";

    public override void Process(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        message.Request.Headers.Add(HeaderName, FoundryToolboxResource.PreviewFeatureHeaderValue);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        message.Request.Headers.Add(HeaderName, FoundryToolboxResource.PreviewFeatureHeaderValue);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }
}
