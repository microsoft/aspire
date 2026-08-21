// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Front Door resource in the distributed application model.
/// </summary>
/// <remarks>
/// Azure Front Door is a global, scalable entry point that uses the Microsoft global edge network to create
/// fast, secure, and widely scalable web applications. It provides load balancing, SSL offloading,
/// and application acceleration for your web applications.
/// </remarks>
public class AzureFrontDoorResource : AzureProvisioningResource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureFrontDoorResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="configureInfrastructure">Callback to configure the Azure resources.</param>
    public AzureFrontDoorResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
        : base(name, configureInfrastructure)
    {
        Annotations.Add(new PipelineStepAnnotation(_ =>
        {
            var summaryStep = new PipelineStep
            {
                Name = $"print-frontdoor-url-{name}",
                Description = $"Prints the Azure Front Door endpoints for {name}.",
                Action = PrintEndpointUrlsAsync,
                Tags = ["print-summary"],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Resource = this
            };
            return summaryStep;
        }));
        Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            var frontDoorProvisionSteps = context.GetSteps(this, WellKnownPipelineTags.ProvisionInfrastructure);
            context.GetSteps(this, "print-summary").DependsOn(frontDoorProvisionSteps);

            foreach (var deploymentTarget in Annotations
                .OfType<AzureFrontDoorOriginAnnotation>()
                .SelectMany(static origin => origin.Resource.GetDeploymentTargetAnnotations())
                .Select(static annotation => annotation.DeploymentTarget)
                .OfType<AzureBicepResource>())
            {
                frontDoorProvisionSteps.DependsOn(
                    context.GetSteps(deploymentTarget, WellKnownPipelineTags.ProvisionInfrastructure));
            }
        }));
    }

    /// <summary>
    /// Gets the endpoint URL output reference for a specific origin by its resource name.
    /// </summary>
    /// <param name="originResourceName">The name of the origin resource (as specified in the Aspire application model).</param>
    /// <returns>A <see cref="BicepOutputReference"/> for the Front Door endpoint URL serving that origin.</returns>
    /// <remarks>
    /// The output name follows the pattern <c>{normalizedOriginName}_endpointUrl</c>.
    /// For example, if the origin resource is named "api", the output is <c>api_endpointUrl</c>.
    /// </remarks>
    public BicepOutputReference GetEndpointUrl(string originResourceName)
    {
        var normalizedName = Infrastructure.NormalizeBicepIdentifier(originResourceName);
        return new($"{normalizedName}_endpointUrl", this);
    }

    private async Task PrintEndpointUrlsAsync(PipelineStepContext context)
    {
        foreach (var origin in Annotations.OfType<AzureFrontDoorOriginAnnotation>())
        {
            var endpointUrl = await GetEndpointUrl(origin.Resource.Name)
                .GetValueAsync(context.CancellationToken)
                .ConfigureAwait(false) ??
                throw new InvalidOperationException($"Azure Front Door did not produce an endpoint for origin '{origin.Resource.Name}'.");
            context.Summary.Add(
                $"🌐 {origin.Resource.Name}",
                new MarkdownString($"[{endpointUrl}]({endpointUrl})"));
        }

        await context.ReportingStep.CompleteAsync(
            "Azure Front Door endpoints are available.",
            CompletionState.Completed,
            context.CancellationToken).ConfigureAwait(false);
    }
}
