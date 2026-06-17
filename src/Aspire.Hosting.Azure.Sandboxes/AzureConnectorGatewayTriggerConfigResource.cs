// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Sandboxes.Provisioning;
using Aspire.Hosting.Pipelines;
using Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure connector namespace trigger config.
/// </summary>
[AspireExport(ExposeProperties = true)]
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureConnectorGatewayTriggerConfigResource : AzureProvisioningResource, IResourceWithParent<AzureConnectorGatewayResource>
{
    internal const string DefaultState = "Enabled";
    internal const string DefaultHttpMethod = "Post";
    internal const string SandboxProxyManagedIdentityAudience = "https://auth.adcproxy.io/";

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureConnectorGatewayTriggerConfigResource"/> class.
    /// </summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="triggerName">The Azure trigger config name.</param>
    /// <param name="operationName">The connector trigger operation name.</param>
    /// <param name="callbackEndpoint">The sandbox endpoint that receives trigger notifications.</param>
    /// <param name="callbackPath">The optional path appended to the sandbox endpoint URL.</param>
    /// <param name="description">The trigger config description.</param>
    /// <param name="connection">The connector connection used by the trigger.</param>
    /// <param name="triggerParameters">The connector trigger parameters.</param>
    public AzureConnectorGatewayTriggerConfigResource(
        string name,
        string triggerName,
        string operationName,
        EndpointReference callbackEndpoint,
        string? callbackPath,
        string? description,
        AzureConnectorGatewayConnectionResource connection,
        IReadOnlyList<AzureConnectorGatewayTriggerParameter> triggerParameters)
        : base(name, ConfigureTriggerInfrastructure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(callbackEndpoint);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(triggerParameters);

        TriggerName = triggerName;
        OperationName = operationName;
        CallbackEndpoint = callbackEndpoint;
        CallbackPath = NormalizeCallbackPath(callbackPath);
        Description = description;
        Connection = connection;
        TriggerParameters = triggerParameters;

        // AzureProvisioningResource inherits AzureBicepResource, which adds a normal
        // provision-infrastructure step in its constructor. Trigger configs depend on a
        // sandbox callback URL that only exists after the sandbox data-plane deploy step
        // persists endpoint state, so remove the early step and add a late deploy step below.
        foreach (var annotation in Annotations.Where(static annotation => annotation is PipelineStepAnnotation or PipelineConfigurationAnnotation).ToArray())
        {
            Annotations.Remove(annotation);
        }

        // This resource is deploy-time wiring rather than part of the first-pass Azure
        // infrastructure artifact. Keeping it out of generic publish/run provisioning avoids
        // creating a trigger with an empty callback URL before the sandbox exists.
        Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);
        Annotations.Add(new PipelineStepAnnotation(CreatePipelineSteps));
    }

    /// <summary>
    /// Gets the Azure trigger config name.
    /// </summary>
    public string TriggerName { get; }

    /// <summary>
    /// Gets the connector trigger operation name.
    /// </summary>
    public string OperationName { get; }

    /// <summary>
    /// Gets the sandbox callback endpoint.
    /// </summary>
    public EndpointReference CallbackEndpoint { get; }

    /// <summary>
    /// Gets the optional relative callback path appended to <see cref="CallbackEndpoint"/>.
    /// </summary>
    public string? CallbackPath { get; }

    /// <summary>
    /// Gets the trigger description.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Gets the connector trigger parameters.
    /// </summary>
    public IReadOnlyList<AzureConnectorGatewayTriggerParameter> TriggerParameters { get; }

    /// <summary>
    /// Gets the connector connection used by the trigger.
    /// </summary>
    public AzureConnectorGatewayConnectionResource Connection { get; }

    /// <inheritdoc/>
    public AzureConnectorGatewayResource Parent => Connection.Parent;

    internal ReferenceExpression GetCallbackUrlExpression()
    {
        var endpointExpression = CallbackEndpoint.Property(EndpointProperty.Url);
        var endpointResource = CallbackEndpoint.Resource;
        if (endpointResource.GetComputeEnvironment() is not AzureSandboxGroupResource sandboxGroup)
        {
            throw new InvalidOperationException(
                $"Connector trigger '{Name}' targets endpoint '{CallbackEndpoint.EndpointName}' on resource '{endpointResource.Name}', but that resource is not deployed to an Azure sandbox group.");
        }

        var baseUrl = sandboxGroup.GetEndpointPropertyExpression(endpointExpression);
        return CallbackPath is null
            ? baseUrl
            : ReferenceExpression.Create($"{baseUrl}{CallbackPath}");
    }

    private static string? NormalizeCallbackPath(string? callbackPath)
    {
        if (string.IsNullOrWhiteSpace(callbackPath))
        {
            return null;
        }

        var normalizedPath = callbackPath.TrimStart('/');
        return string.IsNullOrWhiteSpace(normalizedPath) ? null : normalizedPath;
    }

    private static void ConfigureTriggerInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        var triggerResource = (AzureConnectorGatewayTriggerConfigResource)infrastructure.AspireResource;
        var gateway = (ConnectorGateway)triggerResource.Parent.AddAsExistingResource(infrastructure);
        var trigger = new ConnectorGatewayTriggerConfig(triggerResource.GetBicepIdentifier())
        {
            Parent = gateway,
            Name = triggerResource.TriggerName,
            OperationName = triggerResource.OperationName,
            State = DefaultState
        };
        if (!string.IsNullOrWhiteSpace(triggerResource.Description))
        {
            trigger.Description = triggerResource.Description;
        }

        trigger.ConnectionDetails.ConnectorName = triggerResource.Connection.ConnectorName;
        trigger.ConnectionDetails.ConnectionName = triggerResource.Connection.ConnectionName;
        trigger.NotificationDetails.CallbackUrl = triggerResource.GetCallbackUrlExpression().AsProvisioningParameter(infrastructure, $"{triggerResource.Name}_callbackUrl");
        trigger.NotificationDetails.HttpMethod = DefaultHttpMethod;
        trigger.NotificationDetails.Authentication.Type = "ManagedServiceIdentity";
        trigger.NotificationDetails.Authentication.Audience = SandboxProxyManagedIdentityAudience;

        // The triggerConfig ARM schema is still preview-only. Keep the emitted
        // metadata intentionally narrow and derived from the target endpoint so
        // users can inspect which sandbox callback the trigger was bound to.
        trigger.Metadata["sandboxResource"] = triggerResource.CallbackEndpoint.Resource.Name;
        trigger.Metadata["sandboxEndpoint"] = triggerResource.CallbackEndpoint.EndpointName;

        foreach (var parameter in triggerResource.TriggerParameters)
        {
            trigger.Parameters.Add(new ConnectorGatewayTriggerParameter
            {
                Name = parameter.Name,
                Value = parameter.Value
            });
        }

        infrastructure.Add(trigger);
    }

    private IEnumerable<PipelineStep> CreatePipelineSteps(PipelineStepFactoryContext factoryContext)
    {
        var sandboxContainer = GetCallbackSandboxContainerOrDefault();
        if (sandboxContainer is null)
        {
            // The sandbox group prepares hidden deployment targets in its BeforeStart pipeline step.
            // Pipeline step annotations are collected before any BeforeStart step executes, so this
            // late trigger step must opt out during that first collection pass. Deploy collects
            // annotations again after BeforeStart, at which point the sandbox target exists and the
            // trigger can safely depend on the sandbox deploy step.
            return [];
        }

        ProvisioningTaskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        return
        [
            new PipelineStep
            {
                Name = $"provision-{Name}",
                Description = $"Provisions connector trigger config '{TriggerName}' after sandbox deployment.",
                Action = ProvisionTriggerConfigAsync,
                Tags = [WellKnownPipelineTags.ProvisionInfrastructure],
                DependsOnSteps =
                [
                    AzureEnvironmentResource.CreateProvisioningContextStepName,
                    AzureSandboxContainerDeployment.GetDeployStepName(sandboxContainer)
                ],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Resource = this
            }
        ];
    }

    private AzureSandboxContainerResource? GetCallbackSandboxContainerOrDefault()
    {
        var endpointResource = CallbackEndpoint.Resource;
        if (endpointResource.GetComputeEnvironment() is not AzureSandboxGroupResource sandboxGroup)
        {
            throw new InvalidOperationException($"Connector trigger '{Name}' targets endpoint '{CallbackEndpoint.EndpointName}' on resource '{endpointResource.Name}', but that resource is not deployed to an Azure sandbox group.");
        }

        if (endpointResource.GetDeploymentTargetAnnotation(sandboxGroup)?.DeploymentTarget is AzureSandboxContainerResource sandboxContainer)
        {
            return sandboxContainer;
        }

        return null;
    }

    private async Task ProvisionTriggerConfigAsync(PipelineStepContext context)
    {
        if (ProvisioningTaskCompletionSource?.Task.IsCompleted == true)
        {
            context.Logger.LogDebug("Connector trigger config {ResourceName} is already provisioned. Skipping provisioning.", Name);
            return;
        }

        var options = context.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>();
        ProvisioningBuildOptions = options.Value.ProvisioningBuildOptions;

        var bicepProvisioner = context.Services.GetRequiredService<IBicepProvisioner>();
        var azureEnvironment = context.Model.Resources.OfType<AzureEnvironmentResource>().FirstOrDefault() ??
            throw new InvalidOperationException("AzureEnvironmentResource must be present in the application model.");
        var provisioningContext = await azureEnvironment.ProvisioningContextTask.Task.ConfigureAwait(false);

        var resourceTask = await context.ReportingStep
            .CreateTaskAsync(new MarkdownString($"Deploying connector trigger **{Name}**"), context.CancellationToken)
            .ConfigureAwait(false);

        await using (resourceTask.ConfigureAwait(false))
        {
            try
            {
                if (await bicepProvisioner.ConfigureResourceAsync(this, context.CancellationToken).ConfigureAwait(false))
                {
                    ProvisioningTaskCompletionSource?.TrySetResult();
                    await resourceTask.CompleteAsync(
                        new MarkdownString($"Using existing deployment for connector trigger **{Name}**"),
                        CompletionState.Completed,
                        context.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await bicepProvisioner.GetOrCreateResourceAsync(this, provisioningContext, context.CancellationToken).ConfigureAwait(false);
                    ProvisioningTaskCompletionSource?.TrySetResult();
                    await resourceTask.CompleteAsync(
                        new MarkdownString($"Successfully provisioned connector trigger **{Name}**"),
                        CompletionState.Completed,
                        context.CancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                var errorMessage = ex switch
                {
                    RequestFailedException requestEx => $"Deployment failed: {AzureBicepResource.ExtractDetailedErrorMessage(requestEx)}",
                    _ => $"Deployment failed: {ex.Message}"
                };
                ProvisioningTaskCompletionSource?.TrySetException(ex);
                await resourceTask.CompleteAsync(
                    new MarkdownString($"Failed to provision connector trigger **{Name}**: {errorMessage}"),
                    CompletionState.CompletedWithError,
                    context.CancellationToken).ConfigureAwait(false);
                throw new ProvisioningFailedException(errorMessage, ex);
            }
        }
    }
}
