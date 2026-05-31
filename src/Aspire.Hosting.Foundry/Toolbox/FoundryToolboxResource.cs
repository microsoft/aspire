// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel;
using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Represents a Microsoft Foundry Toolbox endpoint associated with a Foundry project.
/// </summary>
/// <remarks>
/// Toolboxes are Foundry data-plane resources: there is no ARM/Bicep representation. Each call
/// to <see cref="AgentToolboxes.CreateToolboxVersionAsync(string, IEnumerable{ProjectsAgentTool}, string, IDictionary{string,string}, ToolboxPolicies, System.Threading.CancellationToken)"/>
/// creates a new immutable version of the toolbox. Aspire registers a deploy-time pipeline step
/// that resolves all tool definitions and calls the data plane API once the parent project's
/// endpoint is ready.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class FoundryToolboxResource : Resource, IResourceWithParent<AzureCognitiveServicesProjectResource>, IResourceWithConnectionString
{
    internal const string DefaultApiVersion = "v1";
    internal const string PreviewFeatureHeaderValue = "Toolboxes=V1Preview";
    internal const string AuthorizationScopeValue = "https://ai.azure.com/.default";

    private const string BeforeStartStepName = "before-start";
    private const string RunModeAzureProvisionStepName = "run-mode-azure-provision";
    private const int ProjectEndpointReadinessMaxRetryAttempts = 11;
    private static readonly TimeSpan s_projectEndpointReadinessDelay = TimeSpan.FromSeconds(5);

    private readonly List<FoundryToolboxToolDefinition> _tools = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="FoundryToolboxResource"/> class.
    /// </summary>
    /// <param name="name">The Toolbox name.</param>
    /// <param name="parent">The parent Microsoft Foundry project resource.</param>
    /// <param name="version">The optional Toolbox version to reference.</param>
    public FoundryToolboxResource([ResourceName] string name, AzureCognitiveServicesProjectResource parent, string? version = null)
        : base(name)
    {
        ArgumentNullException.ThrowIfNull(parent);

        Parent = parent;
        Version = version;

        // Register pipeline steps to create a new toolbox version on the Foundry data plane.
        // Mirrors AzurePromptAgentResource: a publish-mode step that always runs and a run-mode
        // step that kicks off the deployment before the application starts.
        Annotations.Add(new PipelineStepAnnotation(context =>
        {
            var steps = new List<PipelineStep>();

            if (context.PipelineContext.ExecutionContext.IsRunMode)
            {
                var beforeStartDeployStep = new PipelineStep
                {
                    Name = $"deploy-{Name}-before-start",
                    Description = $"Deploys toolbox {Name} before the application starts.",
                    Action = DeployBeforeStartAsync,
                    RequiredBySteps = [BeforeStartStepName],
                    Resource = this,
                    DependsOnSteps = [RunModeAzureProvisionStepName]
                };
                steps.Add(beforeStartDeployStep);
            }

            var toolboxDeployStep = new PipelineStep
            {
                Name = $"deploy-{Name}",
                Description = $"Deploys toolbox {Name}.",
                Action = async (stepCtx) =>
                {
                    var version = await DeployAsync(Parent, stepCtx, logRetry: null, stepCtx.CancellationToken).ConfigureAwait(false);
                    stepCtx.ReportingStep.Log(LogLevel.Information,
                        new MarkdownString($"Successfully deployed **{Name}** as Foundry Toolbox (version {version.Version})"));
                    DeployedVersion.Set(version.Version);
                },
                Tags = [WellKnownPipelineTags.DeployCompute],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Resource = this,
                DependsOnSteps = [WellKnownPipelineSteps.DeployPrereq, AzureEnvironmentResource.ProvisionInfrastructureStepName]
            };
            steps.Add(toolboxDeployStep);

            return Task.FromResult<IEnumerable<PipelineStep>>(steps);
        }));

        // In publish mode the Foundry data plane calls back into MCP servers, so any MCP tool that
        // points at a sibling compute resource (project, container, executable, app service, ACA)
        // must have that resource deployed before the toolbox registers the URL. Walk the resolved
        // pipeline graph and wire a tag-based dependency from this resource's deploy-compute step
        // to every referenced compute resource's deploy-compute step.
        Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            var toolboxDeploySteps = context.GetSteps(this, WellKnownPipelineTags.DeployCompute);

            foreach (var referenced in GetMcpReferencedResources(context.Model))
            {
                var referencedDeploySteps = context.GetSteps(referenced, WellKnownPipelineTags.DeployCompute);
                toolboxDeploySteps.DependsOn(referencedDeploySteps);
            }

            return Task.CompletedTask;
        }));
    }

    /// <summary>
    /// Gets the parent Microsoft Foundry project resource.
    /// </summary>
    public AzureCognitiveServicesProjectResource Parent { get; }

    /// <summary>
    /// Gets or sets the Toolbox version to reference. When unset, the default Toolbox version is used.
    /// </summary>
    /// <remarks>
    /// This is the user-pinned version used in the MCP endpoint URI. To read the version that was
    /// produced by the most recent deployment, use <see cref="DeployedVersion"/>.
    /// </remarks>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the API version used by the Toolbox MCP endpoint.
    /// </summary>
    public string ApiVersion { get; set; } = DefaultApiVersion;

    /// <summary>
    /// Gets or sets a description of the toolbox. Persisted as the toolbox version's description in Foundry.
    /// </summary>
    public string Description { get; set; } = "Foundry Toolbox";

    /// <summary>
    /// Gets the metadata to associate with the toolbox version on creation.
    /// </summary>
    public IDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>()
    {
        { "DeployedBy", "Aspire Hosting Framework" },
        { "DeployedOn", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) }
    };

    /// <summary>
    /// The version produced by the most recent deployment. Populated after the deploy pipeline
    /// step runs successfully; empty before that.
    /// </summary>
    public StaticValueProvider<string> DeployedVersion { get; } = new();

    /// <summary>
    /// Gets the tool definitions modeled for this Toolbox.
    /// </summary>
    public IReadOnlyList<FoundryToolboxToolDefinition> Tools => _tools;

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
        _tools.Add(tool);
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        // Each connection property maps to an env var prefixed with the toolbox resource name
        // (e.g. FIELD_TOOLS_URI). This lets multiple toolboxes coexist on the same consumer without
        // colliding. Apps that only consume a single toolbox should prefer the canonical Foundry
        // env vars emitted by the specialized WithReference overload (FOUNDRY_AGENT_TOOLBOX_*) so
        // the same code path works both locally and in the Foundry hosted-agent runtime.
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

    /// <summary>
    /// Deploys the toolbox to the given Microsoft Foundry project, creating a new immutable toolbox
    /// version via the data plane.
    /// </summary>
    private async Task<ToolboxVersion> DeployAsync(
        AzureCognitiveServicesProjectResource project,
        PipelineStepContext context,
        Action<string>? logRetry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);

        var projectEndpoint = await project.Endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(projectEndpoint))
        {
            throw new InvalidOperationException($"Project '{project.Name}' does not have a valid endpoint.");
        }

        var tokenCredentialProvider = context.Services.GetRequiredService<ITokenCredentialProvider>();
        var credential = tokenCredentialProvider.TokenCredential;

        // Resolve each tool definition to its SDK shape.
        var tools = new List<ProjectsAgentTool>(_tools.Count);
        foreach (var tool in _tools)
        {
            var agentTool = await tool.ToProjectsAgentToolAsync(cancellationToken).ConfigureAwait(false);
            tools.Add(agentTool);
        }

        var projectClient = new AIProjectClient(new Uri(projectEndpoint), credential);

        var retryPipeline = new ResiliencePipelineBuilder<ToolboxVersion>()
            .AddRetry(new RetryStrategyOptions<ToolboxVersion>
            {
                Delay = s_projectEndpointReadinessDelay,
                MaxRetryAttempts = ProjectEndpointReadinessMaxRetryAttempts,
                ShouldHandle = new PredicateBuilder<ToolboxVersion>()
                    .Handle<ClientResultException>(IsProjectEndpointNotReady),
                OnRetry = retry =>
                {
                    var retryMessage = $"Foundry project endpoint for '{project.Name}' is not ready yet. Retrying toolbox deployment in {s_projectEndpointReadinessDelay.TotalSeconds:n0} seconds ({retry.AttemptNumber + 1}/{ProjectEndpointReadinessMaxRetryAttempts}).";
                    if (logRetry is not null)
                    {
                        logRetry?.Invoke(retryMessage);
                    }
                    else
                    {
                        context.ReportingStep.Log(LogLevel.Warning, retryMessage);
                    }

                    return ValueTask.CompletedTask;
                }
            })
            .Build();

        return await retryPipeline.ExecuteAsync(async ct =>
        {
            // Pass policies: null — this opts in to the V1Preview default policy set. The metadata
            // dictionary copy guards against later mutation of Metadata on the resource between
            // deploys (e.g. when a run-mode deploy redeploys after a publish-mode deploy).
            var result = await projectClient.AgentAdministrationClient
                .GetAgentToolboxes()
                .CreateToolboxVersionAsync(
                    Name,
                    tools,
                    Description,
                    new Dictionary<string, string>(Metadata),
                    policies: null,
                    cancellationToken: ct)
                .ConfigureAwait(false);

            return result.Value;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsProjectEndpointNotReady(ClientResultException ex) =>
        ex.Status == 404 &&
        (ex.Message.Contains("Subdomain does not map to a resource", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("The project does not exist", StringComparison.OrdinalIgnoreCase));

    private Task DeployBeforeStartAsync(PipelineStepContext context)
    {
        if (!context.ExecutionContext.IsRunMode)
        {
            return Task.CompletedTask;
        }

        StartRunModeDeployment(context);

        return Task.CompletedTask;
    }

    private void StartRunModeDeployment(PipelineStepContext context)
    {
        // Fire-and-forget so the application can start before the toolbox finishes deploying.
        // The notification service surfaces the toolbox state in the dashboard while the deploy
        // is in flight. Matches AzurePromptAgentResource.
        var lifetime = context.Services.GetRequiredService<IHostApplicationLifetime>();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);

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
            await notificationService.PublishUpdateAsync(this, s => s with
            {
                State = new("Waiting for project", KnownResourceStateStyles.Info)
            }).ConfigureAwait(false);

            await WaitForProjectAndToolsAsync(notificationService, model, cancellationToken).ConfigureAwait(false);

            await notificationService.PublishUpdateAsync(this, s => s with
            {
                State = new("Deploying toolbox", KnownResourceStateStyles.Info)
            }).ConfigureAwait(false);

            logger.LogInformation("Deploying toolbox '{ToolboxName}' to Foundry project '{ProjectName}'...", Name, Parent.Name);

            var version = await DeployAsync(Parent, context, message => logger.LogWarning("{Message}", message), cancellationToken).ConfigureAwait(false);
            DeployedVersion.Set(version.Version);

            logger.LogInformation("Successfully deployed toolbox '{ToolboxName}' (version {Version})", Name, version.Version);

            await notificationService.PublishUpdateAsync(this, s => s with
            {
                State = new("Running", KnownResourceStateStyles.Success)
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deploy toolbox '{ToolboxName}'", Name);

            await notificationService.PublishUpdateAsync(this, s => s with
            {
                State = new("Failed to deploy", KnownResourceStateStyles.Error)
            }).ConfigureAwait(false);
        }
    }

    private async Task WaitForProjectAndToolsAsync(ResourceNotificationService notificationService, DistributedApplicationModel model, CancellationToken cancellationToken)
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

        // The only sub-resources we need to await are the Azure AI Search project connections
        // backing AzureAISearch toolbox tools. WebSearch needs no provisioning, and MCP tools
        // expose only a URL expression - those are handled separately below.
        var toolConnectionProvisioningTasks = _tools
            .Select(tool => tool switch
            {
                FoundryToolboxAzureAISearchToolDefinition { Connection: IAzureResource searchConnection } => searchConnection.ProvisioningTaskCompletionSource?.Task,
                _ => null
            })
            .OfType<Task>();

        // Wait for any locally-hosted compute resources targeted by MCP tools before we try to
        // register their URLs with Foundry. Restrict to IComputeResource + IResourceWithWaitSupport
        // so we don't block forever on Azure-only or model-only resources that never publish a
        // Running state via the notification service.
        var mcpRunModeWaits = GetMcpReferencedResources(model)
            .Where(r => r is IComputeResource and IResourceWithWaitSupport)
            .Select(r => notificationService.WaitForResourceAsync(r.Name, KnownResourceStates.Running, cancellationToken));

        await Task.WhenAll(toolConnectionProvisioningTasks
            .Select(task => task.WaitAsync(cancellationToken))
            .Concat(mcpRunModeWaits)).ConfigureAwait(false);
    }

    // Returns the distinct set of resources referenced by the MCP tools' endpoint expressions that
    // also exist in the current application model. Restricting to model membership avoids waiting on
    // externally constructed references (e.g. a synthetic EndpointReference produced for test setup)
    // and avoids self-dependencies that would deadlock the pipeline.
    private IEnumerable<IResource> GetMcpReferencedResources(DistributedApplicationModel model)
    {
        var modelResources = new HashSet<IResource>(model.Resources);
        var seen = new HashSet<IResource>();

        foreach (var tool in _tools.OfType<FoundryToolboxMcpToolDefinition>())
        {
            foreach (var referenced in WalkValueReferences(tool.EndpointExpression).OfType<IResource>())
            {
                if (ReferenceEquals(referenced, this))
                {
                    continue;
                }

                if (!modelResources.Contains(referenced))
                {
                    continue;
                }

                if (seen.Add(referenced))
                {
                    yield return referenced;
                }
            }
        }
    }

    // Depth-first walk of every object reachable via IValueWithReferences. Used to surface the
    // concrete IResource instances that back an arbitrary ReferenceExpression so we can wire
    // pipeline dependencies and run-mode waits regardless of how the user composed the expression
    // (raw EndpointReference, EndpointReferenceExpression, nested ReferenceExpression, etc.).
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

            if (current is IValueWithReferences withRefs)
            {
                foreach (var reference in withRefs.References)
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
