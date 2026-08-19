// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure.AppContainers;

/// <summary>
/// Represents an Azure Container App resource.
/// </summary>
public class AzureContainerAppResource : AzureProvisioningResource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureContainerAppResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource in the Aspire application model.</param>
    /// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
    /// <param name="targetResource">The target compute resource that this Azure Container App is being created for.</param>
    public AzureContainerAppResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure, IResource targetResource)
        : base(name, configureInfrastructure)
    {
        TargetResource = targetResource;

        // Add pipeline step annotation for deploy
        Annotations.Add(new PipelineStepAnnotation((factoryContext) =>
        {
            // Get the deployment target annotation for this stamp. A stamped target resource has one
            // deployment target per compute environment, so the environment has to narrow the lookup.
            var deploymentTargetAnnotation = targetResource.GetDeploymentTargetAnnotation(ComputeEnvironment);
            if (deploymentTargetAnnotation is null)
            {
                return [];
            }

            var stampName = targetResource.GetStampQualifiedName(ComputeEnvironment);

            var steps = new List<PipelineStep>();

            var printResourceSummary = new PipelineStep
            {
                Name = $"print-{stampName}-summary",
                Description = $"Prints the deployment summary and URL for {stampName}.",
                Action = async ctx =>
                {
                    var containerAppEnv = (AzureContainerAppEnvironmentResource)deploymentTargetAnnotation.ComputeEnvironment!;

                    var domainValue = await containerAppEnv.ContainerAppDomain.GetValueAsync(ctx.CancellationToken).ConfigureAwait(false);
                    var portalLink = await ContainerAppUrls.GetPortalLinkAsync(containerAppEnv, stampName.ToLowerInvariant(), ctx.CancellationToken).ConfigureAwait(false);

                    if (targetResource.TryGetEndpoints(out var endpoints) && endpoints.Any(e => e.IsExternal))
                    {
                        var endpoint = $"https://{stampName.ToLowerInvariant()}.{domainValue}";
                        var summaryValue = $"[{endpoint}]({endpoint}) ({portalLink})";

                        ctx.ReportingStep.Log(LogLevel.Information, new MarkdownString($"Successfully deployed **{stampName}** to {summaryValue}"));
                        ctx.Summary.Add(stampName, new MarkdownString(summaryValue));
                    }
                    else
                    {
                        var summaryValue = $"No public endpoints ({portalLink})";

                        ctx.ReportingStep.Log(LogLevel.Information, new MarkdownString($"Successfully deployed **{stampName}** to Azure Container Apps environment **{containerAppEnv.Name}**. {summaryValue}"));
                        ctx.Summary.Add(stampName, new MarkdownString(summaryValue));
                    }
                },
                Tags = ["print-summary"],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy]
            };

            var deployStep = new PipelineStep
            {
                Name = $"deploy-{stampName}",
                Description = $"Aggregation step for deploying {stampName} to Azure Container Apps.",
                Action = _ => Task.CompletedTask,
                Tags = [WellKnownPipelineTags.DeployCompute]
            };

            deployStep.DependsOn(printResourceSummary);

            steps.Add(printResourceSummary);
            steps.Add(deployStep);

            return steps;
        }));

        // Add pipeline configuration annotation to wire up dependencies
        Annotations.Add(new PipelineConfigurationAnnotation((context) =>
        {
            var provisionSteps = context.GetSteps(this, WellKnownPipelineTags.ProvisionInfrastructure);

            // The app deployment should depend on push steps from the target resource
            var pushSteps = context.GetSteps(targetResource, WellKnownPipelineTags.PushContainerImage);
            provisionSteps.DependsOn(pushSteps);

            // Ensure summary step runs after provision
            context.GetSteps(this, "print-summary").DependsOn(provisionSteps);
        }));
    }

    /// <summary>
    /// Gets the target resource that this Azure Container App is being created for.
    /// </summary>
    public IResource TargetResource { get; }

    /// <summary>
    /// Gets the compute environment this container app is deployed to.
    /// </summary>
    /// <remarks>
    /// A target resource deployed as several regional stamps produces one <see cref="AzureContainerAppResource"/>
    /// per compute environment. This identifies which of those stamps this instance represents, so that
    /// deployment-target lookups and generated names stay unambiguous.
    /// </remarks>
    public IComputeEnvironmentResource? ComputeEnvironment { get; init; }
}
