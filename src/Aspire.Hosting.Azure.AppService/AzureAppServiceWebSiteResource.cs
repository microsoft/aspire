// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure App Service Web Site resource.
/// </summary>
public class AzureAppServiceWebSiteResource : AzureProvisioningResource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureAppServiceWebSiteResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource in the Aspire application model.</param>
    /// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
    /// <param name="targetResource">The target resource that this Azure Web Site is being created for.</param>
    public AzureAppServiceWebSiteResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure, IResource targetResource)
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
                Action = async ctx =>
                {
                    var computerEnv = (AzureAppServiceEnvironmentResource)deploymentTargetAnnotation.ComputeEnvironment!;
                    string? deploymentSlot = null;

                    if (computerEnv.DeploymentSlot is not null || computerEnv.DeploymentSlotParameter is not null)
                    {
                        deploymentSlot = computerEnv.DeploymentSlotParameter is null ?
                           computerEnv.DeploymentSlot :
                           await computerEnv.DeploymentSlotParameter.GetValueAsync(ctx.CancellationToken).ConfigureAwait(false);
                    }

                    var hostName = await GetAppServiceWebsiteNameAsync(ctx, deploymentSlot).ConfigureAwait(false);
                    var endpoint = $"https://{hostName}.azurewebsites.net";
                    ctx.ReportingStep.Log(LogLevel.Information, new MarkdownString($"Successfully deployed **{targetResource.Name}** to [{endpoint}]({endpoint})"));
                    ctx.Summary.Add(targetResource.Name, endpoint);
                },
                Tags = ["print-summary"],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy]
            };

            var deployStep = new PipelineStep
            {
                Name = $"deploy-{targetResource.Name}",
                Description = $"Aggregation step for deploying {targetResource.Name} to Azure App Service.",
                Action = _ => Task.CompletedTask,
                Tags = [WellKnownPipelineTags.DeployCompute]
            };

            deployStep.DependsOn(printResourceSummary);

            steps.Add(deployStep);
            steps.Add(printResourceSummary);

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
    /// Gets the target resource that this Azure Web Site is being created for.
    /// </summary>
    public IResource TargetResource { get; }

    /// <summary>
    /// Gets the Azure App Service website name, optionally including the deployment slot suffix.
    /// </summary>
    /// <param name="context">The pipeline step context.</param>
    /// <param name="deploymentSlot">The optional deployment slot name to append to the website name.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the website name.</returns>
    private async Task<string> GetAppServiceWebsiteNameAsync(PipelineStepContext context, string? deploymentSlot = null)
    {
        var computerEnv = (AzureAppServiceEnvironmentResource)TargetResource.GetDeploymentTargetAnnotation()!.ComputeEnvironment!;
        var websiteSuffix = await computerEnv.WebSiteSuffix.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
        var websiteName = $"{TargetResource.Name.ToLowerInvariant()}-{websiteSuffix}";

        if (string.IsNullOrWhiteSpace(deploymentSlot))
        {
            return TruncateToMaxLength(websiteName, 60);
        }

        websiteName = TruncateToMaxLength(websiteName, MaxWebSiteNamePrefixLengthWithSlot);
        websiteName += $"-{deploymentSlot}";

        return TruncateToMaxLength(websiteName, MaxHostPrefixLengthWithSlot);
    }

    private static string TruncateToMaxLength(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }
        return value.Substring(0, maxLength);
    }

    // For Azure App Service, the maximum length for a host name is 63 characters. With slot, the host name is 59 characters, with 4 characters reserved for random slot suffix (very edge case).
    // Source of truth: https://msazure.visualstudio.com/One/_git/AAPT-Antares-Websites?path=%2Fsrc%2FHosting%2FAdministrationService%2FMicrosoft.Web.Hosting.Administration.Api%2FCommonConstants.cs&_a=contents&version=GBdev
    internal const int MaxHostPrefixLengthWithSlot = 59;
    internal const int MaxWebSiteNamePrefixLengthWithSlot = 40;
}
