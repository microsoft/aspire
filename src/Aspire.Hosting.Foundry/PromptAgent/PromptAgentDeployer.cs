// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// These using directives are flagged as unnecessary by the analyzer but are required for compilation.
#pragma warning disable IDE0005
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;
#pragma warning restore IDE0005

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Deploys prompt agents to Azure Foundry during <c>aspire run</c> mode.
/// </summary>
/// <remarks>
/// In run mode, pipeline steps are not executed. This deployer subscribes to
/// <see cref="BeforeStartEvent"/> and deploys all <see cref="AzurePromptAgentResource"/>
/// instances after their parent Azure infrastructure finishes provisioning.
/// </remarks>
internal sealed class PromptAgentDeployer(
    ResourceNotificationService notificationService,
    ResourceLoggerService loggerService,
    DistributedApplicationExecutionContext executionContext
) : IDistributedApplicationEventingSubscriber
{
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext _, CancellationToken cancellationToken)
    {
        if (executionContext.IsRunMode)
        {
            eventing.Subscribe<BeforeStartEvent>(OnBeforeStartAsync);
        }

        return Task.CompletedTask;
    }

    private async Task OnBeforeStartAsync(BeforeStartEvent @event, CancellationToken cancellationToken)
    {
        var agents = @event.Model.Resources.OfType<AzurePromptAgentResource>().ToList();
        if (agents.Count == 0)
        {
            return;
        }

        // Fire-and-forget: deploy each agent after its project becomes available
        foreach (var agent in agents)
        {
            _ = DeployAgentAsync(agent, cancellationToken);
        }
    }

    private async Task DeployAgentAsync(AzurePromptAgentResource agent, CancellationToken cancellationToken)
    {
        var logger = loggerService.GetLogger(agent);

        try
        {
            await notificationService.PublishUpdateAsync(agent, s => s with
            {
                State = new("Waiting for project", KnownResourceStateStyles.Info)
            }).ConfigureAwait(false);

            // Wait for the parent project to be fully provisioned
            var project = agent.Project;
            var projectAzureResource = project as IAzureResource;
            if (projectAzureResource?.ProvisioningTaskCompletionSource is not null)
            {
                await projectAzureResource.ProvisioningTaskCompletionSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Fall back to watching resource state
                await notificationService.WaitForResourceAsync(
                    project.Name,
                    KnownResourceStates.Running,
                    cancellationToken
                ).ConfigureAwait(false);
            }

            // Also wait for all tool connection resources to be provisioned
            foreach (var tool in agent.Tools)
            {
                if (tool is BingGroundingToolResource { Connection: IAzureResource bingConn } &&
                    bingConn.ProvisioningTaskCompletionSource is not null)
                {
                    await bingConn.ProvisioningTaskCompletionSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (tool is AzureAISearchToolResource { Connection: IAzureResource searchConn } &&
                    searchConn.ProvisioningTaskCompletionSource is not null)
                {
                    await searchConn.ProvisioningTaskCompletionSource.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await notificationService.PublishUpdateAsync(agent, s => s with
            {
                State = new("Deploying agent", KnownResourceStateStyles.Info)
            }).ConfigureAwait(false);

            logger.LogInformation("Deploying prompt agent '{AgentName}' to Foundry project '{ProjectName}'...", agent.Name, project.Name);

            var version = await agent.DeployAsync(cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Successfully deployed prompt agent '{AgentName}' (version {Version})", agent.Name, version.Version);

            await notificationService.PublishUpdateAsync(agent, s => s with
            {
                State = new("Running", KnownResourceStateStyles.Success)
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to deploy prompt agent '{AgentName}'", agent.Name);

            await notificationService.PublishUpdateAsync(agent, s => s with
            {
                State = new("Failed to deploy", KnownResourceStateStyles.Error)
            }).ConfigureAwait(false);
        }
    }
}
