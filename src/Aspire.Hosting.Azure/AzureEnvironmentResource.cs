// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Pipelines;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents the root Azure deployment target for an Aspire application.
/// Manages deployment parameters and context for Azure resources.
/// </summary>
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/dotnet/aspire/diagnostics#{0}")]
public sealed class AzureEnvironmentResource : Resource
{
    /// <summary>
    /// The name of the step that creates the provisioning context.
    /// </summary>
    internal const string CreateProvisioningContextStepName = "create-provisioning-context";

    /// <summary>
    /// The name of the step that provisions Azure infrastructure resources.
    /// </summary>
    public const string ProvisionInfrastructureStepName = "provision-azure-bicep-resources";

    /// <summary>
    /// Gets or sets the Azure location that the resources will be deployed to.
    /// </summary>
    public ParameterResource Location { get; set; }

    /// <summary>
    /// Gets or sets the Azure resource group name that the resources will be deployed to.
    /// </summary>
    public ParameterResource ResourceGroupName { get; set; }

    /// <summary>
    /// Gets or sets the Azure principal ID that will be used to deploy the resources.
    /// </summary>
    public ParameterResource PrincipalId { get; set; }

    /// <summary>
    /// Gets the task completion source for the provisioning context.
    /// Consumers should await ProvisioningContextTask.Task to get the provisioning context.
    /// </summary>
    internal TaskCompletionSource<ProvisioningContext> ProvisioningContextTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the Azure environment resource.</param>
    /// <param name="location">The Azure location that the resources will be deployed to.</param>
    /// <param name="resourceGroupName">The Azure resource group name that the resources will be deployed to.</param>
    /// <param name="principalId">The Azure principal ID that will be used to deploy the resources.</param>
    /// <exception cref="ArgumentNullException">Thrown when the name is null or empty.</exception>
    /// <exception cref="ArgumentException">Thrown when the name is invalid.</exception>
    public AzureEnvironmentResource(string name, ParameterResource location, ParameterResource resourceGroupName, ParameterResource principalId) : base(name)
    {
        Annotations.Add(new PipelineStepAnnotation((factoryContext) =>
        {
            var publishStep = new PipelineStep
            {
                Name = $"publish-{Name}",
                Description = $"Publishes the Azure environment configuration for {Name}.",
                Action = ctx => PublishAsync(ctx),
                RequiredBySteps = [WellKnownPipelineSteps.Publish],
                DependsOnSteps = [WellKnownPipelineSteps.PublishPrereq]
            };

            var validateStep = new PipelineStep
            {
                Name = "validate-azure-login",
                Description = "Validates Azure CLI authentication before deployment.",
                Action = ctx => ValidateAzureLoginAsync(ctx),
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                DependsOnSteps = [WellKnownPipelineSteps.DeployPrereq]
            };

            var createContextStep = new PipelineStep
            {
                Name = CreateProvisioningContextStepName,
                Description = "Creates the Azure provisioning context for infrastructure deployment.",
                Action = async ctx =>
                {
                    var provisioningContextProvider = ctx.Services.GetRequiredService<IProvisioningContextProvider>();
                    var provisioningContext = await provisioningContextProvider.CreateProvisioningContextAsync(ctx.CancellationToken).ConfigureAwait(false);
                    ProvisioningContextTask.TrySetResult(provisioningContext);

                    // Add Azure deployment information to the pipeline summary
                    AddToPipelineSummary(ctx, provisioningContext);
                },
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                DependsOnSteps = [WellKnownPipelineSteps.DeployPrereq]
            };
            createContextStep.DependsOn(validateStep);

            var provisionStep = new PipelineStep
            {
                Name = ProvisionInfrastructureStepName,
                Description = "Aggregation step for all Azure infrastructure provisioning operations.",
                Action = _ => Task.CompletedTask,
                Tags = [WellKnownPipelineTags.ProvisionInfrastructure],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                DependsOnSteps = [WellKnownPipelineSteps.DeployPrereq]
            };

            provisionStep.DependsOn(createContextStep);

            return [publishStep, validateStep, createContextStep, provisionStep];
        }));

        Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);

        Location = location;
        ResourceGroupName = resourceGroupName;
        PrincipalId = principalId;
    }

    /// <summary>
    /// Adds Azure deployment information to the pipeline summary.
    /// </summary>
    /// <param name="ctx">The pipeline step context.</param>
    /// <param name="provisioningContext">The Azure provisioning context.</param>
    private static void AddToPipelineSummary(PipelineStepContext ctx, ProvisioningContext provisioningContext)
    {
        var resourceGroupName = provisioningContext.ResourceGroup.Name;
        var subscriptionId = provisioningContext.Subscription.Id.Name;
        var location = provisioningContext.Location.Name;

        var tenantId = provisioningContext.Tenant.TenantId;
        var tenantSegment = tenantId.HasValue ? $"#@{tenantId.Value}" : "#";
        var portalUrl = $"https://portal.azure.com/{tenantSegment}/resource/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/overview";
        var resourceGroupValue = $"[{resourceGroupName}]({portalUrl})";

        ctx.Summary.Add("☁️ Target", "Azure");
        ctx.Summary.Add("📦 Resource Group", new MarkdownString(resourceGroupValue));
        ctx.Summary.Add("🔑 Subscription", subscriptionId);
        ctx.Summary.Add("🌐 Location", location);
    }

    private Task PublishAsync(PipelineStepContext context)
    {
        var azureProvisioningOptions = context.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>();
        var outputService = context.Services.GetRequiredService<IPipelineOutputService>();
        var publishingContext = new AzurePublishingContext(
            outputService.GetOutputDirectory(),
            azureProvisioningOptions.Value,
            context.Services,
            context.Logger,
            context.ReportingStep);

        return publishingContext.WriteModelAsync(context.Model, this);
    }

    private static async Task ValidateAzureLoginAsync(PipelineStepContext context)
    {
        var tokenCredentialProvider = context.Services.GetRequiredService<ITokenCredentialProvider>();

        try
        {
            var tokenRequest = new TokenRequestContext(["https://management.azure.com/.default"]);
            await tokenCredentialProvider.TokenCredential.GetTokenAsync(tokenRequest, context.CancellationToken)
                .ConfigureAwait(false);

            await context.ReportingStep.CompleteAsync(
                "Azure CLI authentication validated successfully",
                CompletionState.Completed,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await context.ReportingStep.CompleteAsync(
                new MarkdownString("Azure CLI authentication failed. Please run `az login` to authenticate before deploying. Learn more at [Azure CLI documentation](https://learn.microsoft.com/cli/azure/authenticate-azure-cli)."),
                CompletionState.CompletedWithError,
                context.CancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
