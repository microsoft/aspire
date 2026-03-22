// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// A Microsoft Foundry hosted agent resource.
/// </summary>
public class AzureHostedAgentResource : Resource, IComputeResource, IResourceWithEnvironment
{
    /// <summary>
    /// Creates a new instance of the <see cref="AzureHostedAgentResource"/> class.
    /// </summary>
    public AzureHostedAgentResource([ResourceName] string name, IResource target, Action<HostedAgentConfiguration>? configure = null) : base(name)
    {
        ArgumentNullException.ThrowIfNull(target);
        Target = target;
        Configure = configure;
        Annotations.Add(new ManifestPublishingCallbackAnnotation(PublishAsync));
        // Set up steps for deploying this particular hosted agent
        Annotations.Add(new PipelineStepAnnotation(async (ctx) =>
        {
            List<PipelineStep> steps = [];
            var deploymentAnnotation = Target.GetDeploymentTargetAnnotation() ?? throw new InvalidOperationException($"Deployment target annotation is required on resource '{Target.Name}' to deploy as hosted agent.");
            var project = deploymentAnnotation.ComputeEnvironment as AzureCognitiveServicesProjectResource
                ?? throw new InvalidOperationException($"Compute environment for resource '{Target.Name}' must be an AzureCognitiveServicesProjectResource to deploy as hosted agent.");

            // Create a step to deploy container as agent
            var agentDeployStep = new PipelineStep
            {
                Name = $"deploy-{Name}",
                Action = async (ctx) =>
                {
                    var version = await DeployAsync(ctx, project).ConfigureAwait(false);
#pragma warning disable CS0618
                    ctx.ReportingStep.Log(LogLevel.Information, $"Successfully deployed **{Name}** as Hosted Agent (version {version})", enableMarkdown: true);
#pragma warning restore CS0618
                    Version.Set(version.Version);
                },
                Tags = [WellKnownPipelineTags.DeployCompute],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Resource = this,
                DependsOnSteps = [WellKnownPipelineSteps.DeployPrereq, AzureEnvironmentResource.ProvisionInfrastructureStepName]
            };
            steps.Add(agentDeployStep);

            return steps;
        }));

        // Wire up pipeline steps we introduced above
        Annotations.Add(new PipelineConfigurationAnnotation(async (context) =>
        {
            // BuildCompute = build Docker images, so do that before pushing
            context.GetSteps(Target, WellKnownPipelineTags.BuildCompute).RequiredBy(context.GetSteps(Target, WellKnownPipelineTags.PushContainerImage));

            var agentDeployStep = context.GetSteps(this, WellKnownPipelineTags.DeployCompute);

            // The app deployment should depend on push steps from the target resource
            var pushSteps = context.GetSteps(Target, WellKnownPipelineTags.PushContainerImage);
            agentDeployStep.DependsOn(pushSteps);
        }));
    }

    /// <summary>
    /// Configuration action to customize the hosted agent definition during deployment.
    /// </summary>
    public Action<HostedAgentConfiguration>? Configure { get; set; }

    /// <summary>
    /// Once deployed, the version that is assigned to this hosted agent.
    /// </summary>
    public StaticValueProvider<string> Version { get; } = new();

    /// <summary>
    /// The fully qualified image name for the hosted agent.
    /// </summary>
    public ContainerImageReference Image => new(Target);

    /// <summary>
    /// The target containerized workload that this hosted agent deploys.
    /// </summary>
    public IResource Target { get; }

    /// <summary>
    /// Convert all dynamic values into concrete values for deployment.
    /// </summary>
    public async Task<HostedAgentConfiguration> ToHostedAgentConfigurationAsync(PipelineStepContext context)
    {
        var imageName = await ((IValueProvider)Image).GetValueAsync(context.CancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(imageName))
        {
            throw new InvalidOperationException($"Container image for hosted agent '{Name}' could not be resolved.");
        }

        var def = new HostedAgentConfiguration(imageName)
        {
            // ProcessEnvironmentVariableValuesAsync does not resolve values properly in the deploy context
            EnvironmentVariables = await GetResolvedEnvironmentVariablesAsync(context.ExecutionContext, Target, context.Logger, context.CancellationToken).ConfigureAwait(false),
        };
        if (Configure is not null)
        {
            Configure(def);
        }
        return def;
    }

