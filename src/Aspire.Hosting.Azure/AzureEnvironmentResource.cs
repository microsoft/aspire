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
using Azure;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

            var destroyStep = new PipelineStep
            {
                Name = $"destroy-azure-{Name}",
                Description = $"Destroys the Azure resource group and all resources for {Name}.",
                Action = ctx => DestroyAzureResourcesAsync(ctx),
                RequiredBySteps = [WellKnownPipelineSteps.Destroy],
                DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq]
            };

            return [publishStep, validateStep, createContextStep, provisionStep, destroyStep];
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

        ctx.Summary.Add("☁️ Target", "Azure");
        ctx.Summary.Add("📦 Resource Group", AzurePortalUrls.GetResourceGroupLink(subscriptionId, resourceGroupName, tenantId));
        ctx.Summary.Add("📜 Deployments", AzurePortalUrls.GetResourceGroupDeploymentsLink(subscriptionId, resourceGroupName, tenantId));
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

    private static async Task DestroyAzureResourcesAsync(PipelineStepContext context)
    {
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        var tokenCredentialProvider = context.Services.GetRequiredService<ITokenCredentialProvider>();
        var armClientProvider = context.Services.GetRequiredService<IArmClientProvider>();

        // Read deployment state to find the resource group
        var azureStateSection = await deploymentStateManager.AcquireSectionAsync("Azure", context.CancellationToken).ConfigureAwait(false);

        var resourceGroupName = azureStateSection.Data["ResourceGroup"]?.ToString();
        var subscriptionId = azureStateSection.Data["SubscriptionId"]?.ToString();

        if (string.IsNullOrEmpty(resourceGroupName) || string.IsNullOrEmpty(subscriptionId))
        {
            await context.ReportingStep.CompleteAsync(
                "No Azure deployment state found. Nothing to destroy.",
                CompletionState.Completed,
                context.CancellationToken).ConfigureAwait(false);
            return;
        }

        // Fail fast in non-interactive mode without --yes before doing any Azure work
        var options = context.Services.GetRequiredService<IOptions<PipelineOptions>>();
        if (!options.Value.SkipConfirmation)
        {
            var interactionService = context.Services.GetRequiredService<IInteractionService>();
            if (!interactionService.IsAvailable)
            {
                throw new InvalidOperationException(
                    "Cannot perform destructive operation without confirmation. Use --yes to skip the confirmation prompt in non-interactive mode.");
            }
        }

        var credential = tokenCredentialProvider.TokenCredential;
        var armClient = armClientProvider.GetArmClient(credential, subscriptionId);
        var (subscription, _) = await armClient.GetSubscriptionAndTenantAsync(context.CancellationToken).ConfigureAwait(false);

        var resourceGroups = subscription.GetResourceGroups();

        IResourceGroupResource resourceGroup;
        try
        {
            var rgResponse = await resourceGroups.GetAsync(resourceGroupName, context.CancellationToken).ConfigureAwait(false);
            resourceGroup = rgResponse.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Resource group already deleted
            await context.ReportingStep.CompleteAsync(
                new MarkdownString($"Resource group **{resourceGroupName}** not found (already deleted)"),
                CompletionState.Completed,
                context.CancellationToken).ConfigureAwait(false);
            return;
        }

        // Enumerate resources in the resource group so the user can see what will be destroyed
        var resources = new List<(string Name, string ResourceType)>();

        {
            var discoveryTask = await context.ReportingStep.CreateTaskAsync(
                new MarkdownString($"Discovering resources in **{resourceGroupName}**"),
                context.CancellationToken).ConfigureAwait(false);
            await using var _ = discoveryTask.ConfigureAwait(false);

            try
            {
                await foreach (var resource in resourceGroup.GetResourcesAsync(context.CancellationToken).ConfigureAwait(false))
                {
                    resources.Add(resource);
                }

                if (resources.Count == 0)
                {
                    await discoveryTask.CompleteAsync(
                        new MarkdownString($"Resource group **{resourceGroupName}** is empty"),
                        CompletionState.Completed,
                        context.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    foreach (var (name, type) in resources)
                    {
                        var shortType = type.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                            ? type["Microsoft.".Length..]
                            : type;
                        context.Logger.LogInformation("  {Type}: {Name}", shortType, name);
                    }

                    await discoveryTask.CompleteAsync(
                        new MarkdownString($"Found **{resources.Count}** resource(s) in **{resourceGroupName}**"),
                        CompletionState.Completed,
                        context.CancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Non-fatal — proceed with deletion even if enumeration fails
                context.Logger.LogWarning(ex, "Failed to enumerate resources in resource group '{ResourceGroupName}'", resourceGroupName);
                await discoveryTask.CompleteAsync(
                    "Could not enumerate resources (will proceed with deletion)",
                    CompletionState.Completed,
                    context.CancellationToken).ConfigureAwait(false);
            }
        }

        // Confirm destruction with the user (unless --yes was specified)
        if (!options.Value.SkipConfirmation)
        {
            var interactionService = context.Services.GetRequiredService<IInteractionService>();

            var confirmMessage = resources.Count > 0
                ? $"Delete resource group '{resourceGroupName}' with {resources.Count} resource(s)? This action cannot be undone."
                : $"Delete resource group '{resourceGroupName}'? This action cannot be undone.";

            var result = await interactionService.PromptNotificationAsync(
                "Destroy Azure resources",
                confirmMessage,
                new NotificationInteractionOptions
                {
                    Intent = MessageIntent.Confirmation,
                    ShowSecondaryButton = true,
                    ShowDismiss = false,
                    PrimaryButtonText = "Destroy",
                    SecondaryButtonText = "Cancel"
                },
                context.CancellationToken).ConfigureAwait(false);

            if (result.Canceled || !result.Data)
            {
                context.Logger.LogInformation("User canceled the destroy operation.");
                throw new OperationCanceledException("Destroy operation canceled by user.");
            }
        }

        // Delete the resource group
        var deleteTask = await context.ReportingStep.CreateTaskAsync(
            new MarkdownString($"Deleting resource group **{resourceGroupName}** ({resources.Count} resource(s))"),
            context.CancellationToken).ConfigureAwait(false);
        await using var __ = deleteTask.ConfigureAwait(false);

        try
        {
            await resourceGroup.DeleteAsync(WaitUntil.Started, context.CancellationToken).ConfigureAwait(false);

            var portalUrl = AzurePortalUrls.GetResourceGroupUrl(subscriptionId, resourceGroupName, subscription.TenantId);
            context.Summary.Add("🗑️ Resource Group", new MarkdownString($"[{resourceGroupName}]({portalUrl})"));
            context.Summary.Add("🔑 Subscription", subscriptionId);
            context.Summary.Add("⏳ Status", new MarkdownString($"Deletion in progress. Monitor [here]({portalUrl})"));

            await deleteTask.CompleteAsync(
                new MarkdownString($"Resource group **{resourceGroupName}** deletion in progress. Monitor in the [Azure portal]({portalUrl})."),
                CompletionState.Completed,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await deleteTask.CompleteAsync(
                $"Failed to delete resource group '{resourceGroupName}': {ex.Message}",
                CompletionState.CompletedWithError,
                context.CancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
