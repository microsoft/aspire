// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Pipelines;
using Azure;
using Azure.Core;
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
    internal const string DeploymentStateProvisioningStateSucceeded = "Succeeded";

    private const string DeploymentOperationPropertyPrefix = "azure.deployment.operations.";
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
            portalUrls.Add(new(Name: "deployment", Url: GetDeploymentUrl(id), IsInternal: false));
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
                State = new("Provisioned", KnownResourceStateStyles.Success),
                Urls = [.. portalUrls],
                Properties = props
            };
        }).ConfigureAwait(false);

        return true;
    }

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
            State = new("Starting", KnownResourceStateStyles.Info),
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
                State = new("Compiling ARM template", KnownResourceStateStyles.Info)
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
                    State = new("Azure deployment failed", KnownResourceStateStyles.Error),
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
                Urls = [.. state.Urls, new(Name: "deployment", Url: url, IsInternal: false)],
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
                State = new("Provisioned", KnownResourceStateStyles.Success),
                CreationTimeStamp = _timeProvider.GetUtcNow().UtcDateTime,
                Properties = properties
            };
        })
        .ConfigureAwait(false);
    }

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
            return new("Azure deployment failed", KnownResourceStateStyles.Error);
        }

        var runningLabels = CreateOperationResourceLabels(summary.RunningOperations);
        if (runningLabels.Length > 0)
        {
            var runningText = runningLabels.Length == 1
                ? $"Provisioning {runningLabels[0]}"
                : $"Provisioning {runningLabels.Length} Azure resources";

            return new(runningText, KnownResourceStateStyles.Info);
        }

        if (summary.SucceededOperations.Length > 0)
        {
            return new($"Provisioned {summary.SucceededOperations.Length} Azure resources", KnownResourceStateStyles.Info);
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
