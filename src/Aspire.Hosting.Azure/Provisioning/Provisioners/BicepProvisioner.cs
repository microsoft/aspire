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
            var props = state.Properties.SetResourcePropertyRange([
                new("azure.subscription.id", azureContext.SubscriptionId),
                new("azure.resource.group", azureContext.ResourceGroup),
                new("azure.tenant.id", azureContext.TenantId),
                new("azure.tenant.domain", azureContext.TenantDomain),
                new("azure.location", configuredLocation),
                new(CustomResourceKnownProperties.Source, deploymentId)
            ]);

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
        var resourceLogger = loggerService.GetLogger(resource);

        if (BicepUtilities.GetExistingResourceGroup(resource) is { } existingResourceGroup)
        {
            var existingResourceGroupName = existingResourceGroup is ParameterResource parameterResource
                ? (await parameterResource.GetValueAsync(cancellationToken).ConfigureAwait(false))!
                : (string)existingResourceGroup;
            var response = await context.Subscription.GetResourceGroups().GetAsync(existingResourceGroupName, cancellationToken).ConfigureAwait(false);
            resourceGroup = response.Value;
        }

        var effectiveLocation = GetEffectiveLocation(resource, context);

        await notificationService.PublishUpdateAsync(resource, state => state with
        {
            ResourceType = resource.GetType().Name,
            State = new("Starting", KnownResourceStateStyles.Info),
            Properties = state.Properties.SetResourcePropertyRange([
                new("azure.subscription.id", context.Subscription.Id.Name),
                new("azure.resource.group", resourceGroup.Id.Name),
                new("azure.tenant.id", context.Tenant.TenantId?.ToString()),
                new("azure.tenant.domain", context.Tenant.DefaultDomain),
                new("azure.location", effectiveLocation),
            ])
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

        var isSubscriptionScopedDeployment = resource.Scope?.Subscription != null;
        // Resources with a Subscription scope should use a subscription-level deployment.
        var deployments = isSubscriptionScopedDeployment
            ? context.Subscription.GetArmDeployments()
            : resourceGroup.GetArmDeployments();
        var deploymentName = executionContext.IsPublishMode ? $"{resource.Name}-{_timeProvider.GetUtcNow().ToUnixTimeSeconds()}" : resource.Name;
        var deploymentId = GetDeploymentId(context, resourceGroup, deploymentName, isSubscriptionScopedDeployment);
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

        resourceLogger.LogInformation("Deploying {Name} to {ResourceGroup}", resource.Name, resourceGroup.Name);
        logger.LogDebug("Starting deployment of resource {ResourceName} to resource group {ResourceGroupName}", resource.Name, resourceGroup.Name);

        var deploymentContent = new ArmDeploymentContent(new(ArmDeploymentMode.Incremental)
        {
            Template = BinaryData.FromString(armTemplateContents),
            Parameters = BinaryData.FromObjectAsJson(parameters),
            DebugSettingDetailLevel = "ResponseContent"
        });
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

        try
        {
            await operation.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
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

        sw.Stop();
        resourceLogger.LogInformation("Deployment of {Name} to {ResourceGroup} took {Elapsed}", resource.Name, resourceGroup.Name, sw.Elapsed);
        logger.LogDebug("Deployment of resource {ResourceName} to resource group {ResourceGroupName} completed in {Elapsed}", resource.Name, resourceGroup.Name, sw.Elapsed);

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

            throw new InvalidOperationException($"Deployment of {resource.Name} to {resourceGroup.Name} failed with {provisioningState}");
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
            ImmutableArray<ResourcePropertySnapshot> properties = state.Properties.SetResourcePropertyRange([
                new("azure.subscription.id", context.Subscription.Id.Name),
                new("azure.resource.group", resourceGroup.Id.Name),
                new("azure.tenant.id", context.Tenant.TenantId?.ToString()),
                new("azure.tenant.domain", context.Tenant.DefaultDomain),
                new("azure.location", effectiveLocation),
                new(CustomResourceKnownProperties.Source, deployment.Id.ToString())
            ]);

            return state with
            {
                State = new("Provisioned", KnownResourceStateStyles.Success),
                CreationTimeStamp = _timeProvider.GetUtcNow().UtcDateTime,
                Properties = properties
            };
        })
        .ConfigureAwait(false);
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
            string.Equals(locationOverride, effectiveLocation, StringComparison.OrdinalIgnoreCase))
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

    private static ResourceIdentifier GetDeploymentId(ProvisioningContext provisioningContext, IResourceGroupResource resourceGroup, string deploymentName, bool isSubscriptionScopedDeployment)
    {
        var deploymentPath = isSubscriptionScopedDeployment
            ? $"{provisioningContext.Subscription.Id}/providers/Microsoft.Resources/deployments/{deploymentName}"
            : $"{provisioningContext.Subscription.Id}/resourceGroups/{resourceGroup.Name}/providers/Microsoft.Resources/deployments/{deploymentName}";

        return new(deploymentPath);
    }

    private async Task<AzureContextState> GetCurrentAzureContextAsync(ResourceIdentifier? deploymentId, CancellationToken cancellationToken)
    {
        var section = await deploymentStateManager.AcquireSectionAsync("Azure", cancellationToken).ConfigureAwait(false);

        return new AzureContextState(
            GetStateValue(section, "SubscriptionId") ?? deploymentId?.SubscriptionId,
            deploymentId?.ResourceGroupName ?? GetStateValue(section, "ResourceGroup"),
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

    private sealed record AzureContextState(string? SubscriptionId, string? ResourceGroup, string? TenantId, string? TenantDomain, string? Location);
}
