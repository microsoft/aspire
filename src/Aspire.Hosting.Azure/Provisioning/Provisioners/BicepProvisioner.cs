// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Azure.Resources;
using Aspire.Hosting.Pipelines;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure.Provisioning;

#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

internal sealed class BicepProvisioner(
    ResourceNotificationService notificationService,
    ResourceLoggerService loggerService,
    IBicepCompiler bicepCompiler,
    ISecretClientProvider secretClientProvider,
    IDeploymentStateManager deploymentStateManager,
    DistributedApplicationExecutionContext executionContext,
    IFileSystemService fileSystemService,
    ILogger<BicepProvisioner> logger,
    TimeProvider? timeProvider = null) : IBicepProvisioner
{
    internal const string DeploymentStateProvisioningStateKey = "ProvisioningState";
    internal const string DeploymentStateProvisioningStateRunning = "Running";
    internal const string DeploymentStateProvisioningStateCanceled = "Canceled";
    internal const string DeploymentStateProvisioningStateFailed = "Failed";
    internal const string DeploymentStateProvisioningStateSucceeded = "Succeeded";

    private const string DeploymentActiveErrorCode = "DeploymentActive";
    private const string DeploymentUrlName = "deployment";
    private const string DeploymentOperationPropertyPrefix = "azure.deployment.operations.";
    private const string DeploymentOperationSummaryPropertyName = DeploymentOperationPropertyPrefix + "summary";
    private static readonly TimeSpan s_deploymentOperationPollingInterval = TimeSpan.FromSeconds(2);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<bool> ConfigureResourceAsync(AzureBicepResource resource, CancellationToken cancellationToken)
    {
        var stateSection = await deploymentStateManager.AcquireSectionAsync($"Azure:Deployments:{resource.Name}", cancellationToken).ConfigureAwait(false);
        if (stateSection.Data.Count == 0)
        {
            return false;
        }

        if (stateSection.Data[DeploymentStateProvisioningStateKey]?.GetValue<string>() is { Length: > 0 } provisioningState &&
            !string.Equals(provisioningState, DeploymentStateProvisioningStateSucceeded, StringComparison.Ordinal))
        {
            logger.LogDebug("Cached deployment state for resource {ResourceName} is incomplete because provisioning state is {ProvisioningState}.", resource.Name, provisioningState);
            return false;
        }

        var currentCheckSum = await BicepUtilities.GetCurrentChecksumAsync(resource, stateSection, logger, cancellationToken).ConfigureAwait(false);
        var configCheckSum = stateSection.Data[BicepUtilities.DeploymentStateChecksumKey]?.GetValue<string>();

        if (string.IsNullOrEmpty(configCheckSum))
        {
            logger.LogDebug("Cached deployment state for resource {ResourceName} is incomplete because it is missing a checksum.", resource.Name);
            return false;
        }

        if (string.IsNullOrEmpty(currentCheckSum) || !string.Equals(currentCheckSum, configCheckSum, StringComparison.Ordinal))
        {
            logger.LogDebug("Checksum mismatch for resource {ResourceName}. Expected cached checksum {ExpectedChecksum}, computed checksum {ActualChecksum}", resource.Name, configCheckSum, currentCheckSum);
            return false;
        }

        logger.LogDebug("Configuring resource {ResourceName} from existing deployment state.", resource.Name);

        if (stateSection.Data[BicepUtilities.DeploymentStateOutputsKey]?.GetValue<string>() is { Length: > 0 } outputJson)
        {
            JsonNode? outputObj = null;
            try
            {
                outputObj = JsonNode.Parse(outputJson);

                if (outputObj is null)
                {
                    return false;
                }
            }
            catch
            {
                // Unable to parse the JSON, to treat it as not existing
                return false;
            }

            foreach (var item in outputObj.AsObject())
            {
                // TODO: Handle complex output types
                // Populate the resource outputs
                resource.Outputs[item.Key] = item.Value?.Prop("value")?.ToString();
            }
        }

        if (resource is IAzureKeyVaultResource kvr)
        {
            ConfigureSecretResolver(kvr);
        }

        var portalUrls = new List<UrlSnapshot>();

        string? deploymentId = null;
        ResourceIdentifier? deploymentResourceId = null;
        if (stateSection.Data[BicepUtilities.DeploymentStateIdKey]?.GetValue<string>() is { Length: > 0 } configuredDeploymentId &&
            ResourceIdentifier.TryParse(configuredDeploymentId, out var id) &&
            id is not null)
        {
            deploymentId = configuredDeploymentId;
            deploymentResourceId = id;
            portalUrls.Add(new(Name: DeploymentUrlName, Url: GetDeploymentUrl(id), IsInternal: false));
        }

        var azureContext = await GetCurrentAzureContextAsync(deploymentResourceId, cancellationToken).ConfigureAwait(false);
        var configuredLocation = GetConfiguredLocation(stateSection, azureContext.Location);

        await notificationService.PublishUpdateAsync(resource, state =>
        {
            // Reused deployment state should expose the same Azure identity metadata as a freshly provisioned resource
            // so agents and commands can reliably locate the backing Azure deployment.
            var props = state.Properties.WithoutAzureProvisioningFailureProperties()
                .SetResourcePropertyRange(AzureResourceProperties.CreateContextProperties(
                    azureContext.SubscriptionId,
                    azureContext.ResourceGroup,
                    azureContext.TenantId,
                    azureContext.TenantDomain,
                    configuredLocation,
                    includeEmptyProperties: true))
                .SetResourcePropertyRange([new(CustomResourceKnownProperties.Source, deploymentId)]);

            return state with
            {
                State = new(AzureProvisioningController.ProvisionedState, KnownResourceStateStyles.Success),
                Urls = [.. portalUrls],
                Properties = props
            };
        }).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Adopts a cached ARM deployment that was still running when the AppHost last stopped or was canceled.
    /// </summary>
    /// <remarks>
    /// The persisted state only tells us which deployment belongs to this resource. ARM remains the source of
    /// truth, so reconciliation probes the deployment, follows it while it is still active, applies outputs if it
    /// succeeded, records terminal failure/cancellation, or clears stale local state so normal provisioning can retry.
    /// </remarks>
    public async Task<bool> ReconcileDeploymentStateAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
    {
        // Reconciliation is a run-mode recovery step. Publish should not revive state
        // left behind by a previous local command.
        if (!context.ExecutionContext.IsRunMode)
        {
            return false;
        }

        // The cache stores the ARM deployment ID that was running before the AppHost
        // stopped or a command was canceled.
        var sectionName = $"Azure:Deployments:{resource.Name}";
        var stateSection = await deploymentStateManager.AcquireSectionAsync(sectionName, cancellationToken).ConfigureAwait(false);

        // Example cached section shape:
        //   Azure:Deployments:cache
        //     Id = /subscriptions/<subscription>/resourceGroups/<group>/providers/Microsoft.Resources/deployments/cache
        //     Parameters = {"redisName":{"value":"cache"}}
        //     Scope = {"subscriptionId":"<subscription>","resourceGroup":"<group>"}
        //     CheckSum = <model-checksum>
        //     ProvisioningState = Running
        // Only sections that still claim a running deployment with a parseable ARM ID can be adopted.
        if (!IsRunningCachedDeployment(stateSection) ||
            stateSection.Data[BicepUtilities.DeploymentStateIdKey]?.GetValue<string>() is not { Length: > 0 } deploymentId ||
            !ResourceIdentifier.TryParse(deploymentId, out var deploymentResourceId) ||
            deploymentResourceId is null)
        {
            return false;
        }

        AzureDeploymentState? deployment;
        try
        {
            // Probe ARM before mutating cached state. If the probe cannot run, leave
            // the cache intact and let normal provisioning decide.
            deployment = await context.ArmClient.GetDeploymentAsync(deploymentId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CredentialUnavailableException ex)
        {
            logger.LogDebug(ex, "Unable to reconcile cached deployment state for {ResourceName} because no Azure credential is available.", resource.Name);
            return false;
        }
        catch (RequestFailedException ex)
        {
            logger.LogDebug(ex, "Unable to reconcile cached deployment state for {ResourceName} because the Azure deployment probe failed.", resource.Name);
            return false;
        }

        if (deployment is null)
        {
            // ARM no longer has the cached deployment, so the local "running" marker
            // is stale and should not block a fresh deployment.
            logger.LogInformation("Cached Azure deployment {DeploymentId} for {ResourceName} no longer exists. Reprovisioning.", deploymentId, resource.Name);
            await ClearCachedRunningDeploymentStateAsync(stateSection, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var currentLocation = GetConfiguredLocation(stateSection, context.Location.Name);
        if (IsActiveDeploymentProvisioningState(deployment.ProvisioningState))
        {
            // A still-active ARM deployment should be adopted and observed to
            // completion instead of starting a duplicate deployment.
            deployment = await WaitForCachedRunningDeploymentAsync(
                resource,
                context,
                deploymentId,
                currentLocation,
                deployment,
                cancellationToken).ConfigureAwait(false);

            if (deployment is null)
            {
                // The deployment disappeared while we were watching it; clear the
                // stale marker so the caller reprovisions from the model.
                logger.LogInformation("Cached Azure deployment {DeploymentId} for {ResourceName} no longer exists after reconciliation started. Reprovisioning.", deploymentId, resource.Name);
                await ClearCachedRunningDeploymentStateAsync(stateSection, cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        // At this point the cached deployment is no longer active. Adopt known
        // terminal outcomes; leave unknown states to the reprovision fallback below.
        if (await TryApplyTerminalReconciledDeploymentStateAsync(resource, stateSection, deploymentResourceId, deployment, currentLocation, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        logger.LogDebug("Cached Azure deployment {DeploymentId} for {ResourceName} has provisioning state {ProvisioningState}. Reprovisioning.", deploymentId, resource.Name, deployment.ProvisioningState);
        return false;
    }

    private async Task<AzureDeploymentState?> WaitForCachedRunningDeploymentAsync(
        AzureBicepResource resource,
        ProvisioningContext context,
        string deploymentId,
        string currentLocation,
        AzureDeploymentState deployment,
        CancellationToken cancellationToken)
    {
        var tracker = new DeploymentOperationProgressTracker();
        while (IsActiveDeploymentProvisioningState(deployment.ProvisioningState))
        {
            try
            {
                await PublishCachedRunningDeploymentStateAsync(resource, context, deploymentId, currentLocation, tracker, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (RequestFailedException ex)
            {
                logger.LogDebug(ex, "Unable to publish Azure deployment operation progress for cached deployment {DeploymentId} on {ResourceName}.", deploymentId, resource.Name);
            }
            catch (CredentialUnavailableException ex)
            {
                logger.LogDebug(ex, "Unable to publish Azure deployment operation progress for cached deployment {DeploymentId} on {ResourceName} because no Azure credential is available.", deploymentId, resource.Name);
            }

            await Task.Delay(s_deploymentOperationPollingInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            AzureDeploymentState? latestDeployment;
            try
            {
                latestDeployment = await context.ArmClient.GetDeploymentAsync(deploymentId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (RequestFailedException ex)
            {
                logger.LogDebug(ex, "Unable to continue reconciling cached Azure deployment {DeploymentId} for {ResourceName}.", deploymentId, resource.Name);
                return deployment;
            }
            catch (CredentialUnavailableException ex)
            {
                logger.LogDebug(ex, "Unable to continue reconciling cached Azure deployment {DeploymentId} for {ResourceName} because no Azure credential is available.", deploymentId, resource.Name);
                return deployment;
            }

            if (latestDeployment is null)
            {
                return null;
            }

            deployment = latestDeployment;
        }

        return deployment;
    }

    private async Task PublishCachedRunningDeploymentStateAsync(
        AzureBicepResource resource,
        ProvisioningContext context,
        string deploymentId,
        string currentLocation,
        DeploymentOperationProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        var deploymentResourceId = new ResourceIdentifier(deploymentId);
        var deploymentUrl = GetDeploymentUrl(deploymentResourceId);

        // A cached deployment can be adopted after the original AppHost process is gone,
        // so rebuild the dashboard's "waiting for deployment" state from persisted ARM
        // metadata before publishing more detailed operation progress.
        await notificationService.PublishUpdateAsync(resource, state => state with
        {
            State = new(AzureProvisioningController.WaitingForDeploymentState, KnownResourceStateStyles.Info),
            Urls = [.. state.Urls.Where(static url => !string.Equals(url.Name, DeploymentUrlName, StringComparison.Ordinal)), new(Name: DeploymentUrlName, Url: deploymentUrl, IsInternal: false)],
            Properties = state.Properties
                .WithoutAzureProvisioningFailureProperties()
                .SetResourceProperty(CustomResourceKnownProperties.Source, deploymentId)
        }).ConfigureAwait(false);

        // Deployment operations carry provider-level progress and failure details that
        // are not present on the deployment resource itself. Publish a best-effort
        // summary so the adopted deployment looks like an in-flight deployment started
        // by the current AppHost run.
        await PublishDeploymentOperationSummaryAsync(resource, context, deploymentResourceId, currentLocation, tracker, force: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ConfigureSucceededReconciledDeploymentAsync(
        AzureBicepResource resource,
        DeploymentStateSection stateSection,
        ResourceIdentifier deploymentId,
        JsonObject? outputs,
        string currentLocation,
        CancellationToken cancellationToken)
    {
        if (!TryGetDeploymentStateJsonObject(stateSection, BicepUtilities.DeploymentStateParametersKey, resource.Name, out var parameters) ||
            stateSection.Data[BicepUtilities.DeploymentStateChecksumKey]?.GetValue<string>() is not { Length: > 0 } checksum)
        {
            logger.LogDebug("Cached deployment state for resource {ResourceName} cannot be reconciled because parameters or checksum are missing.", resource.Name);
            return false;
        }

        // Scope is optional cached state. If it is missing or malformed, we can still
        // adopt the succeeded deployment because the current Azure context supplies
        // the resource group scope when ConfigureResourceAsync reloads the outputs.
        // The cached value is the JSON string produced by BicepUtilities.SetScopeAsync:
        //   {"resourceGroup":"<group>","subscription":"<subscription>"}
        // Tenant-scoped resources also include "tenant": "current"; resources without
        // an explicit/existing scope persist {"resourceGroup":null}.
        var scope = TryGetDeploymentStateJsonObject(stateSection, BicepUtilities.DeploymentStateScopeKey, resource.Name, out var cachedScope)
            ? cachedScope
            : null;

        var locationOverride = stateSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>();

        UpdateDeploymentState(
            stateSection,
            locationOverride,
            deploymentId,
            parameters,
            outputs,
            scope,
            checksum,
            currentLocation,
            provisioningState: null);
        await deploymentStateManager.SaveSectionAsync(stateSection, cancellationToken).ConfigureAwait(false);

        return await ConfigureResourceAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryApplyTerminalReconciledDeploymentStateAsync(
        AzureBicepResource resource,
        DeploymentStateSection stateSection,
        ResourceIdentifier deploymentId,
        AzureDeploymentState deployment,
        string currentLocation,
        CancellationToken cancellationToken)
    {
        // Terminal deployment states are the only states we can safely adopt. A
        // succeeded deployment updates cached outputs and configures the resource.
        // Canceled/failed deployments persist the ARM terminal state and throw so
        // the dashboard shows the original outcome. Any other state returns false
        // so the caller can reprovision from the current model.
        if (string.Equals(deployment.ProvisioningState, DeploymentStateProvisioningStateSucceeded, StringComparisons.AzureProvisioningState))
        {
            // Successful adoption replays the deployment outputs into the resource
            // state exactly as normal provisioning would.
            return await ConfigureSucceededReconciledDeploymentAsync(
                resource,
                stateSection,
                deploymentId,
                deployment.Outputs,
                currentLocation,
                cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(deployment.ProvisioningState, DeploymentStateProvisioningStateCanceled, StringComparisons.AzureProvisioningState))
        {
            // Preserve ARM's terminal cancellation so the dashboard explains why this
            // resource stopped instead of silently retrying.
            await PersistReconciledProvisioningStateAsync(stateSection, DeploymentStateProvisioningStateCanceled, cancellationToken).ConfigureAwait(false);
            await PublishReconciledTerminalStateAsync(resource, AzureProvisioningStrings.ResourceStateAzureDeploymentCanceled).ConfigureAwait(false);
            throw new InvalidOperationException($"Azure deployment for {resource.Name} was canceled.");
        }

        if (string.Equals(deployment.ProvisioningState, DeploymentStateProvisioningStateFailed, StringComparisons.AzureProvisioningState))
        {
            // Preserve ARM's terminal failure for the same reason as cancellation:
            // this is the outcome of the adopted deployment.
            await PersistReconciledProvisioningStateAsync(stateSection, DeploymentStateProvisioningStateFailed, cancellationToken).ConfigureAwait(false);
            await PublishReconciledTerminalStateAsync(resource, AzureProvisioningStrings.ResourceStateAzureDeploymentFailed).ConfigureAwait(false);
            throw new InvalidOperationException($"Azure deployment for {resource.Name} failed.");
        }

        return false;
    }

    private async Task PersistReconciledProvisioningStateAsync(DeploymentStateSection stateSection, string provisioningState, CancellationToken cancellationToken)
    {
        stateSection.Data[DeploymentStateProvisioningStateKey] = provisioningState;
        await deploymentStateManager.SaveSectionAsync(stateSection, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishReconciledTerminalStateAsync(AzureBicepResource resource, string state)
    {
        await notificationService.PublishUpdateAsync(resource, snapshot => snapshot with
        {
            State = new(state, KnownResourceStateStyles.Error)
        }).ConfigureAwait(false);
    }

    private async Task ClearCachedRunningDeploymentStateAsync(DeploymentStateSection stateSection, CancellationToken cancellationToken)
    {
        var locationOverride = stateSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>();
        stateSection.Data.Clear();

        if (!string.IsNullOrWhiteSpace(locationOverride))
        {
            stateSection.Data[AzureProvisioningController.LocationOverrideKey] = locationOverride;
            await deploymentStateManager.SaveSectionAsync(stateSection, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await deploymentStateManager.DeleteSectionAsync(stateSection, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryGetDeploymentStateJsonObject(
        DeploymentStateSection stateSection,
        string key,
        string resourceName,
        [NotNullWhen(true)] out JsonObject? value)
    {
        value = null;
        if (stateSection.Data[key]?.GetValue<string>() is not { Length: > 0 } json)
        {
            return false;
        }

        try
        {
            value = AzureProvisioningJsonHelpers.ParseDeploymentStateJson(json)?.AsObject();
            return value is not null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unable to parse cached deployment state property {PropertyName} for resource {ResourceName}.", key, resourceName);
            return false;
        }
    }

    private static bool IsRunningCachedDeployment(DeploymentStateSection stateSection)
        => string.Equals(
            stateSection.Data[DeploymentStateProvisioningStateKey]?.GetValue<string>(),
            DeploymentStateProvisioningStateRunning,
            StringComparison.Ordinal);

    private static bool IsActiveDeploymentProvisioningState(string? provisioningState)
        => !string.IsNullOrEmpty(provisioningState) &&
           !string.Equals(provisioningState, DeploymentStateProvisioningStateSucceeded, StringComparisons.AzureProvisioningState) &&
           !string.Equals(provisioningState, DeploymentStateProvisioningStateFailed, StringComparisons.AzureProvisioningState) &&
           !string.Equals(provisioningState, DeploymentStateProvisioningStateCanceled, StringComparisons.AzureProvisioningState);

    /// <inheritdoc />
    public async Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
    {
        var resourceGroup = context.ResourceGroup;
        var subscription = context.Subscription;
        var resourceLogger = loggerService.GetLogger(resource);
        var targetScope = BicepUtilities.GetExistingResourceScope(resource);
        var isTenantScoped = targetScope?.IsTenantScope == true;
        var isSubscriptionScoped = !isTenantScoped &&
            targetScope is { Subscription: not null, ResourceGroup: null };

        if (targetScope?.Subscription is { } existingSubscription)
        {
            var existingSubscriptionId = await ResolveScopeValueAsync(existingSubscription, cancellationToken).ConfigureAwait(false);
            subscription = await context.ArmClient.GetSubscriptionAsync(existingSubscriptionId, cancellationToken).ConfigureAwait(false);
        }

        if (targetScope?.ResourceGroup is { } existingResourceGroup)
        {
            var existingResourceGroupName = await ResolveScopeValueAsync(existingResourceGroup, cancellationToken).ConfigureAwait(false);
            var response = await subscription.GetResourceGroups().GetAsync(existingResourceGroupName, cancellationToken).ConfigureAwait(false);
            resourceGroup = response.Value;
        }

        var resourceGroupName = isTenantScoped || isSubscriptionScoped ? null : resourceGroup.Id.Name;
        var effectiveLocation = GetEffectiveLocation(resource, context);

        await notificationService.PublishUpdateAsync(resource, state => state with
        {
            ResourceType = resource.GetType().Name,
            State = new(AzureProvisioningController.StartingState, KnownResourceStateStyles.Info),
            Properties = WithoutDeploymentOperationProperties(state.Properties.WithoutAzureProvisioningFailureProperties()).SetResourcePropertyRange(
                AzureResourceProperties.CreateContextProperties(
                    subscription.Id.Name,
                    resourceGroupName,
                    context.Tenant.TenantId?.ToString(),
                    context.Tenant.DefaultDomain,
                    effectiveLocation,
                    includeEmptyProperties: true))
        }).ConfigureAwait(false);

        var tempDirectory = fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-bicep").Path;
        var template = resource.GetBicepTemplateFile(tempDirectory);
        var path = template.Path;

        // GetBicepTemplateFile may have added new well-known parameters, so we need
        // to populate them only after calling GetBicepTemplateFile.
        PopulateWellKnownParameters(resource, context);

        await notificationService.PublishUpdateAsync(resource, state =>
        {
            return state with
            {
                State = new(AzureProvisioningController.CompilingArmTemplateState, KnownResourceStateStyles.Info)
            };
        })
        .ConfigureAwait(false);

        var armTemplateContents = await bicepCompiler.CompileBicepToArmAsync(path, cancellationToken).ConfigureAwait(false);

        logger.LogDebug("Setting parameters and scope for resource {ResourceName}", resource.Name);
        // Convert the parameters to a JSON object
        var parameters = new JsonObject();
        await BicepUtilities.SetParametersAsync(parameters, resource, cancellationToken: cancellationToken).ConfigureAwait(false);

        var scope = new JsonObject();
        await BicepUtilities.SetScopeAsync(scope, resource, cancellationToken: cancellationToken).ConfigureAwait(false);

        var deployments = isTenantScoped
            ? context.Tenant.GetArmDeployments()
            : isSubscriptionScoped
                ? subscription.GetArmDeployments()
                : resourceGroup.GetArmDeployments();
        var deploymentName = executionContext.IsPublishMode ? $"{resource.Name}-{_timeProvider.GetUtcNow().ToUnixTimeSeconds()}" : resource.Name;
        var deploymentId = GetDeploymentId(isTenantScoped, isSubscriptionScoped, subscription, resourceGroup, deploymentName);
        var checksum = BicepUtilities.GetChecksum(resource, parameters, scope);
        var sw = Stopwatch.StartNew();

        await notificationService.PublishUpdateAsync(resource, state =>
        {
            return state with
            {
                State = new(AzureProvisioningController.CreatingArmDeploymentState, KnownResourceStateStyles.Info),
                Properties = state.Properties.SetResourceProperty(CustomResourceKnownProperties.Source, deploymentId.ToString()),
            };
        })
        .ConfigureAwait(false);

        var deploymentTargetName = GetDeploymentTargetName(isTenantScoped, isSubscriptionScoped, subscription, resourceGroup);
        resourceLogger.LogInformation("Deploying {Name} to {DeploymentTarget}", resource.Name, deploymentTargetName);
        logger.LogDebug("Starting deployment of resource {ResourceName} to {DeploymentTarget}", resource.Name, deploymentTargetName);

        var deploymentContent = new ArmDeploymentContent(new(ArmDeploymentMode.Incremental)
        {
            Template = BinaryData.FromString(armTemplateContents),
            Parameters = BinaryData.FromObjectAsJson(parameters),
            DebugSettingDetailLevel = "ResponseContent"
        });

        if (isTenantScoped || isSubscriptionScoped)
        {
            deploymentContent.Location = new AzureLocation(effectiveLocation);
        }

        ArmOperation<ArmDeploymentResource> operation;
        try
        {
            operation = await deployments.CreateOrUpdateAsync(WaitUntil.Started, deploymentName, deploymentContent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (context.ExecutionContext.IsRunMode &&
                await TryCancelDeploymentAsync(deployments, deploymentName, resourceLogger, treatMissingOrInactiveAsCanceled: false).ConfigureAwait(false))
            {
                var sectionName = $"Azure:Deployments:{resource.Name}";
                var canceledStateSection = await deploymentStateManager.AcquireSectionAsync(sectionName, CancellationToken.None).ConfigureAwait(false);
                var canceledLocationOverride = canceledStateSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>();
                UpdateDeploymentState(canceledStateSection, canceledLocationOverride, deploymentId, parameters, outputObj: null, scope, checksum, effectiveLocation, DeploymentStateProvisioningStateCanceled);
                await deploymentStateManager.SaveSectionAsync(canceledStateSection, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        catch (RequestFailedException ex)
        {
            if (context.ExecutionContext.IsRunMode &&
                IsActiveDeploymentConflict(ex) &&
                await TryAdoptActiveDeploymentConflictAsync(
                    resource,
                    context,
                    deploymentId,
                    checksum,
                    effectiveLocation,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            // Some validation failures occur before Azure creates a deployment operation. In that
            // path there is no operation list to poll, so parse and enrich the provider error from
            // the CreateOrUpdate response itself.
            var failureDetails = AzureProvisioningFailureDetails.FromRequestFailedException(ex, AzureProvisioningFailureDetails.ProvisionOperation);
            if (!failureDetails.IsLocationAvailabilityFailure)
            {
                LogProvisioningFailure(resourceLogger, failureDetails);
                throw;
            }

            failureDetails = await EnrichFailureDetailsAsync(failureDetails, context, effectiveLocation, cancellationToken).ConfigureAwait(false);
            if (context.ExecutionContext.IsRunMode)
            {
                await notificationService.PublishUpdateAsync(resource, state => state with
                {
                    State = new(AzureProvisioningStrings.ResourceStateAzureDeploymentFailed, KnownResourceStateStyles.Error),
                    Properties = failureDetails.SetResourceProperties(WithoutDeploymentOperationProperties(state.Properties), AzureProvisioningFailureDetails.ProvisionOperation)
                }).ConfigureAwait(false);
            }

            LogProvisioningFailure(resourceLogger, failureDetails);
            throw new AzureProvisioningFailureException(failureDetails, ex);
        }

        // Run mode keeps persisting deployment state and final diagnostics after cancellation so
        // Ctrl+C leaves enough information for recovery. Publish/deploy should still honor the
        // caller's cancellation token because those operations are command-scoped.
        var statePersistenceCancellationToken = context.ExecutionContext.IsRunMode ? CancellationToken.None : cancellationToken;
        DeploymentStateSection? stateSection = null;
        string? locationOverride = null;

        if (context.ExecutionContext.IsRunMode)
        {
            var sectionName = $"Azure:Deployments:{resource.Name}";
            stateSection = await deploymentStateManager.AcquireSectionAsync(sectionName, statePersistenceCancellationToken).ConfigureAwait(false);
            locationOverride = stateSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>();
            UpdateDeploymentState(stateSection, locationOverride, deploymentId, parameters, outputObj: null, scope, checksum, effectiveLocation, DeploymentStateProvisioningStateRunning);
            await deploymentStateManager.SaveSectionAsync(stateSection, statePersistenceCancellationToken).ConfigureAwait(false);
        }
        // Resolve the deployment URL before waiting for the operation to complete
        var url = GetDeploymentUrl(deploymentId);

        resourceLogger.LogInformation("Deployment started: {Url}", url);

        await notificationService.PublishUpdateAsync(resource, state =>
        {
            return state with
            {
                State = new(AzureProvisioningController.WaitingForDeploymentState, KnownResourceStateStyles.Info),
                Urls = [.. state.Urls, new(Name: DeploymentUrlName, Url: url, IsInternal: false)],
                Properties = state.Properties.SetResourceProperty(CustomResourceKnownProperties.Source, deploymentId.ToString()),
            };
        })
        .ConfigureAwait(false);

        using var deploymentOperationTrackingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deploymentOperationTracker = new DeploymentOperationProgressTracker();

        // Operation polling is run-mode UX only. Publish/deploy paths do a single operation lookup
        // after an LRO failure so CLI deployments get the same provider-level diagnostics without
        // adding steady-state polling to successful deployments.
        var deploymentOperationTrackingTask = context.ExecutionContext.IsRunMode
            ? TrackDeploymentOperationsAsync(resource, context, deploymentId, effectiveLocation, deploymentOperationTracker, deploymentOperationTrackingCts.Token)
            : Task.CompletedTask;

        try
        {
            await operation.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
            if (context.ExecutionContext.IsRunMode)
            {
                await PublishDeploymentOperationSummaryAsync(resource, context, deploymentId, effectiveLocation, deploymentOperationTracker, force: true, statePersistenceCancellationToken).ConfigureAwait(false);
            }
        }
        catch (RequestFailedException ex)
        {
            var summary = await PublishDeploymentOperationSummaryAsync(
                resource,
                context,
                deploymentId,
                effectiveLocation,
                deploymentOperationTracker,
                force: true,
                statePersistenceCancellationToken).ConfigureAwait(false);

            if (summary.FailedOperations.FirstOrDefault(static operation => operation.FailureDetails is not null)?.FailureDetails is { } failureDetails)
            {
                LogProvisioningFailure(resourceLogger, failureDetails);
                throw new AzureProvisioningFailureException(failureDetails, ex);
            }

            var requestFailureDetails = AzureProvisioningFailureDetails.FromRequestFailedException(ex, AzureProvisioningFailureDetails.ProvisionOperation);
            if (requestFailureDetails.IsLocationAvailabilityFailure)
            {
                requestFailureDetails = await EnrichFailureDetailsAsync(requestFailureDetails, context, effectiveLocation, cancellationToken).ConfigureAwait(false);
                LogProvisioningFailure(resourceLogger, requestFailureDetails);
                throw new AzureProvisioningFailureException(requestFailureDetails, ex);
            }

            LogProvisioningFailure(resourceLogger, requestFailureDetails);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (await TryCancelDeploymentAsync(deployments, deploymentName, resourceLogger, treatMissingOrInactiveAsCanceled: true).ConfigureAwait(false) &&
                stateSection is not null)
            {
                UpdateDeploymentState(stateSection, locationOverride, deploymentId, parameters, outputObj: null, scope, checksum, effectiveLocation, DeploymentStateProvisioningStateCanceled);
                await deploymentStateManager.SaveSectionAsync(stateSection, statePersistenceCancellationToken).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            deploymentOperationTrackingCts.Cancel();
            await deploymentOperationTrackingTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        sw.Stop();
        resourceLogger.LogInformation("Deployment of {Name} to {DeploymentTarget} took {Elapsed}", resource.Name, deploymentTargetName, sw.Elapsed);
        logger.LogDebug("Deployment of resource {ResourceName} to {DeploymentTarget} completed in {Elapsed}", resource.Name, deploymentTargetName, sw.Elapsed);

        var deployment = operation.Value;

        var provisioningState = deployment.Data.Properties.ProvisioningState;

        if (provisioningState == ResourcesProvisioningState.Succeeded)
        {
            if (context.ExecutionContext.IsRunMode)
            {
                template.Dispose();
            }
        }
        else
        {
            if (stateSection is not null)
            {
                UpdateDeploymentState(stateSection, locationOverride, deployment.Id, parameters, outputObj: null, scope, checksum, effectiveLocation, provisioningState.ToString());
                await deploymentStateManager.SaveSectionAsync(stateSection, statePersistenceCancellationToken).ConfigureAwait(false);
            }

            resourceLogger.LogError("Azure deployment of {Name} to {DeploymentTarget} completed with provisioning state {ProvisioningState}.", resource.Name, deploymentTargetName, provisioningState);
            throw new InvalidOperationException($"Deployment of {resource.Name} to {deploymentTargetName} failed with {provisioningState}");
        }

        // e.g. {  "sqlServerName": { "type": "String", "value": "<value>" }}
        var outputs = deployment.Data.Properties.Outputs;
        var outputObj = outputs?.ToObjectFromJson<JsonObject>();

        if (stateSection is null)
        {
            var sectionName = $"Azure:Deployments:{resource.Name}";
            stateSection = await deploymentStateManager.AcquireSectionAsync(sectionName, statePersistenceCancellationToken).ConfigureAwait(false);
            locationOverride = stateSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>();
        }

        UpdateDeploymentState(stateSection, locationOverride, deployment.Id, parameters, outputObj, scope, checksum, effectiveLocation, provisioningState: null);
        await deploymentStateManager.SaveSectionAsync(stateSection, statePersistenceCancellationToken).ConfigureAwait(false);

        if (outputObj is not null)
        {
            foreach (var item in outputObj.AsObject())
            {
                // TODO: Handle complex output types
                // Populate the resource outputs
                resource.Outputs[item.Key] = item.Value?.Prop("value")?.ToString();
            }
        }

        // Populate secret outputs from key vault (if any)
        if (resource is IAzureKeyVaultResource kvr)
        {
            ConfigureSecretResolver(kvr);
        }

        await notificationService.PublishUpdateAsync(resource, state =>
        {
            ImmutableArray<ResourcePropertySnapshot> properties = state.Properties.WithoutAzureProvisioningFailureProperties()
                .SetResourcePropertyRange(AzureResourceProperties.CreateContextProperties(
                    subscription.Id.Name,
                    resourceGroupName,
                    context.Tenant.TenantId?.ToString(),
                    context.Tenant.DefaultDomain,
                    effectiveLocation,
                    includeEmptyProperties: true))
                .SetResourceProperty(CustomResourceKnownProperties.Source, deployment.Id.ToString());

            return state with
            {
                State = new(AzureProvisioningController.ProvisionedState, KnownResourceStateStyles.Success),
                CreationTimeStamp = _timeProvider.GetUtcNow().UtcDateTime,
                Properties = properties
            };
        })
        .ConfigureAwait(false);
    }

    // Handles ARM 409 DeploymentActive responses by adopting the deployment that Azure says is
    // already running, but only when persisted state proves it belongs to the same resource model.
    // If any proof is missing, stale, or unreadable, return false so the caller uses the normal
    // failure path instead of accidentally adopting an unrelated deployment.
    private async Task<bool> TryAdoptActiveDeploymentConflictAsync(
        AzureBicepResource resource,
        ProvisioningContext context,
        ResourceIdentifier deploymentId,
        string checksum,
        string effectiveLocation,
        CancellationToken cancellationToken)
    {
        var sectionName = $"Azure:Deployments:{resource.Name}";
        var stateSection = await deploymentStateManager.AcquireSectionAsync(sectionName, cancellationToken).ConfigureAwait(false);
        var cachedDeploymentId = stateSection.Data[BicepUtilities.DeploymentStateIdKey]?.GetValue<string>();
        var cachedChecksum = stateSection.Data[BicepUtilities.DeploymentStateChecksumKey]?.GetValue<string>();

        // The active deployment must be the same ARM deployment and the same compiled model checksum
        // that this AppHost is trying to run. Otherwise, the 409 could be from another command or a
        // previous model, and adopting it would publish the wrong outputs.
        if (!IsRunningCachedDeployment(stateSection) ||
            !string.Equals(cachedDeploymentId, deploymentId.ToString(), StringComparisons.AzureResourceId) ||
            !string.Equals(cachedChecksum, checksum, StringComparison.Ordinal))
        {
            logger.LogDebug("Azure deployment {DeploymentId} for {ResourceName} is active, but cached state does not match the current deployment request.", deploymentId, resource.Name);
            return false;
        }

        AzureDeploymentState? deployment;
        try
        {
            // ARM is the source of truth after the 409. The cache proves ownership, but ARM tells us
            // whether the deployment is still active, terminal, or already gone.
            deployment = await context.ArmClient.GetDeploymentAsync(deploymentId.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RequestFailedException ex)
        {
            logger.LogDebug(ex, "Unable to adopt active Azure deployment {DeploymentId} for {ResourceName} because the Azure deployment probe failed.", deploymentId, resource.Name);
            return false;
        }
        catch (CredentialUnavailableException ex)
        {
            logger.LogDebug(ex, "Unable to adopt active Azure deployment {DeploymentId} for {ResourceName} because no Azure credential is available.", deploymentId, resource.Name);
            return false;
        }

        if (deployment is null)
        {
            // The active-deployment conflict raced with deletion or cleanup. Do not clear local state
            // here because this path runs while handling a failed create attempt; let the caller surface
            // the original ARM failure.
            logger.LogDebug("Unable to adopt active Azure deployment {DeploymentId} for {ResourceName} because it could not be found.", deploymentId, resource.Name);
            return false;
        }

        var currentLocation = GetConfiguredLocation(stateSection, effectiveLocation);
        if (IsActiveDeploymentProvisioningState(deployment.ProvisioningState))
        {
            // The deployment is still running, so wait on the existing ARM operation rather than
            // starting a duplicate deployment with the same name.
            logger.LogInformation("Azure deployment {DeploymentId} for {ResourceName} is already active. Waiting for the existing deployment to complete.", deploymentId, resource.Name);
            deployment = await WaitForCachedRunningDeploymentAsync(
                resource,
                context,
                deploymentId.ToString(),
                currentLocation,
                deployment,
                cancellationToken).ConfigureAwait(false);

            if (deployment is null)
            {
                // If the deployment disappears while we are waiting, adoption is no longer possible.
                // Return false so the caller keeps the original conflict behavior.
                logger.LogDebug("Unable to adopt active Azure deployment {DeploymentId} for {ResourceName} because it disappeared while waiting.", deploymentId, resource.Name);
                return false;
            }
        }

        // The conflict pointed at an existing deployment for this model. If it has
        // now reached a known terminal state, adopt that result instead of starting
        // another deployment.
        if (await TryApplyTerminalReconciledDeploymentStateAsync(resource, stateSection, deploymentId, deployment, currentLocation, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        // A non-terminal or unknown ARM state cannot be safely mapped to resource outputs or a dashboard
        // terminal state, so leave it unadopted and let the original active-deployment failure stand.
        logger.LogDebug("Unable to adopt active Azure deployment {DeploymentId} for {ResourceName} because provisioning state is {ProvisioningState}.", deploymentId, resource.Name, deployment.ProvisioningState);
        return false;
    }

    private static bool IsActiveDeploymentConflict(RequestFailedException ex)
        => ex.Status == 409 &&
           string.Equals(ex.ErrorCode, DeploymentActiveErrorCode, StringComparisons.AzureProvisioningErrorCode);

    private async Task TrackDeploymentOperationsAsync(
        AzureBicepResource resource,
        ProvisioningContext context,
        ResourceIdentifier deploymentId,
        string currentLocation,
        DeploymentOperationProgressTracker tracker,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PublishDeploymentOperationSummaryAsync(resource, context, deploymentId, currentLocation, tracker, force: false, cancellationToken).ConfigureAwait(false);
            await Task.Delay(s_deploymentOperationPollingInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AzureDeploymentOperationSummary> PublishDeploymentOperationSummaryAsync(
        AzureBicepResource resource,
        ProvisioningContext context,
        ResourceIdentifier deploymentId,
        string currentLocation,
        DeploymentOperationProgressTracker tracker,
        bool force,
        CancellationToken cancellationToken)
    {
        AzureDeploymentOperationSummary summary;
        try
        {
            summary = await GetDeploymentOperationSummaryAsync(context, deploymentId, currentLocation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Deployment operation polling is best-effort status reporting. The ARM deployment
            // operation itself remains the source of truth and will surface terminal failures.
            logger.LogDebug(ex, "Unable to query Azure deployment operations for {DeploymentId}.", deploymentId);
            return tracker.Current;
        }

        if (!tracker.TryUpdate(summary, force))
        {
            return summary;
        }

        await notificationService.PublishUpdateAsync(resource, state =>
        {
            var properties = WithoutDeploymentOperationProperties(state.Properties).SetResourcePropertyRange(CreateDeploymentOperationProperties(summary));
            if (summary.FailedOperations.FirstOrDefault(static operation => operation.FailureDetails is not null)?.FailureDetails is { } failureDetails)
            {
                properties = failureDetails.SetResourceProperties(properties, AzureProvisioningFailureDetails.ProvisionOperation);
            }

            return state with
            {
                State = CreateDeploymentOperationState(summary),
                Properties = properties
            };
        })
        .ConfigureAwait(false);

        return summary;
    }

    private async Task<AzureDeploymentOperationSummary> GetDeploymentOperationSummaryAsync(
        ProvisioningContext context,
        ResourceIdentifier deploymentId,
        string currentLocation,
        CancellationToken cancellationToken)
    {
        var operations = ImmutableArray.CreateBuilder<AzureDeploymentOperationDetails>();
        var runningOperations = ImmutableArray.CreateBuilder<AzureDeploymentOperationDetails>();
        var succeededOperations = ImmutableArray.CreateBuilder<AzureDeploymentOperationDetails>();
        var failedOperations = ImmutableArray.CreateBuilder<AzureDeploymentOperationDetails>();
        var canceledOperations = ImmutableArray.CreateBuilder<AzureDeploymentOperationDetails>();
        var enrichmentTasks = new List<(int OperationIndex, Task<AzureProvisioningFailureDetails> EnrichmentTask)>();

        await foreach (var operation in context.ArmClient.GetDeploymentOperationsAsync(deploymentId.ToString(), recursive: true, cancellationToken).ConfigureAwait(false))
        {
            if (operation.FailureDetails is { } failureDetails)
            {
                // Supported-location lookups are independent provider metadata calls. Start them as
                // operations arrive and await them together so multiple failed resources do not add
                // serial ARM round trips to failure reporting.
                enrichmentTasks.Add((operations.Count, EnrichFailureDetailsAsync(failureDetails, context, currentLocation, cancellationToken)));
            }

            operations.Add(operation);
        }

        if (enrichmentTasks.Count > 0)
        {
            await Task.WhenAll(enrichmentTasks.Select(static enrichment => enrichment.EnrichmentTask)).ConfigureAwait(false);

            foreach (var (operationIndex, enrichmentTask) in enrichmentTasks)
            {
                operations[operationIndex] = operations[operationIndex] with
                {
                    FailureDetails = await enrichmentTask.ConfigureAwait(false)
                };
            }
        }

        foreach (var operationDetails in operations)
        {
            if (operationDetails is not { IsCreateOperation: true, TargetResource: not null } ||
                operationDetails.IsNestedDeploymentCreate)
            {
                continue;
            }

            if (string.Equals(operationDetails.ProvisioningState, AzureDeploymentOperationDetails.RunningState, StringComparisons.AzureProvisioningState))
            {
                runningOperations.Add(operationDetails);
            }
            else if (string.Equals(operationDetails.ProvisioningState, AzureDeploymentOperationDetails.SucceededState, StringComparisons.AzureProvisioningState))
            {
                succeededOperations.Add(operationDetails);
            }
            else if (string.Equals(operationDetails.ProvisioningState, AzureDeploymentOperationDetails.FailedState, StringComparisons.AzureProvisioningState))
            {
                failedOperations.Add(operationDetails);
            }
            else if (string.Equals(operationDetails.ProvisioningState, AzureDeploymentOperationDetails.CanceledState, StringComparisons.AzureProvisioningState))
            {
                canceledOperations.Add(operationDetails);
            }
        }

        return new(
            Operations: operations.ToImmutable(),
            RunningOperations: runningOperations.ToImmutable(),
            SucceededOperations: succeededOperations.ToImmutable(),
            FailedOperations: failedOperations.ToImmutable(),
            CanceledOperations: canceledOperations.ToImmutable());
    }

    private async Task<AzureProvisioningFailureDetails> EnrichFailureDetailsAsync(
        AzureProvisioningFailureDetails failureDetails,
        ProvisioningContext context,
        string currentLocation,
        CancellationToken cancellationToken)
    {
        if (!failureDetails.IsLocationAvailabilityFailure)
        {
            return context.ExecutionContext.IsRunMode
                ? failureDetails
                : failureDetails.WithDeploymentRecommendedActions();
        }

        try
        {
            var supportedLocations = await context.ArmClient.GetSupportedLocationsAsync(context.Subscription.Id.Name, failureDetails.ResourceType!, cancellationToken).ConfigureAwait(false);
            var enrichedFailureDetails = failureDetails.WithLocationAvailability(currentLocation, supportedLocations);
            return context.ExecutionContext.IsRunMode
                ? enrichedFailureDetails
                : enrichedFailureDetails.WithDeploymentRecommendedActions();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Provider metadata is advisory diagnostic context. Preserve the provider error even
            // when ARM blocks or throttles the supported-location lookup.
            logger.LogDebug(ex, "Unable to query supported Azure locations for resource type {ResourceType}.", failureDetails.ResourceType);
            var enrichedFailureDetails = failureDetails.WithLocationAvailability(currentLocation, []);
            return context.ExecutionContext.IsRunMode
                ? enrichedFailureDetails
                : enrichedFailureDetails.WithDeploymentRecommendedActions();
        }
    }

    private static ImmutableArray<ResourcePropertySnapshot> CreateDeploymentOperationProperties(AzureDeploymentOperationSummary summary)
    {
        var properties = ImmutableArray.CreateBuilder<ResourcePropertySnapshot>();

        if (CreateDeploymentOperationSummaryProperty(summary) is { } summaryProperty)
        {
            properties.Add(summaryProperty);
        }

        properties.Add(new("azure.deployment.operations.total", summary.Operations.Length));
        properties.Add(new("azure.deployment.operations.running", summary.RunningOperations.Length));
        properties.Add(new("azure.deployment.operations.succeeded", summary.SucceededOperations.Length));
        properties.Add(new("azure.deployment.operations.failed", summary.FailedOperations.Length));
        properties.Add(new("azure.deployment.operations.canceled", summary.CanceledOperations.Length));

        AddResourceLabels("azure.deployment.operations.running.resources", summary.RunningOperations);
        AddResourceLabels("azure.deployment.operations.succeeded.resources", summary.SucceededOperations);
        AddResourceLabels("azure.deployment.operations.failed.resources", summary.FailedOperations);
        AddResourceLabels("azure.deployment.operations.canceled.resources", summary.CanceledOperations);

        return properties.ToImmutable();

        void AddResourceLabels(string propertyName, ImmutableArray<AzureDeploymentOperationDetails> operations)
        {
            var labels = CreateOperationResourceLabels(operations);
            if (labels.Length > 0)
            {
                properties.Add(new(propertyName, labels));
            }
        }
    }

    private static ResourcePropertySnapshot? CreateDeploymentOperationSummaryProperty(AzureDeploymentOperationSummary summary)
    {
        if (TryCreateSummaryValue(summary.FailedOperations, AzureProvisioningStrings.DeploymentOperationFailedResourcesFormat, out var failedValue))
        {
            return CreateSummaryProperty(failedValue);
        }

        if (TryCreateSummaryValue(summary.CanceledOperations, AzureProvisioningStrings.DeploymentOperationCanceledResourcesFormat, out var canceledValue))
        {
            return CreateSummaryProperty(canceledValue);
        }

        if (TryCreateSummaryValue(summary.RunningOperations, AzureProvisioningStrings.DeploymentOperationRunningResourcesFormat, out var runningValue))
        {
            return CreateSummaryProperty(runningValue);
        }

        return null;

        static bool TryCreateSummaryValue(ImmutableArray<AzureDeploymentOperationDetails> operations, string format, [NotNullWhen(true)] out string? value)
        {
            var labels = CreateOperationResourceLabels(operations);
            if (labels.Length > 0)
            {
                value = string.Format(CultureInfo.CurrentCulture, format, string.Join(", ", labels));
                return true;
            }

            value = null;
            return false;
        }

        static ResourcePropertySnapshot CreateSummaryProperty(string value)
        {
            return new(DeploymentOperationSummaryPropertyName, value)
            {
                DisplayName = AzureProvisioningStrings.DeploymentOperationSummaryDisplayName,
                IsHighlighted = true
            };
        }
    }

    private static ImmutableArray<ResourcePropertySnapshot> WithoutDeploymentOperationProperties(ImmutableArray<ResourcePropertySnapshot> properties)
    {
        if (properties.IsDefaultOrEmpty)
        {
            return [];
        }

        if (!properties.Any(static property => property.Name.StartsWith(DeploymentOperationPropertyPrefix, StringComparison.Ordinal)))
        {
            return properties;
        }

        // A later deployment can fail before operations exist, or the set of running/succeeded
        // resources can shrink between polls. Filter all old operation properties before publishing
        // new state so describe output never mixes a fresh failure with stale resource counters.
        return [.. properties.Where(static property => !property.Name.StartsWith(DeploymentOperationPropertyPrefix, StringComparison.Ordinal))];
    }

    private static string[] CreateOperationResourceLabels(IEnumerable<AzureDeploymentOperationDetails> operations)
    {
        return
        [
            .. operations
                .Select(static operation => operation.TargetResource switch
                {
                    { ResourceName.Length: > 0, ResourceType.Length: > 0 } target => $"{target.ResourceName} ({target.ResourceType})",
                    { ResourceName.Length: > 0 } target => target.ResourceName,
                    { ResourceType.Length: > 0 } target => target.ResourceType,
                    { Id.Length: > 0 } target => target.Id,
                    _ => null
                })
                .Where(static label => !string.IsNullOrEmpty(label))
                .Select(static label => label!)
                .Distinct(StringComparers.AzureResourceName)
                .OrderBy(static label => label, StringComparers.AzureResourceName)
        ];
    }

    private static ResourceStateSnapshot CreateDeploymentOperationState(AzureDeploymentOperationSummary summary)
    {
        if (summary.FailedOperations.Length > 0)
        {
            return new(AzureProvisioningStrings.ResourceStateAzureDeploymentFailed, KnownResourceStateStyles.Error);
        }

        var runningLabels = CreateOperationResourceLabels(summary.RunningOperations);
        if (runningLabels.Length > 0)
        {
            var runningText = runningLabels.Length == 1
                ? string.Format(CultureInfo.CurrentCulture, AzureProvisioningStrings.ResourceStateProvisioningResourceFormat, runningLabels[0])
                : string.Format(CultureInfo.CurrentCulture, AzureProvisioningStrings.ResourceStateProvisioningMultipleAzureResourcesFormat, runningLabels.Length);

            return new(runningText, KnownResourceStateStyles.Info);
        }

        if (summary.SucceededOperations.Length > 0)
        {
            return new(
                string.Format(CultureInfo.CurrentCulture, AzureProvisioningStrings.ResourceStateProvisionedMultipleAzureResourcesFormat, summary.SucceededOperations.Length),
                KnownResourceStateStyles.Info);
        }

        return new(AzureProvisioningController.WaitingForDeploymentState, KnownResourceStateStyles.Info);
    }

    private static void LogProvisioningFailure(ILogger resourceLogger, AzureProvisioningFailureDetails failureDetails)
    {
        resourceLogger.LogError("Azure provisioning failed: {Message}{Details}", failureDetails.ErrorMessage, CreateFailureLogDetails(failureDetails));
    }

    private static string CreateFailureLogDetails(AzureProvisioningFailureDetails failureDetails)
    {
        var details = new List<string>();

        AddDetail("code", failureDetails.ErrorCode);
        AddDetail("resource type", failureDetails.ResourceType);
        AddDetail("resource name", failureDetails.ResourceName);
        AddDetail("target resource", failureDetails.TargetResourceId);
        AddDetail("current location", failureDetails.CurrentLocation);
        AddDetail("request id", failureDetails.RequestId);
        AddDetail("correlation id", failureDetails.CorrelationId);

        return details.Count == 0 ? string.Empty : $" ({string.Join("; ", details)})";

        void AddDetail(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                details.Add($"{name}: {value}");
            }
        }
    }

    private static async Task<string> ResolveScopeValueAsync(object scopeValue, CancellationToken cancellationToken)
    {
        return scopeValue switch
        {
            string value => value,
            IValueProvider valueProvider => await valueProvider.GetValueAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("The Azure resource scope value cannot be null."),
            _ => throw new NotSupportedException($"The scope value type {scopeValue.GetType()} is not supported.")
        };
    }

    private async Task<bool> TryCancelDeploymentAsync(IArmDeploymentCollection deployments, string deploymentName, ILogger resourceLogger, bool treatMissingOrInactiveAsCanceled)
    {
        try
        {
            await deployments.CancelAsync(deploymentName, CancellationToken.None).ConfigureAwait(false);
            resourceLogger.LogInformation("Cancellation requested for Azure deployment {DeploymentName}.", deploymentName);
            return true;
        }
        catch (RequestFailedException ex) when (treatMissingOrInactiveAsCanceled && (ex.Status == 404 || ex.Status == 409))
        {
            logger.LogInformation(ex, "Azure deployment {DeploymentName} was already absent or no longer active during cancellation.", deploymentName);
            resourceLogger.LogInformation("Azure deployment {DeploymentName} was already absent or no longer active during cancellation.", deploymentName);
            return true;
        }
        catch (RequestFailedException ex)
        {
            logger.LogWarning(ex, "Failed to cancel Azure deployment {DeploymentName}.", deploymentName);
            resourceLogger.LogWarning("Failed to cancel Azure deployment {DeploymentName}: {Message}", deploymentName, ex.Message);
            return false;
        }
    }

    private static void UpdateDeploymentState(
        DeploymentStateSection stateSection,
        string? locationOverride,
        ResourceIdentifier deploymentId,
        JsonObject parameters,
        JsonObject? outputObj,
        JsonObject? scope,
        string checksum,
        string effectiveLocation,
        string? provisioningState)
    {
        stateSection.Data.Clear();

        // Only preserve a per-resource override when it still matches the resource we just deployed. This keeps
        // run-mode reprovisioning sticky while allowing global context changes to clear stale overrides naturally.
        if (!string.IsNullOrEmpty(locationOverride) &&
            string.Equals(locationOverride, effectiveLocation, StringComparisons.AzureLocation))
        {
            stateSection.Data[AzureProvisioningController.LocationOverrideKey] = locationOverride;
        }

        stateSection.Data[BicepUtilities.DeploymentStateIdKey] = deploymentId.ToString();
        stateSection.Data[BicepUtilities.DeploymentStateParametersKey] = parameters.ToJsonString();

        if (outputObj is not null)
        {
            stateSection.Data[BicepUtilities.DeploymentStateOutputsKey] = outputObj.ToJsonString();
        }

        if (scope is not null)
        {
            stateSection.Data[BicepUtilities.DeploymentStateScopeKey] = scope.ToJsonString();
        }

        stateSection.Data[BicepUtilities.DeploymentStateChecksumKey] = checksum;

        if (!string.IsNullOrEmpty(provisioningState))
        {
            stateSection.Data[DeploymentStateProvisioningStateKey] = provisioningState;
        }
    }

    private void ConfigureSecretResolver(IAzureKeyVaultResource kvr)
    {
        var resource = (AzureBicepResource)kvr;

        var vaultUri = resource.Outputs[kvr.VaultUriOutputReference.Name] as string ?? throw new InvalidOperationException($"{kvr.VaultUriOutputReference.Name} not found in outputs.");

        // Set the client for resolving secrets at runtime
        var client = secretClientProvider.GetSecretClient(new(vaultUri));
        kvr.SecretResolver = async (secretRef, ct) =>
        {
            var secret = await client.GetSecretAsync(secretRef.SecretName, cancellationToken: ct).ConfigureAwait(false);
            return secret.Value.Value;
        };
    }

    private static void PopulateWellKnownParameters(AzureBicepResource resource, ProvisioningContext context)
    {
        static void ValidateUnknownPrincipalParameter(ProvisioningContext context)
        {
            // Well-known principal parameters can only be populated in run mode.
            // In publish mode, principal parameters must be provided by the creator of the bicep resource.

            // We assume that the BicepProvisioner only runs in publish mode during `aspire deploy` operations
            // and not from azd. azd fills in principal parameters during its deployment process with a managed
            // identity it creates. But the BicepProvisioner only fills them in with the current principal,
            // which is not correct in publish mode.
            if (context.ExecutionContext.IsPublishMode)
            {
                throw new InvalidOperationException("An Azure principal parameter was not supplied a value. Ensure you are using an environment that supports role assignments, for example AddAzureContainerAppEnvironment.");
            }
        }

        if (resource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.PrincipalId, out var principalId) && principalId is null)
        {
            ValidateUnknownPrincipalParameter(context);

            resource.Parameters[AzureBicepResource.KnownParameters.PrincipalId] = context.Principal.Id;
        }

        if (resource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.PrincipalName, out var principalName) && principalName is null)
        {
            ValidateUnknownPrincipalParameter(context);

            resource.Parameters[AzureBicepResource.KnownParameters.PrincipalName] = context.Principal.Name;
        }

        if (resource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.PrincipalType, out var principalType) && principalType is null)
        {
            ValidateUnknownPrincipalParameter(context);

            resource.Parameters[AzureBicepResource.KnownParameters.PrincipalType] = "User";
        }

        if (!resource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.Location, out var location) || location is null)
        {
            resource.Parameters[AzureBicepResource.KnownParameters.Location] = context.Location.Name;
        }
    }

    public static string GetDeploymentUrl(ResourceIdentifier deploymentId) =>
        AzurePortalUrls.GetDeploymentUrl(deploymentId);

    private static string GetDeploymentTargetName(bool isTenantScoped, bool isSubscriptionScoped, ISubscriptionResource subscription, IResourceGroupResource resourceGroup)
    {
        if (isTenantScoped)
        {
            return "tenant";
        }

        if (isSubscriptionScoped)
        {
            return $"subscription {subscription.Id.Name}";
        }

        return $"resource group {resourceGroup.Name}";
    }

    private static ResourceIdentifier GetDeploymentId(bool isTenantScoped, bool isSubscriptionScoped, ISubscriptionResource subscription, IResourceGroupResource resourceGroup, string deploymentName)
    {
        var deploymentPath = isTenantScoped
            ? $"/providers/Microsoft.Resources/deployments/{deploymentName}"
            : isSubscriptionScoped
                ? $"{subscription.Id}/providers/Microsoft.Resources/deployments/{deploymentName}"
                : $"{resourceGroup.Id}/providers/Microsoft.Resources/deployments/{deploymentName}";

        return new(deploymentPath);
    }

    private async Task<AzureContextState> GetCurrentAzureContextAsync(ResourceIdentifier? deploymentId, CancellationToken cancellationToken)
    {
        var section = await deploymentStateManager.AcquireSectionAsync("Azure", cancellationToken).ConfigureAwait(false);

        return new AzureContextState(
            GetStateValue(section, "SubscriptionId") ?? deploymentId?.SubscriptionId,
            deploymentId is not null ? deploymentId.ResourceGroupName : GetStateValue(section, "ResourceGroup"),
            GetStateValue(section, "TenantId"),
            GetStateValue(section, "Tenant"),
            GetStateValue(section, "Location"));
    }

    private static string? GetStateValue(DeploymentStateSection section, string key) =>
        section.Data[key]?.GetValue<string>() is { Length: > 0 } value ? value : null;

    private static string GetConfiguredLocation(DeploymentStateSection section, string? fallbackLocation)
    {
        if (section.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>() is { Length: > 0 } locationOverride)
        {
            return locationOverride;
        }

        if (section.Data[BicepUtilities.DeploymentStateParametersKey]?.GetValue<string>() is { Length: > 0 } parametersJson)
        {
            try
            {
                if (JsonNode.Parse(parametersJson)?[AzureBicepResource.KnownParameters.Location]?["value"]?.GetValue<string>() is { Length: > 0 } configuredLocation)
                {
                    return configuredLocation;
                }
            }
            catch
            {
            }
        }

        return fallbackLocation ?? string.Empty;
    }

    private static string GetEffectiveLocation(AzureBicepResource resource, ProvisioningContext context) =>
        resource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.Location, out var location) && location is not null
            ? location.ToString() ?? context.Location.ToString()
            : context.Location.ToString();

    private sealed class DeploymentOperationProgressTracker
    {
        private string? _signature;

        public AzureDeploymentOperationSummary Current { get; private set; } = AzureDeploymentOperationSummary.Empty;

        public bool TryUpdate(AzureDeploymentOperationSummary summary, bool force)
        {
            var signature = CreateSignature(summary);
            if (!force && string.Equals(signature, _signature, StringComparison.Ordinal))
            {
                return false;
            }

            Current = summary;
            _signature = signature;
            return true;
        }

        private static string CreateSignature(AzureDeploymentOperationSummary summary)
        {
            return string.Join(
                "|",
                summary.Operations
                    .OrderBy(static operation => operation.OperationId, StringComparer.Ordinal)
                    .Select(static operation => $"{operation.OperationId}:{operation.ProvisioningState}:{operation.TargetResource?.Id}:{operation.FailureDetails?.ErrorCode}"));
        }
    }

    private sealed record AzureContextState(string? SubscriptionId, string? ResourceGroup, string? TenantId, string? TenantDomain, string? Location);
}
