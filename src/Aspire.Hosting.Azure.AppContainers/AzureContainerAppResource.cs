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
            // Get the deployment target annotation
            var deploymentTargetAnnotation = targetResource.GetDeploymentTargetAnnotation();
            if (deploymentTargetAnnotation is null)
            {
                return [];
            }

            var steps = new List<PipelineStep>();

            var printResourceSummary = new PipelineStep
            {
                Name = $"print-{targetResource.Name}-summary",
                Description = $"Prints the deployment summary and URL for {targetResource.Name}.",
                Resource = targetResource,
                Action = async ctx =>
                {
                    var containerAppEnv = (AzureContainerAppEnvironmentResource)deploymentTargetAnnotation.ComputeEnvironment!;

                    var domainValue = await containerAppEnv.ContainerAppDomain.GetValueAsync(ctx.CancellationToken).ConfigureAwait(false);
                    var portalLink = await ContainerAppUrls.GetPortalLinkAsync(containerAppEnv, targetResource.Name.ToLowerInvariant(), ctx.CancellationToken).ConfigureAwait(false);

                    if (targetResource.TryGetEndpoints(out var endpoints) && endpoints.Any(e => e.IsExternal))
                    {
                        var endpoint = $"https://{targetResource.Name.ToLowerInvariant()}.{domainValue}";
                        var summaryValue = $"[{endpoint}]({endpoint}) ({portalLink})";

                        ctx.ReportingStep.Log(LogLevel.Information, new MarkdownString($"Successfully deployed **{targetResource.Name}** to {summaryValue}"));
                        ctx.Summary.Add(targetResource.Name, new MarkdownString(summaryValue));
                    }
                    else
                    {
                        var summaryValue = $"No public endpoints ({portalLink})";

                        ctx.ReportingStep.Log(LogLevel.Information, new MarkdownString($"Successfully deployed **{targetResource.Name}** to Azure Container Apps environment **{containerAppEnv.Name}**. {summaryValue}"));
                        ctx.Summary.Add(targetResource.Name, new MarkdownString(summaryValue));
                    }
                },
                Tags = ["print-summary"],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy]
            };

            var deployStep = new PipelineStep
            {
                Name = $"deploy-{targetResource.Name}",
                Description = $"Aggregation step for deploying {targetResource.Name} to Azure Container Apps.",
                Resource = targetResource,
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

            // Ensure summary step runs after provision.
            // Use targetResource because the print step has Resource = targetResource (set in the step annotation above).
            context.GetSteps(targetResource, "print-summary").DependsOn(provisionSteps);
        }));
    }

    /// <summary>
    /// Gets the target resource that this Azure Container App is being created for.
    /// </summary>
    public IResource TargetResource { get; }
}