    /// <summary>
    /// Publishes the hosted agent during the manifest publishing phase.
    /// </summary>
    public async Task PublishAsync(ManifestPublishingContext ctx)
    {
        // Write agent manifest
        ctx.Writer.WriteString("type", "azure.ai.agent.v0");
        ctx.Writer.WriteStartObject("definition");
        ctx.Writer.WriteString("kind", "hosted");
        ctx.Writer.WriteString("target", Target.Name);
        ctx.Writer.WriteEndObject(); // definition
        ctx.TryAddDependentResources(Target);
    }

    /// <summary>
    /// Deploys the specified agent to the given Microsoft Foundry project.
    /// </summary>
    public async Task<AgentVersion> DeployAsync(PipelineStepContext context, AzureCognitiveServicesProjectResource project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var projectEndpoint = await project.Endpoint.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(projectEndpoint))
        {
            throw new InvalidOperationException($"Project '{project.Name}' does not have a valid connection string.");
        }
        var def = await ToHostedAgentConfigurationAsync(context).ConfigureAwait(false);
        var projectClient = new AIProjectClient(new Uri(projectEndpoint), new DefaultAzureCredential());
        var result = await projectClient.Agents.CreateAgentVersionAsync(
            Name,
            def.ToAgentVersionCreationOptions(),
            context.CancellationToken
        ).ConfigureAwait(false);
        return result.Value;
    }

    internal static async Task<Dictionary<string, string>> GetResolvedEnvironmentVariablesAsync(
        DistributedApplicationExecutionContext context,
        IResource resource,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var collectedEnvVars = new Dictionary<string, object>();
        if (resource.TryGetEnvironmentVariables(out var callbacks))
        {
            var envContext = new EnvironmentCallbackContext(context, resource, collectedEnvVars, cancellationToken)
            {
                Logger = logger
            };

            foreach (var callback in callbacks)
            {
                await callback.Callback(envContext).ConfigureAwait(false);
            }
        }
        if (resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var identityAnnotation))
        {
            collectedEnvVars["AZURE_CLIENT_ID"] = identityAnnotation.IdentityResource.ClientId;
            collectedEnvVars["AZURE_TOKEN_CREDENTIALS"] = "ManagedIdentityCredential";
        }
        var resolvedEnvVars = new Dictionary<string, string>();
        foreach (var (key, value) in collectedEnvVars)
        {
            switch (value)
            {
                case null:
                    resolvedEnvVars[key] = string.Empty;
                    break;
                case string s:
                    resolvedEnvVars[key] = s;
                    break;
                case IValueProvider provider:
                    resolvedEnvVars[key] = await provider.GetValueAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
                    break;
                case IFormattable f:
                    resolvedEnvVars[key] = f.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    logger.LogWarning("Environment variable '{Key}' for resource '{Name}' has unknown value of type '{type}' and will be skipped.", key, resource.Name, value.GetType().FullName);
                    break;
            }
        }
        return resolvedEnvVars;
    }
}

/// <summary>
/// A static value provider that returns a fixed value once it's been set.
/// </summary>

public class StaticValueProvider<T> : IValueProvider, IManifestExpressionProvider
{
    private T? _value;
    private bool _isSet;

    /// <inheritdoc/>
    public string ValueExpression => "{value}";

    /// <summary>
    /// Sets the value of the provider.
    /// </summary>
    public void Set(T value)
    {
        if (_isSet)
        {
            throw new InvalidOperationException($"Value has already been set.");
        }
        _value = value;
        _isSet = true;
    }

    /// <summary>
    /// Creates a new instance of the <see cref="StaticValueProvider{T}"/> class.
    /// </summary>
    public StaticValueProvider()
    {
        _isSet = false;
    }

    /// <summary>
    /// Creates a new instance of the <see cref="StaticValueProvider{T}"/> class.
    /// </summary>
    public StaticValueProvider(T value)
    {
        _value = value;
        _isSet = true;
    }

    /// <inheritdoc/>
    public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        if (_isSet == false)
        {
            throw new InvalidOperationException("Value for provider has not been set.");
        }
        else
        {
            return ValueTask.FromResult(_value?.ToString());
        }
    }
}
