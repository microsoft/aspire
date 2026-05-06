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
                Resource = targetResource,
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

                    var websiteName = await GetAppServiceWebsiteBaseNameAsync(ctx).ConfigureAwait(false);
                    var hostName = GetAppServiceWebsiteName(websiteName, deploymentSlot);
                    var endpoint = $"https://{hostName}.azurewebsites.net";
                    var portalLink = await AppSvcUrls.GetPortalLinkAsync(computerEnv, websiteName, deploymentSlot, ctx.CancellationToken).ConfigureAwait(false);
                    var summaryValue = $"[{endpoint}]({endpoint}) ({portalLink})";

                    ctx.ReportingStep.Log(LogLevel.Information, new MarkdownString($"Successfully deployed **{targetResource.Name}** to {summaryValue}"));
                    ctx.Summary.Add(targetResource.Name, new MarkdownString(summaryValue));
                },
                Tags = ["print-summary"],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy]
            };

            var deployStep = new PipelineStep
            {
                Name = $"deploy-{targetResource.Name}",
                Description = $"Aggregation step for deploying {targetResource.Name} to Azure App Service.",
                Resource = targetResource,
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

            // Ensure summary step runs after provision.
            // Use targetResource because the print step has Resource = targetResource (set in the step annotation above).
            context.GetSteps(targetResource, "print-summary").DependsOn(provisionSteps);
        }));
    }

    /// <summary>
    /// Gets the target resource that this Azure Web Site is being created for.
    /// </summary>
    public IResource TargetResource { get; }

    /// <summary>
    /// Gets the base Azure App Service website name without any deployment slot suffix.
    /// </summary>
    /// <param name="context">The pipeline step context.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the website name.</returns>
    private async Task<string> GetAppServiceWebsiteBaseNameAsync(PipelineStepContext context)
    {
        var computerEnv = (AzureAppServiceEnvironmentResource)TargetResource.GetDeploymentTargetAnnotation()!.ComputeEnvironment!;
        var websiteSuffix = await computerEnv.WebSiteSuffix.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
        return TruncateToMaxLength($"{TargetResource.Name.ToLowerInvariant()}-{websiteSuffix}", 60);
    }

    private static string GetAppServiceWebsiteName(string websiteName, string? deploymentSlot = null)
    {
        if (string.IsNullOrWhiteSpace(deploymentSlot))
        {
            return websiteName;
        }

        var slotHostName = TruncateToMaxLength(websiteName, MaxWebSiteNamePrefixLengthWithSlot);
        slotHostName += $"-{deploymentSlot}";

        return TruncateToMaxLength(slotHostName, MaxHostPrefixLengthWithSlot);
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
