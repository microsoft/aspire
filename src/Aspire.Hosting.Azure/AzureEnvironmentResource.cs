// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
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
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public sealed class AzureEnvironmentResource : Resource
{
    /// <summary>
    /// The name of the step that creates the provisioning context.
    /// </summary>
    internal const string CreateProvisioningContextStepName = "create-provisioning-context";

    /// <summary>
    /// The name of the step that prepares Azure resources (e.g. materializes role-assignment
    /// resources) so that downstream steps can reference them.
    /// </summary>
    public const string PrepareResourcesStepName = "azure-prepare-resources";

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
            var steps = new List<PipelineStep>();

            var prepareResourcesStep = new PipelineStep
            {
                Name = PrepareResourcesStepName,
                Description = "Prepares the Azure resources.",
                Action = static async context =>
                {
                    var preparer = context.Services.GetRequiredService<AzureResourcePreparer>();
                    await preparer.PrepareResourcesAsync(context.Model, context.CancellationToken).ConfigureAwait(false);
                },
                RequiredBySteps = [WellKnownPipelineSteps.BeforeStart]
            };
            steps.Add(prepareResourcesStep);

            var publishStep = new PipelineStep
            {
                Name = $"publish-{Name}",
                Description = $"Publishes the Azure environment configuration for {Name}.",
                Action = ctx => PublishAsync(ctx),
                RequiredBySteps = [WellKnownPipelineSteps.Publish],
                DependsOnSteps = [WellKnownPipelineSteps.PublishPrereq]
            };
            steps.Add(publishStep);

            var validateStep = new PipelineStep
            {
                Name = "validate-azure-login",
                Description = "Validates Azure CLI authentication before deployment.",
                Action = ctx => ValidateAzureLoginAsync(ctx),
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                DependsOnSteps = [WellKnownPipelineSteps.DeployPrereq]
            };
            steps.Add(validateStep);

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
            steps.Add(createContextStep);
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
            steps.Add(provisionStep);
            provisionStep.DependsOn(createContextStep);

            var destroyStep = new PipelineStep
            {
                Name = $"destroy-azure-{Name}",
                Description = $"Destroys the Azure resource group and all resources for {Name}.",
                Action = ctx => DestroyAzureResourcesAsync(ctx),
                RequiredBySteps = [WellKnownPipelineSteps.Destroy],
                DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq]
            };
            steps.Add(destroyStep);

            return steps;
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

        var azureStateSection = await deploymentStateManager.AcquireSectionAsync("Azure", context.CancellationToken).ConfigureAwait(false);
        var subscriptionId = azureStateSection.Data["SubscriptionId"]?.ToString();
        if (string.IsNullOrEmpty(subscriptionId))
        {
            await context.ReportingStep.CompleteAsync(
                "No Azure deployment state found. Nothing to destroy.",
                CompletionState.Completed,
                context.CancellationToken).ConfigureAwait(false);
            return;
        }

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

        var resourceGroupNames = new HashSet<string>(StringComparers.AzureResourceGroupName);
        if (azureStateSection.Data["ResourceGroup"]?.ToString() is { Length: > 0 } primaryResourceGroupName)
        {
            resourceGroupNames.Add(primaryResourceGroupName);
        }

        foreach (var ownedResourceGroup in context.Model.Resources.OfType<AzureResourceGroupResource>())
        {
            var stateSection = await deploymentStateManager
                .AcquireSectionAsync($"Azure:Deployments:{ownedResourceGroup.Name}", context.CancellationToken)
                .ConfigureAwait(false);
            var outputsJson = stateSection.Data[BicepUtilities.DeploymentStateOutputsKey]?.GetValue<string>();
            var resourceGroupName = outputsJson is null
                ? null
                : AzureProvisioningJsonHelpers.ParseDeploymentStateJson(outputsJson)?["name"]?["value"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(resourceGroupName))
            {
                resourceGroupName = ownedResourceGroup.ResourceGroupName switch
                {
                    string literal => literal,
                    IValueProvider valueProvider => await valueProvider.GetValueAsync(context.CancellationToken).ConfigureAwait(false),
                    _ => null
                };
            }
            if (!string.IsNullOrWhiteSpace(resourceGroupName))
            {
                resourceGroupNames.Add(resourceGroupName);
            }
            else
            {
                context.Logger.LogWarning(
                    "Azure resource group {ResourceName} has no deployment output or resolvable configured name and cannot be destroyed automatically.",
                    ownedResourceGroup.Name);
            }
        }

        if (resourceGroupNames.Count == 0)
        {
            await context.ReportingStep.CompleteAsync(
                "No Azure deployment state found. Nothing to destroy.",
                CompletionState.Completed,
                context.CancellationToken).ConfigureAwait(false);
            return;
        }

        var credential = tokenCredentialProvider.TokenCredential;
        var armClient = armClientProvider.GetArmClient(credential, subscriptionId);
        var (subscription, _) = await armClient.GetSubscriptionAndTenantAsync(context.CancellationToken).ConfigureAwait(false);
        var resourceGroups = subscription.GetResourceGroups();
        var groupsToDelete = new List<(string Name, IResourceGroupResource Resource, List<(string Name, string ResourceType)> Resources)>();
        foreach (var resourceGroupName in resourceGroupNames.Order(StringComparers.AzureResourceGroupName))
        {
            IResourceGroupResource resourceGroup;
            try
            {
                var response = await resourceGroups.GetAsync(resourceGroupName, context.CancellationToken).ConfigureAwait(false);
                resourceGroup = response.Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                context.Logger.LogInformation("Resource group {ResourceGroupName} was already deleted.", resourceGroupName);
                continue;
            }

            var resources = new List<(string Name, string ResourceType)>();
            var discoveryTask = await context.ReportingStep.CreateTaskAsync(
                new MarkdownString($"Discovering resources in **{resourceGroupName}**"),
                context.CancellationToken).ConfigureAwait(false);
            await using (discoveryTask.ConfigureAwait(false))
            {
                try
                {
                    await foreach (var resource in resourceGroup.GetResourcesAsync(context.CancellationToken).ConfigureAwait(false))
                    {
                        resources.Add(resource);
                    }

                    foreach (var (name, type) in resources)
                    {
                        var shortType = type.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                            ? type["Microsoft.".Length..]
                            : type;
                        context.Logger.LogInformation("  {ResourceGroup}: {Type}: {Name}", resourceGroupName, shortType, name);
                    }

                    var discoveryMessage = resources.Count == 0
                        ? new MarkdownString($"Resource group **{resourceGroupName}** is empty")
                        : new MarkdownString($"Found **{resources.Count}** resource(s) in **{resourceGroupName}**");
                    await discoveryTask.CompleteAsync(
                        discoveryMessage,
                        CompletionState.Completed,
                        context.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    context.Logger.LogWarning(ex, "Failed to enumerate resources in resource group {ResourceGroupName}; deletion will still be attempted.", resourceGroupName);
                    await discoveryTask.CompleteAsync(
                        new MarkdownString($"Could not enumerate resources in **{resourceGroupName}**; deletion will still be attempted."),
                        CompletionState.CompletedWithWarning,
                        context.CancellationToken).ConfigureAwait(false);
                }
            }

            groupsToDelete.Add((resourceGroupName, resourceGroup, resources));
        }

        if (groupsToDelete.Count == 0)
        {
            await context.ReportingStep.CompleteAsync(
                "All Azure resource groups were already absent.",
                CompletionState.Completed,
                context.CancellationToken).ConfigureAwait(false);
            return;
        }

        if (!options.Value.SkipConfirmation)
        {
            var interactionService = context.Services.GetRequiredService<IInteractionService>();
            var groupList = string.Join("', '", groupsToDelete.Select(static group => group.Name));
            var resourceCount = groupsToDelete.Sum(static group => group.Resources.Count);
            var confirmMessage = resourceCount > 0
                ? $"Delete resource groups '{groupList}' with {resourceCount} resource(s) in total? This action cannot be undone."
                : $"Delete resource groups '{groupList}'? This action cannot be undone.";

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

        var deleteFailures = new List<Exception>();
        foreach (var (resourceGroupName, resourceGroup, resources) in groupsToDelete)
        {
            var deleteTask = await context.ReportingStep.CreateTaskAsync(
                new MarkdownString($"Deleting resource group **{resourceGroupName}** ({resources.Count} resource(s))"),
                context.CancellationToken).ConfigureAwait(false);
            await using (deleteTask.ConfigureAwait(false))
            {
                try
                {
                    await resourceGroup.DeleteAsync(WaitUntil.Started, context.CancellationToken).ConfigureAwait(false);

                    var portalUrl = AzurePortalUrls.GetResourceGroupUrl(subscriptionId, resourceGroupName, subscription.TenantId);
                    context.Summary.Add($"🗑️ Resource Group {resourceGroupName}", new MarkdownString($"[{resourceGroupName}]({portalUrl})"));
                    await deleteTask.CompleteAsync(
                        new MarkdownString($"Resource group **{resourceGroupName}** deletion in progress. Monitor in the [Azure portal]({portalUrl})."),
                        CompletionState.Completed,
                        context.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await deleteTask.CompleteAsync(
                        $"Failed to delete resource group '{resourceGroupName}': {ex.Message}",
                        CompletionState.CompletedWithError,
                        context.CancellationToken).ConfigureAwait(false);
                    deleteFailures.Add(new InvalidOperationException($"Failed to delete Azure resource group '{resourceGroupName}'.", ex));
                }
            }
        }

        context.Summary.Add("🔑 Subscription", subscriptionId);
        context.Summary.Add("⏳ Status", "Resource-group deletions are in progress.");
        if (deleteFailures.Count > 0)
        {
            throw new AggregateException("One or more Azure resource groups could not be deleted.", deleteFailures);
        }
    }
}
