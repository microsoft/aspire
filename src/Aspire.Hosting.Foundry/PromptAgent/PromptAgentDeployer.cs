// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// These using directives are flagged as unnecessary by the analyzer but are required for compilation.
#pragma warning disable IDE0005
using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Configuration;
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
    DistributedApplicationExecutionContext executionContext,
    IConfiguration configuration
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

        // Collect all unique tools across agents and set initial state
        var allTools = agents.SelectMany(a => a.Tools).Distinct().ToList();
        foreach (var tool in allTools)
        {
            await notificationService.PublishUpdateAsync(tool, s => s with
            {
                State = new("Waiting", KnownResourceStateStyles.Info)
            }).ConfigureAwait(false);
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

            // Mark all tools attached to this agent as running
            foreach (var tool in agent.Tools)
            {
                await notificationService.PublishUpdateAsync(tool, s => s with
                {
                    State = new("Running", KnownResourceStateStyles.Success)
                }).ConfigureAwait(false);
            }

            // Build the Foundry portal URL for this agent
            var portalUrls = ImmutableArray<UrlSnapshot>.Empty;
            var subscriptionId = configuration["Azure:SubscriptionId"];
            var resourceGroupName = configuration["Azure:ResourceGroup"];
            if (!string.IsNullOrEmpty(subscriptionId) && !string.IsNullOrEmpty(resourceGroupName))
            {
                // Use NameOutputReference to get the actual provisioned Azure names (not the Aspire resource names)
                var foundryAccountName = await project.Parent.NameOutputReference.GetValueAsync(cancellationToken).ConfigureAwait(false);
                var projectNameOutput = await project.NameOutputReference.GetValueAsync(cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(foundryAccountName) && !string.IsNullOrEmpty(projectNameOutput))
                {
                    // Project name output is "accountName/projectName" — extract just the project name
                    var projectName = projectNameOutput.Contains('/')
                        ? projectNameOutput[(projectNameOutput.LastIndexOf('/') + 1)..]
                        : projectNameOutput;
                    var encodedSubscriptionId = AzureCognitiveServicesProjectResource.EncodeSubscriptionId(subscriptionId);
                    var portalUrl = $"https://ai.azure.com/nextgen/r/{encodedSubscriptionId},{resourceGroupName},,{foundryAccountName},{projectName}/build/agents/{agent.Name}/build";
                    portalUrls = [new UrlSnapshot(Name: "Foundry Portal", Url: portalUrl, IsInternal: false)];
                }
            }

            await notificationService.PublishUpdateAsync(agent, s => s with
            {
                State = new("Running", KnownResourceStateStyles.Success),
                Urls = portalUrls
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
