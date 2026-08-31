// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES004
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Hashing;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

internal static class AzureSandboxContainerDeployment
{
    private const string SandboxStateParentSection = "Azure:Sandboxes";
    internal const string SandboxStateSectionPrefix = $"{SandboxStateParentSection}:";
    private const int DiskImageReadyTimeoutSeconds = 600;
    private const int PublicEndpointTimeoutSeconds = 180;
    private static readonly IReadOnlySet<string> s_noExcludedIds = new HashSet<string>(StringComparer.Ordinal);

    public static IEnumerable<PipelineStep> CreatePipelineSteps(AzureSandboxContainerResource resource)
    {
        var deployStepName = GetDeployStepName(resource);
        var destroyStepName = GetDestroyStepName(resource);

        return
        [
            new PipelineStep
            {
                Name = deployStepName,
                Description = $"Deploys compute resource '{resource.TargetResource.Name}' to ACA sandbox '{resource.Name}'.",
                Action = context => DeployAsync(context, resource),
                DependsOnSteps = [AzureEnvironmentResource.ProvisionInfrastructureStepName, WellKnownPipelineSteps.DeployPrereq],
                RequiredBySteps = [WellKnownPipelineSteps.Deploy],
                Tags = [WellKnownPipelineTags.DeployCompute],
                Resource = resource
            },
            new PipelineStep
            {
                Name = destroyStepName,
                Description = $"Deletes ACA sandbox deployment '{resource.Name}'.",
                Action = context => DestroyAsync(context, resource),
                DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq],
                RequiredBySteps = [WellKnownPipelineSteps.Destroy],
                Resource = resource
            }
        ];
    }

    internal static PipelineStep CreateStaleCleanupPipelineStep(IResource resource, IReadOnlySet<string> activeStateSectionNames)
    {
        return new PipelineStep
        {
            Name = GetStaleCleanupStepName(),
            Description = "Deletes stale ACA sandbox deployments.",
            Action = context => DestroyStaleDeploymentsAsync(context, activeStateSectionNames),
            DependsOnSteps = [WellKnownPipelineSteps.DestroyPrereq],
            RequiredBySteps = [WellKnownPipelineSteps.Destroy],
            Resource = resource
        };
    }

    internal static IReadOnlySet<string> GetActiveStateSectionNames(DistributedApplicationModel model)
    {
        var activeStateSectionNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var resource in model.GetComputeResources())
        {
            if (!resource.TryGetAnnotationsOfType<DeploymentTargetAnnotation>(out var deploymentTargetAnnotations))
            {
                continue;
            }

            foreach (var deploymentTargetAnnotation in deploymentTargetAnnotations)
            {
                if (deploymentTargetAnnotation.DeploymentTarget is AzureSandboxContainerResource sandboxContainer)
                {
                    activeStateSectionNames.Add(GetStateSectionName(sandboxContainer));
                }
            }
        }

        return activeStateSectionNames;
    }

    internal static void ConfigureStaleCleanupDestroyOrdering(PipelineConfigurationContext context)
    {
        var cleanupStepName = GetStaleCleanupStepName();

        foreach (var step in GetAzureEnvironmentDestroySteps(context))
        {
            step.DependsOn(cleanupStepName);
        }
    }

    public static void ConfigureDestroyOrdering(PipelineConfigurationContext context, AzureSandboxContainerResource resource)
    {
        var destroyStepName = GetDestroyStepName(resource);

        foreach (var step in GetAzureEnvironmentDestroySteps(context))
        {
            step.DependsOn(destroyStepName);
        }
    }

    public static async Task ConfigureDeployOrderingAsync(PipelineConfigurationContext context, AzureSandboxContainerResource resource)
    {
        var pushSteps = context.GetSteps(resource.TargetResource, WellKnownPipelineTags.PushContainerImage);
        var deploySteps = context.GetSteps(resource, WellKnownPipelineTags.DeployCompute).ToArray();

        deploySteps.DependsOn(pushSteps);

        if (resource.Parent.ContainerRegistry is { } registry)
        {
            deploySteps.DependsOn(context.GetSteps(registry, "acr-login"));
        }

        var executionContext = context.Services.GetRequiredService<DistributedApplicationExecutionContext>();
        var dependencies = await resource.TargetResource.GetResourceDependenciesAsync(
            executionContext,
            ResourceDependencyDiscoveryMode.DirectOnly).ConfigureAwait(false);
        foreach (var dependency in dependencies)
        {
            if (dependency.GetDeploymentTargetAnnotation()?.DeploymentTarget is not AzureSandboxContainerResource producer)
            {
                continue;
            }

            if (!ReferenceEquals(producer.Parent, resource.Parent))
            {
                throw new NotSupportedException(
                    $"Azure sandbox resource '{resource.TargetResource.Name}' in sandbox group '{resource.Parent.Name}' references resource '{dependency.Name}' in sandbox group '{producer.Parent.Name}', but cross-group sandbox references are not supported. Deploy both resources to the same sandbox group.");
            }

            var producerSteps = context.GetSteps(producer, WellKnownPipelineTags.DeployCompute);
            foreach (var consumerStep in deploySteps)
            {
                foreach (var producerStep in producerSteps)
                {
                    if (WouldCreateDependencyCycle(context.Steps, consumerStep, producerStep))
                    {
                        throw new InvalidOperationException(
                            $"Azure sandbox resources '{resource.TargetResource.Name}' and '{producer.TargetResource.Name}' have a circular deployment dependency.");
                    }

                    consumerStep.DependsOn(producerStep);
                }
            }
        }
    }

    private static IEnumerable<PipelineStep> GetAzureEnvironmentDestroySteps(PipelineConfigurationContext context)
    {
        foreach (var environment in context.Model.Resources.OfType<AzureEnvironmentResource>())
        {
            var expectedName = $"destroy-azure-{environment.Name}";
            foreach (var step in context.GetSteps(environment).Where(step => string.Equals(step.Name, expectedName, StringComparison.Ordinal)))
            {
                yield return step;
            }
        }
    }

    private static bool WouldCreateDependencyCycle(
        IReadOnlyList<PipelineStep> steps,
        PipelineStep consumer,
        PipelineStep producer)
    {
        if (string.Equals(consumer.Name, producer.Name, StringComparison.Ordinal))
        {
            return true;
        }

        var stepLookup = steps.ToDictionary(static step => step.Name, StringComparer.Ordinal);
        var pending = new Stack<string>(producer.DependsOnSteps);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryPop(out var stepName))
        {
            if (!visited.Add(stepName))
            {
                continue;
            }

            if (string.Equals(stepName, consumer.Name, StringComparison.Ordinal))
            {
                return true;
            }

            if (stepLookup.TryGetValue(stepName, out var step))
            {
                foreach (var dependency in step.DependsOnSteps)
                {
                    pending.Push(dependency);
                }
            }
        }

        return false;
    }

    private static async Task DeployAsync(PipelineStepContext context, AzureSandboxContainerResource resource)
    {
        var targetResource = resource.TargetResource;
        var endpoints = ResolveSandboxEndpoints(resource);
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        var dataPlaneScope = CreateDataPlaneScope(resource.Parent);
        var client = CreateAzureDevComputeClient(context);
        var configuration = context.Services.GetRequiredService<IConfiguration>();
        var environmentName = context.Services.GetRequiredService<IHostEnvironment>().EnvironmentName;
        var appHostIdentity = GetStableAppHostIdentity(configuration);

        // Creating, committing, and pruning form one reconciliation transaction. The deployment-state
        // manager serializes state writes, but remote ADC operations happen outside those writes, so
        // concurrent processes must hold a resource-scoped lease across the complete transaction.
        using var deploymentLease = await AcquireDeploymentLeaseAsync(
            deploymentStateManager,
            appHostIdentity,
            environmentName,
            GetStateSectionName(resource),
            context.CancellationToken).ConfigureAwait(false);
        var stateSection = await deploymentStateManager.AcquireSectionAsync(GetStateSectionName(resource), context.CancellationToken).ConfigureAwait(false);
        ValidateDeploymentScope(stateSection, dataPlaneScope);
        var previousStateSection = CloneStateSection(stateSection);
        var ownerId = CreateStableOwnerId(
            appHostIdentity,
            environmentName,
            dataPlaneScope,
            resource.Name);
        var previousOwnerId = previousStateSection.Data["OwnerId"]?.GetValue<string>();
        var ownerChanged = !string.IsNullOrWhiteSpace(previousOwnerId) &&
            !string.Equals(previousOwnerId, ownerId, StringComparison.Ordinal);
        var legacyOwnerId = CreateLegacyStableOwnerId(
            appHostIdentity,
            dataPlaneScope,
            resource.Name);
        var pendingOwnerCleanupIds = GetPendingOwnerCleanupIds(previousStateSection, ownerId, legacyOwnerId);
        var pendingLegacyDeploymentCleanup = CreatePendingLegacyDeploymentCleanup(
            previousStateSection,
            ownerId,
            legacyOwnerId);
        stateSection.Data["OwnerId"] = ownerId;
        SetPendingOwnerCleanupIds(stateSection, pendingOwnerCleanupIds);
        SetPendingLegacyDeploymentCleanup(stateSection, pendingLegacyDeploymentCleanup);
        SetRecoveryStateIfMissing(stateSection, dataPlaneScope, resource);
        await deploymentStateManager.SaveSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);

        var deployId = Guid.NewGuid().ToString("N");
        var diskImageId = string.Empty;
        var sandboxId = string.Empty;
        var addedPorts = new List<SandboxEndpoint>();
        var deploymentCommitted = false;

        try
        {
            var imageReference = await ResolveContainerImageAsync(context, resource).ConfigureAwait(false);
            // Capture presence before command callbacks run because callbacks can mutate annotations.
            // The deployed command may still contain their resolved values after such mutation.
            var hasModeledCommandConfiguration = HasModeledCommandConfiguration(targetResource);
            var imageMetadata = await ResolveContainerImageMetadataAsync(context, targetResource, imageReference).ConfigureAwait(false);
            var diskImageReference = await ResolveContainerImageReferenceForDiskImageAsync(context, imageReference).ConfigureAwait(false);
            var diskImageName = CreateSandboxResourceName(targetResource.Name, deployId);

            var diskTask = await context.ReportingStep.CreateTaskAsync($"Creating sandbox disk image for {targetResource.Name}", context.CancellationToken).ConfigureAwait(false);
            await using (diskTask.ConfigureAwait(false))
            {
                var diskImage = await CreateWithResponseLossCleanupAsync(
                    () => CreateDiskImageAsync(context, client, dataPlaneScope, resource, diskImageReference, diskImageName, ownerId, deployId),
                    context,
                    client,
                    dataPlaneScope,
                    ownerId,
                    resource.Name,
                    deployId).ConfigureAwait(false);
                diskImageId = diskImage.Id;
                diskImage = await WaitForDiskImageReadyAsync(context, client, dataPlaneScope, diskImage).ConfigureAwait(false);
                await diskTask.CompleteAsync($"Created sandbox disk image {diskImageId}", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }

            var environmentVariables = new Dictionary<string, string>(imageMetadata.EnvironmentVariables, StringComparer.Ordinal);
            var resolvedEnvironmentVariables = await ResolveEnvironmentVariablesAsync(context, targetResource).ConfigureAwait(false);
            foreach (var (key, value) in resolvedEnvironmentVariables.Values)
            {
                environmentVariables[key] = value;
            }
            AddManagedIdentityEnvironmentVariables(targetResource, environmentVariables);
            var identitySettings = ResolveIdentitySettings(targetResource);
            var egressPolicy = CreateEgressPolicy(
                resolvedEnvironmentVariables.EgressHosts.Concat(imageMetadata.EgressHosts));

            var createTask = await context.ReportingStep.CreateTaskAsync($"Creating sandbox for {targetResource.Name}", context.CancellationToken).ConfigureAwait(false);
            await using (createTask.ConfigureAwait(false))
            {
                var sandbox = await CreateWithResponseLossCleanupAsync(
                    () => CreateSandboxAsync(context, client, dataPlaneScope, resource, diskImageId, environmentVariables, imageMetadata, identitySettings, egressPolicy, ownerId, deployId),
                    context,
                    client,
                    dataPlaneScope,
                    ownerId,
                    resource.Name,
                    deployId).ConfigureAwait(false);
                sandboxId = sandbox.Id;
                await createTask.CompleteAsync($"Created sandbox {sandboxId}", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }

            if (CreateLifecyclePolicy(resource) is { } lifecycle)
            {
                var lifecycleTask = await context.ReportingStep.CreateTaskAsync($"Configuring lifecycle for {resource.Name}", context.CancellationToken).ConfigureAwait(false);
                await using (lifecycleTask.ConfigureAwait(false))
                {
                    await client.SetLifecycleAsync(
                        dataPlaneScope,
                        sandboxId,
                        lifecycle,
                        context.CancellationToken).ConfigureAwait(false);
                    await lifecycleTask.CompleteAsync("Lifecycle policy configured", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
                }
            }

            var portStates = new JsonArray();
            foreach (var endpoint in endpoints)
            {
                var exposeTask = await context.ReportingStep.CreateTaskAsync($"Exposing sandbox port {endpoint.TargetPort}", context.CancellationToken).ConfigureAwait(false);
                await using (exposeTask.ConfigureAwait(false))
                {
                    var addedPort = await AddPortAsync(context, client, dataPlaneScope, sandboxId, endpoint).ConfigureAwait(false);
                    addedPorts.Add(endpoint);

                    var endpointUrl = addedPort.Url.ToString();
                    if (endpoint.IsExternal && endpoint.IsHttp)
                    {
                        await WaitForPublicHttpAsync(endpointUrl, GetPublicEndpointReadyTimeout(resource), context.CancellationToken).ConfigureAwait(false);
                    }

                    portStates.Add(new JsonObject
                    {
                        ["Name"] = endpoint.Name,
                        ["Port"] = endpoint.TargetPort,
                        ["Url"] = endpointUrl,
                        ["IsExternal"] = endpoint.IsExternal,
                        ["IsHttp"] = endpoint.IsHttp,
                        ["Protocol"] = endpoint.Protocol,
                        ["Anonymous"] = endpoint.Anonymous
                    });

                    await exposeTask.CompleteAsync(new MarkdownString($"Public URL: [{endpointUrl}]({endpointUrl})"), CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
                }
            }

            var endpointSecurityFingerprint = CreateDeploymentSecurityFingerprint(
                diskImageReference,
                endpoints,
                identitySettings,
                egressPolicy);
            var securityConfigurationChanged = HasSecurityRelevantEndpointChange(
                previousStateSection,
                endpointSecurityFingerprint,
                resolvedEnvironmentVariables.Values.Count > 0,
                hasModeledCommandConfiguration);
            var pendingSecurityCleanup = previousStateSection.Data["PendingSecurityCleanup"]?.GetValue<bool>() == true;
            securityConfigurationChanged |= pendingSecurityCleanup;

            stateSection.Data.Clear();
            stateSection.Data["OwnerId"] = ownerId;
            SetPendingOwnerCleanupIds(stateSection, pendingOwnerCleanupIds);
            SetPendingLegacyDeploymentCleanup(stateSection, pendingLegacyDeploymentCleanup);
            stateSection.Data["SandboxId"] = sandboxId;
            stateSection.Data["DiskImageId"] = diskImageId;
            stateSection.Data["SubscriptionId"] = dataPlaneScope.SubscriptionId;
            stateSection.Data["ResourceGroup"] = dataPlaneScope.ResourceGroupName;
            stateSection.Data["Location"] = dataPlaneScope.Region;
            stateSection.Data["SandboxGroup"] = dataPlaneScope.SandboxGroupName;
            stateSection.Data["ResourceName"] = resource.Name;
            stateSection.Data["SourceResourceName"] = targetResource.Name;
            stateSection.Data["DeployId"] = deployId;
            stateSection.Data["Ports"] = portStates;
            stateSection.Data["EndpointSecurityFingerprint"] = endpointSecurityFingerprint;
            stateSection.Data["HasRuntimeEnvironmentConfiguration"] = resolvedEnvironmentVariables.Values.Count > 0;
            stateSection.Data["HasRuntimeCommandConfiguration"] = hasModeledCommandConfiguration;
            stateSection.Data["PendingSecurityCleanup"] = securityConfigurationChanged;
            await deploymentStateManager.SaveSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);
            deploymentCommitted = true;

            // Endpoint consumers resolve sandbox URL values during provisioning, before this
            // deployment step can expose the new ADC proxy URL. Keep the previous deployment
            // alive so resources configured in this deploy can continue using the URL they
            // just received. Security-relevant endpoint changes and owner-ID migrations prune the
            // previous generation immediately so an old anonymous or differently exposed endpoint
            // does not remain reachable after a successful deployment.
            try
            {
                var excludedDeployIds = securityConfigurationChanged
                    ? new HashSet<string>(StringComparer.Ordinal) { deployId }
                    : GetExcludedDeployIds(deployId, previousStateSection);
                var excludedSandboxIds = securityConfigurationChanged
                    ? new HashSet<string>(StringComparer.Ordinal) { sandboxId }
                    : GetExcludedResourceIds(sandboxId, previousStateSection, "SandboxId");
                var excludedDiskImageIds = securityConfigurationChanged
                    ? new HashSet<string>(StringComparer.Ordinal) { diskImageId }
                    : GetExcludedResourceIds(diskImageId, previousStateSection, "DiskImageId");

                await DeleteRemoteDeploymentsByResourceLabelAsync(
                    context,
                    client,
                    dataPlaneScope,
                    ownerId,
                    resource.Name,
                    excludedDeployIds,
                    excludedSandboxIds,
                    excludedDiskImageIds,
                    throwOnError: securityConfigurationChanged).ConfigureAwait(false);

                if (pendingLegacyDeploymentCleanup is not null)
                {
                    await DeleteExistingDeploymentAsync(
                        context,
                        client,
                        dataPlaneScope,
                        new DeploymentStateSection(
                            stateSection.SectionName,
                            pendingLegacyDeploymentCleanup,
                            version: 0),
                        throwOnError: true).ConfigureAwait(false);
                }

                foreach (var pendingOwnerCleanupId in pendingOwnerCleanupIds)
                {
                    await DeleteRemoteDeploymentsByResourceLabelAsync(
                        context,
                        client,
                        dataPlaneScope,
                        pendingOwnerCleanupId,
                        resource.Name,
                        s_noExcludedIds,
                        s_noExcludedIds,
                        s_noExcludedIds,
                        throwOnError: true).ConfigureAwait(false);
                }

                if (securityConfigurationChanged ||
                    pendingOwnerCleanupIds.Count > 0 ||
                    pendingLegacyDeploymentCleanup is not null)
                {
                    stateSection.Data["PendingSecurityCleanup"] = false;
                    stateSection.Data.Remove("PendingOwnerCleanupIds");
                    stateSection.Data.Remove("PendingLegacyDeploymentCleanup");
                    await deploymentStateManager.SaveSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Logger.LogWarning(
                    ex,
                    "Best-effort pruning failed after Azure sandbox deployment '{ResourceName}' completed. The new deployment remains active and its state was preserved.",
                    resource.Name);
                if (securityConfigurationChanged)
                {
                    throw new InvalidOperationException(
                        $"The new Azure sandbox deployment '{resource.Name}' succeeded, but the previous generation could not be removed after a security-relevant endpoint change. The new deployment state was preserved, but the deployment is reported as failed so the older endpoint is not silently treated as secured.",
                        ex);
                }
            }

            if (portStates.FirstOrDefault() is JsonObject firstPort && firstPort["Url"]?.GetValue<string>() is { } publicUrl)
            {
                var retainedUrl = securityConfigurationChanged || ownerChanged
                    ? null
                    : GetFirstStateUrl(previousStateSection);
                context.Summary.Add(resource.Name, new MarkdownString(CreateSandboxUrlSummary(publicUrl, retainedUrl)));
            }
            else
            {
                context.Summary.Add(resource.Name, new MarkdownString($"Sandbox `{sandboxId}`"));
            }
        }
        catch
        {
            if (!deploymentCommitted)
            {
                using var deploymentCleanupCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try
                {
                    if (!string.IsNullOrWhiteSpace(sandboxId))
                    {
                        await DeleteSandboxAsync(
                            context,
                            client,
                            dataPlaneScope,
                            sandboxId,
                            addedPorts.Select(static endpoint => endpoint.TargetPort),
                            throwOnError: false,
                            deploymentCleanupCts.Token).ConfigureAwait(false);
                    }

                    if (!string.IsNullOrWhiteSpace(diskImageId))
                    {
                        await DeleteDiskImageAsync(
                            context,
                            client,
                            dataPlaneScope,
                            diskImageId,
                            throwOnError: false,
                            deploymentCleanupCts.Token).ConfigureAwait(false);
                    }

                    await CleanupFailedDeploymentAsync(
                        context,
                        client,
                        dataPlaneScope,
                        ownerId,
                        resource.Name,
                        deployId,
                        deploymentCleanupCts.Token).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    context.Logger.LogWarning(
                        cleanupException,
                        "Failed to reconcile Azure sandbox resources after deployment '{DeployId}' failed.",
                        deployId);
                }
            }

            throw;
        }
    }

    private static IAzureDevComputeClient CreateAzureDevComputeClient(PipelineStepContext context)
    {
        var httpClientFactory = context.Services.GetRequiredService<IHttpClientFactory>();
        var tokenCredentialProvider = context.Services.GetRequiredService<ITokenCredentialProvider>();
        return new AzureDevComputeClient(httpClientFactory.CreateClient(), tokenCredentialProvider.TokenCredential, context.Logger);
    }

    private static Dictionary<string, string> CreateLabels(AzureSandboxContainerResource resource, string ownerId, string deployId)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aspire-owner"] = ownerId,
            ["aspire-resource"] = resource.Name,
            ["aspire-source"] = resource.TargetResource.Name,
            ["aspire-deploy"] = deployId
        };
    }

    internal static AzureDevComputeSandboxLifecyclePolicy? CreateLifecyclePolicy(AzureSandboxContainerResource resource)
    {
        var options = GetAzureSandboxContainerOptions(resource.TargetResource);
        var hasAutoSuspendOverride = options?.AutoSuspendEnabled is not null;
        var hasAutoDeleteOverride = options?.AutoDeleteEnabled is not null;

        if (!hasAutoSuspendOverride && !hasAutoDeleteOverride)
        {
            return null;
        }

        return new AzureDevComputeSandboxLifecyclePolicy
        {
            AutoSuspendPolicy = hasAutoSuspendOverride ? new AzureDevComputeSandboxAutoSuspendPolicy
            {
                Enabled = options!.AutoSuspendEnabled!.Value,
                Interval = ToInt32Seconds(options?.AutoSuspendInterval, nameof(AzureSandboxOptions.AutoSuspendInterval)),
                Mode = options?.AutoSuspendMode?.ToString()
            } : null,
            AutoDeletePolicy = hasAutoDeleteOverride ? new AzureDevComputeSandboxAutoDeletePolicy
            {
                Enabled = options!.AutoDeleteEnabled!.Value,
                DeleteIntervalInSeconds = ToInt64Seconds(options.AutoDeleteInterval, nameof(AzureSandboxOptions.AutoDeleteInterval)),
                Trigger = options.AutoDeleteTrigger?.ToString()
            } : null
        };
    }

    private static int? ToInt32Seconds(TimeSpan? value, string propertyName)
    {
        if (value is null)
        {
            return null;
        }

        if (value < TimeSpan.Zero || value.Value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new InvalidOperationException($"{propertyName} must be a non-negative whole-second duration.");
        }

        var seconds = value.Value.Ticks / TimeSpan.TicksPerSecond;
        return seconds <= int.MaxValue
            ? (int)seconds
            : throw new InvalidOperationException($"{propertyName} exceeds the maximum supported ADC interval.");
    }

    private static long? ToInt64Seconds(TimeSpan? value, string propertyName)
    {
        if (value is null)
        {
            return null;
        }

        if (value < TimeSpan.Zero || value.Value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new InvalidOperationException($"{propertyName} must be a non-negative whole-second duration.");
        }

        return value.Value.Ticks / TimeSpan.TicksPerSecond;
    }

    private static async Task<AzureDevComputeDiskImage> CreateDiskImageAsync(
        PipelineStepContext context,
        IAzureDevComputeClient client,
        AzureDevComputeResourceScope scope,
        AzureSandboxContainerResource resource,
        string imageReference,
        string diskImageName,
        string ownerId,
        string deployId)
    {
        var managedIdentityClientId = await ResolveImagePullManagedIdentityClientIdAsync(
            resource.Parent,
            imageReference,
            context.CancellationToken).ConfigureAwait(false);

        return await client.CreateDiskImageAsync(
            scope,
            new AzureDevComputeCreateDiskImageRequest
            {
                Name = diskImageName,
                Labels = CreateLabels(resource, ownerId, deployId),
                Source = new AzureDevComputeDiskImageSource
                {
                    ImageUrl = imageReference,
                    ManagedIdentityClientId = managedIdentityClientId
                }
            },
            context.CancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string?> ResolveImagePullManagedIdentityClientIdAsync(
        AzureSandboxGroupResource sandboxGroup,
        string imageReference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sandboxGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        var registry = sandboxGroup.ContainerRegistry;
        if (registry is null)
        {
            return null;
        }

        var registryEndpoint = await registry.RegistryEndpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(registryEndpoint) ||
            !imageReference.StartsWith($"{registryEndpoint}/", StringComparison.OrdinalIgnoreCase))
        {
            // ADC rejects an ACR managed identity when it is supplied for a public registry such as
            // Docker Hub. Authenticate only images hosted by the ACR selected for this sandbox group.
            return null;
        }

        return GetRequiredOutput(sandboxGroup, AzureSandboxGroupResource.ImagePullIdentityClientIdOutputName);
    }

    private static async Task<AzureDevComputeDiskImage> WaitForDiskImageReadyAsync(
        PipelineStepContext context,
        IAzureDevComputeClient client,
        AzureDevComputeResourceScope scope,
        AzureDevComputeDiskImage diskImage)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(DiskImageReadyTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (IsDiskImageReady(diskImage))
            {
                return diskImage;
            }

            if (IsTerminalDiskImageFailure(diskImage))
            {
                throw CreateDiskImageFailureException(diskImage);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), context.CancellationToken).ConfigureAwait(false);
            diskImage = await client.GetDiskImageAsync(scope, diskImage.Id, context.CancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Sandbox disk image '{diskImage.Id}' was not ready after {DiskImageReadyTimeoutSeconds} seconds (last state: '{diskImage.Status.State}').");
    }

    private static bool IsDiskImageReady(AzureDevComputeDiskImage diskImage) =>
        string.Equals(diskImage.Status.State, "Ready", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalDiskImageFailure(AzureDevComputeDiskImage diskImage) =>
        diskImage.Status.State.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
        diskImage.Status.State.Contains("error", StringComparison.OrdinalIgnoreCase);

    internal static InvalidOperationException CreateDiskImageFailureException(AzureDevComputeDiskImage diskImage)
    {
        ArgumentNullException.ThrowIfNull(diskImage);
        return new InvalidOperationException(
            $"Sandbox disk image '{diskImage.Id}' failed to become ready (terminal state: '{diskImage.Status.State}'). Service-provided error details were redacted.");
    }

    private static Task<AzureDevComputeSandbox> CreateSandboxAsync(
        PipelineStepContext context,
        IAzureDevComputeClient client,
        AzureDevComputeResourceScope scope,
        AzureSandboxContainerResource resource,
        string diskImageId,
        IReadOnlyDictionary<string, string> environmentVariables,
        ContainerImageMetadata imageMetadata,
        List<AzureDevComputeIdentitySetting>? identitySettings,
        AzureDevComputeSandboxEgressPolicy egressPolicy,
        string ownerId,
        string deployId)
    {
        return client.CreateSandboxAsync(
            scope,
            new AzureDevComputeSandboxRequest
            {
                Labels = CreateLabels(resource, ownerId, deployId),
                Environment = environmentVariables.Count > 0 ? environmentVariables : null,
                IdentitySettings = identitySettings,
                SkipEgressProxy = false,
                EgressPolicy = egressPolicy,
                Entrypoint = imageMetadata.Entrypoint.Count > 0 ? [.. imageMetadata.Entrypoint] : null,
                Cmd = imageMetadata.Command.Count > 0 ? [.. imageMetadata.Command] : null,
                WorkingDirectory = imageMetadata.WorkingDirectory,
                SourcesRef = new AzureDevComputeSandboxSource
                {
                    DiskImage = new AzureDevComputeSandboxDiskImageSource
                    {
                        Id = diskImageId,
                        IsPublic = false
                    }
                },
                Resources = CreateSandboxResources(resource)
            },
            context.CancellationToken);
    }

    private static List<AzureDevComputeIdentitySetting>? ResolveIdentitySettings(IResource resource)
    {
        if (!resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var appIdentityAnnotation) ||
            appIdentityAnnotation.IdentityResource is not AzureUserAssignedIdentityResource userAssignedIdentity)
        {
            return null;
        }

        var identityId = GetRequiredOutput(userAssignedIdentity, "id");
        return
        [
            new AzureDevComputeIdentitySetting
            {
                // ADC serves this user-assigned identity through the sandbox managed-identity endpoint.
                // "All" keeps it available during both startup and the main container lifetime.
                Identity = identityId,
                Lifecycle = "All"
            }
        ];
    }

    private static void AddManagedIdentityEnvironmentVariables(IResource resource, Dictionary<string, string> environmentVariables)
    {
        if (!resource.TryGetLastAnnotation<AppIdentityAnnotation>(out var appIdentityAnnotation) ||
            appIdentityAnnotation.IdentityResource is not AzureUserAssignedIdentityResource userAssignedIdentity)
        {
            return;
        }

        environmentVariables.TryAdd("AZURE_CLIENT_ID", GetRequiredOutput(userAssignedIdentity, "clientId"));
    }

    internal static AzureDevComputeSandboxResources CreateSandboxResources(AzureSandboxContainerResource resource)
    {
        var options = GetAzureSandboxContainerOptions(resource.TargetResource);
        return (options?.Tier ?? AzureSandboxTier.Medium) switch
        {
            AzureSandboxTier.ExtraSmall => new() { Cpu = "250m", Memory = "512Mi", Disk = "20480Mi" },
            AzureSandboxTier.Small => new() { Cpu = "500m", Memory = "1024Mi", Disk = "20480Mi" },
            AzureSandboxTier.Medium => new() { Cpu = "1000m", Memory = "2048Mi", Disk = "20480Mi" },
            AzureSandboxTier.Large => new() { Cpu = "2000m", Memory = "4096Mi", Disk = "40960Mi" },
            AzureSandboxTier.ExtraLarge => new() { Cpu = "4000m", Memory = "8192Mi", Disk = "81920Mi" },
            _ => throw new InvalidOperationException($"Unsupported Azure sandbox tier '{options?.Tier}'.")
        };
    }

    internal static AzureDevComputeSandboxEgressPolicy CreateEgressPolicy(IEnumerable<string> allowedHosts)
    {
        var normalizedHosts = allowedHosts
            .Where(static host => Uri.CheckHostName(host) is not UriHostNameType.Unknown)
            .Where(IsOutboundHost)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AzureDevComputeSandboxEgressPolicy
        {
            DefaultAction = "Deny",
            TrafficInspection = "Full",
            HostRules =
            [
                .. normalizedHosts.Select(static host => new AzureDevComputeSandboxEgressHostRule
                {
                    Action = "Allow",
                    Pattern = host
                })
            ]
        };
    }

    internal static async Task<FileLock?> AcquireDeploymentLeaseAsync(
        IDeploymentStateManager deploymentStateManager,
        string appHostIdentity,
        string environmentName,
        string stateSectionName,
        CancellationToken cancellationToken)
    {
        if (deploymentStateManager.StateFilePath is not { Length: > 0 } stateFilePath)
        {
            return null;
        }

        var stateDirectory = Path.GetDirectoryName(Path.GetFullPath(stateFilePath))
            ?? throw new InvalidOperationException("The deployment state file must have a parent directory.");
        var deploymentsDirectory = Path.GetDirectoryName(stateDirectory) ?? stateDirectory;
        var lockIdentity = $"{appHostIdentity}\0{environmentName.ToLowerInvariant()}\0{stateSectionName}";
        var lockName = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(lockIdentity)).ToString("x16", CultureInfo.InvariantCulture);
        var lockPath = Path.Combine(
            deploymentsDirectory,
            ".locks",
            $"azure-sandbox-{lockName}.lock");

        return await FileLock.AcquireAsync(lockPath, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<string> GetOutboundHttpHosts(string value)
    {
        if (TryGetOutboundHttpHost(value, out var host))
        {
            return [host];
        }

        try
        {
            // Aspire connection references can resolve to composite values such as:
            //   Endpoint=https://account.blob.core.windows.net;ContainerName=uploads
            // Use the connection-string parser so quoted values containing semicolons remain intact.
            var builder = new DbConnectionStringBuilder { ConnectionString = value };
            return builder.Values
                .Cast<object>()
                .Select(static candidate => Convert.ToString(candidate, CultureInfo.InvariantCulture))
                .Select(static candidate => TryGetOutboundHttpHost(candidate, out var candidateHost) ? candidateHost : null)
                .OfType<string>();
        }
        catch (ArgumentException)
        {
            // Most environment values are not connection strings. Values that are neither a direct URI
            // nor a parseable composite value do not describe an outbound host.
            return [];
        }
    }

    private static bool TryGetOutboundHttpHost(string? value, [NotNullWhen(true)] out string? host)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            IsOutboundHost(uri.IdnHost))
        {
            host = uri.IdnHost;
            return true;
        }

        host = null;
        return false;
    }

    private static bool IsOutboundHost([NotNullWhen(true)] string? host)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            host is "*" or "+" or "::" or "[::]")
        {
            return false;
        }

        return !IPAddress.TryParse(host, out var address) ||
            !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);
    }

    internal static void ValidateSandboxCompatibility(IResource resource)
    {
        if (resource.TryGetContainerMounts(out var mounts) && mounts.Any())
        {
            throw new NotSupportedException($"Resource '{resource.Name}' configures container mounts, but Azure sandbox volume provisioning is not supported by this preview integration.");
        }
    }

    private static async Task<AzureDevComputeSandboxPort> AddPortAsync(
        PipelineStepContext context,
        IAzureDevComputeClient client,
        AzureDevComputeResourceScope scope,
        string sandboxId,
        SandboxEndpoint endpoint)
    {
        var ports = await client.AddPortAsync(
            scope,
            sandboxId,
            new AzureDevComputeAddPortRequest
            {
                Name = endpoint.Name,
                Port = endpoint.TargetPort,
                Auth = endpoint.IsExternal
                    ? new AzureDevComputePortAuthConfig
                    {
                        Anonymous = endpoint.Anonymous ?? false
                    }
                    : null,
                Protocol = endpoint.Protocol
            },
            context.CancellationToken).ConfigureAwait(false);

        return ports.FirstOrDefault(port => port.Port == endpoint.TargetPort)
            ?? throw new InvalidOperationException($"The ADC port add response did not contain port '{endpoint.TargetPort}' for sandbox '{sandboxId}'.");
    }

    private static async Task<string> ResolveContainerImageAsync(PipelineStepContext context, AzureSandboxContainerResource resource)
    {
        if (resource.TargetResource.RequiresImageBuildAndPush())
        {
            var containerImageReference = new ContainerImageReference(resource.TargetResource);
            return await ((IValueProvider)containerImageReference)
                .GetValueAsync(new ValueProviderContext { ExecutionContext = context.ExecutionContext, Caller = resource.TargetResource }, context.CancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Could not resolve the pushed container image for resource '{resource.TargetResource.Name}'.");
        }

        if (resource.TargetResource.TryGetContainerImageName(out var imageName))
        {
            return imageName;
        }

        throw new NotSupportedException($"Resource '{resource.TargetResource.Name}' cannot be deployed to Azure sandbox group '{resource.Parent.Name}' because it does not produce or reference a container image.");
    }

    private static async Task<string> ResolveContainerImageReferenceForDiskImageAsync(PipelineStepContext context, string imageReference)
    {
        var runtime = await ResolveContainerRuntimeAsync(context).ConfigureAwait(false);
        return await ResolveContainerImageReferenceForDiskImageAsync(
            runtime,
            imageReference,
            context.CancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string> ResolveContainerImageReferenceForDiskImageAsync(
        IContainerRuntime runtime,
        string imageReference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        var result = await runtime.InspectImageManifestAsync(imageReference, cancellationToken).ConfigureAwait(false);
        if (result.Status == ContainerImageInspectionStatus.Unsupported)
        {
            throw new NotSupportedException(
                $"Container runtime '{runtime.Name}' does not support image manifest inspection, which is required for Azure sandbox deployment.");
        }

        if (result.Status == ContainerImageInspectionStatus.Failed)
        {
            throw new InvalidOperationException(
                result.ErrorMessage ?? $"Container runtime failed to inspect image manifest '{imageReference}'.");
        }

        if (!result.TryGetManifest("linux", "amd64", out var manifest))
        {
            throw new InvalidOperationException(
                $"Container image '{imageReference}' does not contain a linux/amd64 manifest with an immutable digest.");
        }

        return CreateDigestImageReference(imageReference, manifest.Digest);
    }

    private static string CreateDigestImageReference(string imageReference, string digest)
    {
        var digestSeparator = imageReference.IndexOf('@');
        if (digestSeparator >= 0)
        {
            return $"{imageReference[..digestSeparator]}@{digest}";
        }

        var lastSlash = imageReference.LastIndexOf('/');
        var lastColon = imageReference.LastIndexOf(':');
        var repository = lastColon > lastSlash ? imageReference[..lastColon] : imageReference;

        return $"{repository}@{digest}";
    }

    private static async Task<ContainerImageMetadata> ResolveContainerImageMetadataAsync(PipelineStepContext context, IResource resource, string imageReference)
    {
        var modeledCommand = await ResolveModeledCommandAsync(context, resource).ConfigureAwait(false);
        if (resource is not ContainerResource || !resource.RequiresImageBuildAndPush())
        {
            return new ContainerImageMetadata(
                modeledCommand.Entrypoint ?? [],
                modeledCommand.Command ?? [],
                new Dictionary<string, string>(StringComparer.Ordinal),
                WorkingDirectory: null,
                modeledCommand.EgressHosts);
        }

        var metadata = await InspectLocalContainerImageAsync(context, imageReference).ConfigureAwait(false);
        return metadata with
        {
            Entrypoint = modeledCommand.Entrypoint ?? metadata.Entrypoint,
            Command = modeledCommand.Command ?? metadata.Command,
            EgressHosts = modeledCommand.EgressHosts
        };
    }

    internal static async Task<ResolvedModeledCommand> ResolveModeledCommandAsync(PipelineStepContext context, IResource resource)
    {
        var args = new List<object>();
        if (resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var callbacks))
        {
            var callbackContext = new CommandLineArgsCallbackContext(args, resource, context.CancellationToken)
            {
                ExecutionContext = context.ExecutionContext,
                Logger = context.Logger
            };

            foreach (var callback in callbacks)
            {
                await callback.Callback(callbackContext).ConfigureAwait(false);
            }
        }

        var resolvedArgs = new List<string>();
        var egressHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args)
        {
            var resolvedArg = await ResolveValueWithEgressHostsAsync(context, resource, arg).ConfigureAwait(false);
            resolvedArgs.Add(resolvedArg.Value);
            egressHosts.UnionWith(resolvedArg.EgressHosts);
        }

        var entrypoint = resource is ContainerResource container && !string.IsNullOrWhiteSpace(container.Entrypoint)
            ? new[] { container.Entrypoint }
            : null;
        var command = resolvedArgs.Count == 0 ? null : resolvedArgs;

        return new ResolvedModeledCommand(entrypoint, command, egressHosts);
    }

    internal static bool HasModeledCommandConfiguration(IResource resource) =>
        resource is ContainerResource { Entrypoint: { Length: > 0 } } ||
        resource.TryGetAnnotationsOfType<CommandLineArgsCallbackAnnotation>(out var callbacks) && callbacks.Any();

    private static async Task<ContainerImageMetadata> InspectLocalContainerImageAsync(PipelineStepContext context, string imageReference)
    {
        var runtime = await ResolveContainerRuntimeAsync(context).ConfigureAwait(false);
        var result = await runtime.InspectImageConfigAsync(imageReference, context.CancellationToken).ConfigureAwait(false);
        if (result.Status == ContainerImageInspectionStatus.Unsupported)
        {
            throw new NotSupportedException(
                $"Container runtime '{runtime.Name}' does not support image configuration inspection, which is required for Azure sandbox deployment.");
        }

        if (result.Status == ContainerImageInspectionStatus.Failed)
        {
            throw new InvalidOperationException(
                result.ErrorMessage ?? $"Container runtime failed to inspect image configuration '{imageReference}'.");
        }

        if (!result.TryGetConfig(out var config))
        {
            throw new InvalidOperationException($"Container runtime did not return image configuration for '{imageReference}'.");
        }

        return new ContainerImageMetadata(
            config.Entrypoint,
            config.Command,
            new Dictionary<string, string>(StringComparer.Ordinal),
            config.WorkingDirectory,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static Task<IContainerRuntime> ResolveContainerRuntimeAsync(PipelineStepContext context)
    {
        return context.Services.GetRequiredService<IContainerRuntimeResolver>().ResolveAsync(context.CancellationToken);
    }

    internal static async Task<ResolvedEnvironmentVariables> ResolveEnvironmentVariablesAsync(PipelineStepContext context, IResource resource)
    {
        var environmentVariables = new Dictionary<string, object>();
        if (resource.TryGetAnnotationsOfType<EnvironmentCallbackAnnotation>(out var callbacks))
        {
            var callbackContext = new EnvironmentCallbackContext(context.ExecutionContext, resource, environmentVariables, context.CancellationToken)
            {
                Logger = context.Logger
            };

            foreach (var callback in callbacks)
            {
                await callback.Callback(callbackContext).ConfigureAwait(false);
            }
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var egressHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in environmentVariables)
        {
            var resolvedValue = await ResolveValueWithEgressHostsAsync(context, resource, value).ConfigureAwait(false);
            result[key] = resolvedValue.Value;
            egressHosts.UnionWith(resolvedValue.EgressHosts);
        }

        return new ResolvedEnvironmentVariables(result, egressHosts);
    }

    internal static async Task<string> ResolveValueAsync(PipelineStepContext context, IResource resource, object? value)
        => (await ResolveValueWithEgressHostsAsync(context, resource, value).ConfigureAwait(false)).Value;

    internal static async Task<ResolvedValue> ResolveValueWithEgressHostsAsync(PipelineStepContext context, IResource resource, object? value)
    {
        var currentComputeEnvironment = resource.GetComputeEnvironment() ?? resource.GetDeploymentTargetAnnotation()?.ComputeEnvironment;

        while (true)
        {
            switch (value)
            {
                case null:
                    return new ResolvedValue(string.Empty, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                case string s:
                    return new ResolvedValue(s, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                case IResourceWithConnectionString connectionStringResource:
                    value = connectionStringResource.ConnectionStringExpression;
                    continue;
                case EndpointReference endpointReference
                    when TryResolveEndpointReferenceValue(endpointReference, currentComputeEnvironment, out var endpointExpression):
                    return await ResolveEndpointValueAsync(
                        context,
                        resource,
                        endpointExpression,
                        EndpointProperty.Url).ConfigureAwait(false);
                case EndpointReferenceExpression endpointReferenceExpression
                    when TryResolveEndpointReferenceValue(endpointReferenceExpression, currentComputeEnvironment, out var endpointExpression):
                    return await ResolveEndpointValueAsync(
                        context,
                        resource,
                        endpointExpression,
                        endpointReferenceExpression.Property).ConfigureAwait(false);
                case ReferenceExpression referenceExpression:
                    return await ResolveReferenceExpressionAsync(context, resource, referenceExpression).ConfigureAwait(false);
                case IValueProvider valueProvider:
                    var providedValue = await valueProvider
                        .GetValueAsync(new ValueProviderContext { ExecutionContext = context.ExecutionContext, Caller = resource }, context.CancellationToken)
                        .ConfigureAwait(false) ?? string.Empty;
                    return new ResolvedValue(providedValue, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                default:
                    return new ResolvedValue(value.ToString() ?? string.Empty, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        }

        static async Task<ResolvedValue> ResolveEndpointValueAsync(
            PipelineStepContext context,
            IResource resource,
            object endpointExpression,
            EndpointProperty property)
        {
            var resolved = await ResolveValueWithEgressHostsAsync(context, resource, endpointExpression).ConfigureAwait(false);
            var egressHosts = new HashSet<string>(resolved.EgressHosts, StringComparer.OrdinalIgnoreCase);
            switch (property)
            {
                case EndpointProperty.Url:
                    egressHosts.UnionWith(GetOutboundHttpHosts(resolved.Value));
                    break;
                case EndpointProperty.Host or EndpointProperty.IPV4Host:
                    if (Uri.CheckHostName(resolved.Value) is not UriHostNameType.Unknown &&
                        IsOutboundHost(resolved.Value))
                    {
                        egressHosts.Add(resolved.Value);
                    }
                    break;
                case EndpointProperty.HostAndPort:
                    if (Uri.TryCreate($"{Uri.UriSchemeHttp}://{resolved.Value}", UriKind.Absolute, out var endpointUri) &&
                        IsOutboundHost(endpointUri.IdnHost))
                    {
                        egressHosts.Add(endpointUri.IdnHost);
                    }
                    break;
            }

            return new ResolvedValue(resolved.Value, egressHosts);
        }

        static async Task<ResolvedValue> ResolveReferenceExpressionAsync(
            PipelineStepContext context,
            IResource resource,
            ReferenceExpression expression)
        {
            if (expression.IsConditional)
            {
                var condition = await ResolveValueAsync(context, resource, expression.Condition).ConfigureAwait(false);
                var branch = string.Equals(condition, expression.MatchValue, StringComparison.OrdinalIgnoreCase)
                    ? expression.WhenTrue
                    : expression.WhenFalse;
                return branch is null
                    ? new ResolvedValue(string.Empty, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    : await ResolveReferenceExpressionAsync(context, resource, branch).ConfigureAwait(false);
            }

            var arguments = new object?[expression.ValueProviders.Count];
            var egressHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < expression.ValueProviders.Count; i++)
            {
                var resolved = await ResolveValueWithEgressHostsAsync(context, resource, expression.ValueProviders[i]).ConfigureAwait(false);
                egressHosts.UnionWith(resolved.EgressHosts);
                arguments[i] = expression.StringFormats[i] is { } format
                    ? FormatReferenceValue(resolved.Value, format)
                    : resolved.Value;
            }

            return new ResolvedValue(
                string.Format(CultureInfo.InvariantCulture, expression.Format, arguments),
                egressHosts);
        }

        static string FormatReferenceValue(string value, string format)
        {
            return format.ToLowerInvariant() switch
            {
                "uri" => Uri.EscapeDataString(value),
                _ => throw new NotSupportedException($"The format '{format}' is not supported. Supported formats are 'uri' (encodes a URI).")
            };
        }
    }

    internal static bool TryResolveEndpointReferenceValue(EndpointReference endpointReference, IComputeEnvironmentResource? currentComputeEnvironment, [NotNullWhen(true)] out ReferenceExpression? expression)
    {
        return TryResolveEndpointReferenceValue(endpointReference.Property(EndpointProperty.Url), currentComputeEnvironment, out expression);
    }

    internal static bool TryResolveEndpointReferenceValue(EndpointReferenceExpression endpointReferenceExpression, IComputeEnvironmentResource? currentComputeEnvironment, [NotNullWhen(true)] out ReferenceExpression? expression)
    {
        if (currentComputeEnvironment is AzureSandboxGroupResource sandboxGroup &&
            ComputeEnvironmentEndpointResolver.TryGetEffectiveComputeEnvironment(endpointReferenceExpression.Endpoint.Resource, out var owningComputeEnvironment) &&
            ReferenceEquals(owningComputeEnvironment, sandboxGroup))
        {
            expression = sandboxGroup.GetEndpointPropertyExpression(endpointReferenceExpression);
            return true;
        }

        return ComputeEnvironmentEndpointResolver.TryGetCrossEnvironmentEndpointExpression(endpointReferenceExpression, [currentComputeEnvironment], out expression);
    }

    internal static async Task DestroyAsync(PipelineStepContext context, AzureSandboxContainerResource resource)
    {
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        var appHostIdentity = GetStableAppHostIdentity(context.Services.GetRequiredService<IConfiguration>());
        var environmentName = context.Services.GetRequiredService<IHostEnvironment>().EnvironmentName;
        using var deploymentLease = await AcquireDeploymentLeaseAsync(
            deploymentStateManager,
            appHostIdentity,
            environmentName,
            GetStateSectionName(resource),
            context.CancellationToken).ConfigureAwait(false);
        var stateSection = await deploymentStateManager.AcquireSectionAsync(GetStateSectionName(resource), context.CancellationToken).ConfigureAwait(false);
        var ownerId = stateSection.Data["OwnerId"]?.GetValue<string>();
        if (!HasRemoteDeploymentState(stateSection))
        {
            AzureDevComputeResourceScope fallbackScope;
            try
            {
                fallbackScope = CreateDataPlaneScope(resource.Parent);
            }
            catch (InvalidOperationException ex)
            {
                context.Logger.LogWarning(ex, "Sandbox deployment state and Azure scope outputs were unavailable, so data-plane cleanup could not run before the Azure resource group cleanup.");
                await context.ReportingStep.CompleteAsync(
                    "No sandbox deployment state or stable cleanup scope was available.",
                    CompletionState.CompletedWithWarning,
                    context.CancellationToken).ConfigureAwait(false);
                return;
            }

            var stableOwnerId = CreateStableOwnerId(appHostIdentity, environmentName, fallbackScope, resource.Name);
            await DeleteRemoteDeploymentsByResourceLabelAsync(
                context,
                CreateAzureDevComputeClient(context),
                fallbackScope,
                stableOwnerId,
                resource.Name,
                s_noExcludedIds,
                s_noExcludedIds,
                s_noExcludedIds,
                throwOnError: true).ConfigureAwait(false);
            await context.ReportingStep.CompleteAsync(
                "No local sandbox deployment state was found; stable ownership labels were used for cleanup.",
                CompletionState.Completed,
                context.CancellationToken).ConfigureAwait(false);
            return;
        }

        var client = CreateAzureDevComputeClient(context);
        var scope = new AzureDevComputeResourceScope(
            GetRequiredStateValue(stateSection, "SubscriptionId"),
            GetRequiredStateValue(stateSection, "ResourceGroup"),
            GetRequiredStateValue(stateSection, "SandboxGroup"),
            GetRequiredStateValue(stateSection, "Location"));
        var legacyOwnerId = CreateLegacyStableOwnerId(appHostIdentity, scope, resource.Name);

        await DeleteExistingDeploymentAsync(context, client, scope, stateSection, throwOnError: true).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(ownerId) &&
            !string.Equals(ownerId, legacyOwnerId, StringComparison.Ordinal))
        {
            await DeleteRemoteDeploymentsByResourceLabelAsync(context, client, scope, ownerId, resource.Name, s_noExcludedIds, s_noExcludedIds, s_noExcludedIds, throwOnError: true).ConfigureAwait(false);
        }
        if (GetPendingLegacyDeploymentCleanup(stateSection) is JsonObject pendingLegacyDeploymentCleanup)
        {
            await DeleteExistingDeploymentAsync(
                context,
                client,
                scope,
                new DeploymentStateSection(stateSection.SectionName, pendingLegacyDeploymentCleanup, version: 0),
                throwOnError: true).ConfigureAwait(false);
        }
        foreach (var pendingOwnerCleanupId in GetPendingOwnerCleanupIds(stateSection, ownerId))
        {
            await DeleteRemoteDeploymentsByResourceLabelAsync(context, client, scope, pendingOwnerCleanupId, resource.Name, s_noExcludedIds, s_noExcludedIds, s_noExcludedIds, throwOnError: true).ConfigureAwait(false);
        }
        var stableOwnerIdForScope = CreateStableOwnerId(appHostIdentity, environmentName, scope, resource.Name);
        if (!string.Equals(ownerId, stableOwnerIdForScope, StringComparison.Ordinal))
        {
            await DeleteRemoteDeploymentsByResourceLabelAsync(context, client, scope, stableOwnerIdForScope, resource.Name, s_noExcludedIds, s_noExcludedIds, s_noExcludedIds, throwOnError: true).ConfigureAwait(false);
        }
        await deploymentStateManager.DeleteSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);
    }

    private static async Task DestroyStaleDeploymentsAsync(PipelineStepContext context, IReadOnlySet<string> activeStateSectionNames)
    {
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        // Legacy deployment state was shared by every polyglot AppHost in the directory.
        // Stale enumeration must only inspect sections already claimed by this AppHost;
        // direct per-resource reads still use legacy fallback to migrate owned state.
        var sandboxesSection = await deploymentStateManager.AcquireCurrentSectionAsync(SandboxStateParentSection, context.CancellationToken).ConfigureAwait(false);

        var staleResourceNames = sandboxesSection.Data
            .Where(pair => pair.Value is JsonObject)
            .Select(pair => $"{SandboxStateSectionPrefix}{pair.Key}")
            .Where(sectionName => !activeStateSectionNames.Contains(sectionName))
            .ToArray();

        foreach (var sectionName in staleResourceNames)
        {
            using var deploymentLease = await AcquireDeploymentLeaseAsync(
                deploymentStateManager,
                GetStableAppHostIdentity(context.Services.GetRequiredService<IConfiguration>()),
                context.Services.GetRequiredService<IHostEnvironment>().EnvironmentName,
                sectionName,
                context.CancellationToken).ConfigureAwait(false);
            var stateSection = await deploymentStateManager.AcquireSectionAsync(sectionName, context.CancellationToken).ConfigureAwait(false);
            if (!HasRemoteDeploymentState(stateSection))
            {
                await deploymentStateManager.DeleteSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);
                continue;
            }

            var client = CreateAzureDevComputeClient(context);
            var scope = new AzureDevComputeResourceScope(
                GetRequiredStateValue(stateSection, "SubscriptionId"),
                GetRequiredStateValue(stateSection, "ResourceGroup"),
                GetRequiredStateValue(stateSection, "SandboxGroup"),
                GetRequiredStateValue(stateSection, "Location"));

            var cleanupTask = await context.ReportingStep.CreateTaskAsync($"Deleting stale sandbox deployment {sectionName}", context.CancellationToken).ConfigureAwait(false);
            await using (cleanupTask.ConfigureAwait(false))
            {
                var resourceName = GetStateResourceName(stateSection, sectionName);
                var legacyOwnerId = CreateLegacyStableOwnerId(
                    GetStableAppHostIdentity(context.Services.GetRequiredService<IConfiguration>()),
                    scope,
                    resourceName);
                await DeleteExistingDeploymentAsync(context, client, scope, stateSection, throwOnError: true).ConfigureAwait(false);
                if (stateSection.Data["OwnerId"]?.GetValue<string>() is { Length: > 0 } ownerId &&
                    !string.Equals(ownerId, legacyOwnerId, StringComparison.Ordinal))
                {
                    await DeleteRemoteDeploymentsByResourceLabelAsync(context, client, scope, ownerId, resourceName, s_noExcludedIds, s_noExcludedIds, s_noExcludedIds, throwOnError: true).ConfigureAwait(false);
                }
                if (GetPendingLegacyDeploymentCleanup(stateSection) is JsonObject pendingLegacyDeploymentCleanup)
                {
                    await DeleteExistingDeploymentAsync(
                        context,
                        client,
                        scope,
                        new DeploymentStateSection(sectionName, pendingLegacyDeploymentCleanup, version: 0),
                        throwOnError: true).ConfigureAwait(false);
                }
                foreach (var pendingOwnerCleanupId in GetPendingOwnerCleanupIds(
                    stateSection,
                    stateSection.Data["OwnerId"]?.GetValue<string>()))
                {
                    await DeleteRemoteDeploymentsByResourceLabelAsync(
                        context,
                        client,
                        scope,
                        pendingOwnerCleanupId,
                        resourceName,
                        s_noExcludedIds,
                        s_noExcludedIds,
                        s_noExcludedIds,
                        throwOnError: true).ConfigureAwait(false);
                }
                await deploymentStateManager.DeleteSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);
                await cleanupTask.CompleteAsync($"Deleted stale sandbox deployment {sectionName}", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static IReadOnlyList<SandboxEndpoint> ResolveSandboxEndpoints(AzureSandboxContainerResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var options = GetAzureSandboxContainerOptions(resource.TargetResource);
        var endpointOptions = options?.Endpoints?.ToDictionary(
            static endpoint => endpoint.Name!,
            StringComparer.OrdinalIgnoreCase);
        var unmatchedEndpointOptions = endpointOptions is null ? null : new HashSet<string>(endpointOptions.Keys, StringComparer.OrdinalIgnoreCase);
        var endpoints = new Dictionary<int, SandboxEndpoint>();
        foreach (var resolvedEndpoint in resource.TargetResource.ResolveEndpoints())
        {
            if (!resolvedEndpoint.Endpoint.IsExternal)
            {
                continue;
            }

            if (resolvedEndpoint.TargetPort.Value is not int targetPort)
            {
                throw new InvalidOperationException($"Endpoint '{resolvedEndpoint.Endpoint.Name}' on resource '{resource.TargetResource.Name}' does not have a target port. Configure a target port before deploying it to an Azure sandbox.");
            }

            var protocol = ResolveSandboxPortProtocol(resource.TargetResource, resolvedEndpoint.Endpoint);
            AzureSandboxEndpointOptions? resolvedEndpointOptions = null;
            endpointOptions?.TryGetValue(resolvedEndpoint.Endpoint.Name, out resolvedEndpointOptions);
            unmatchedEndpointOptions?.Remove(resolvedEndpoint.Endpoint.Name);
            var endpoint = new SandboxEndpoint(
                resolvedEndpoint.Endpoint.Name,
                targetPort,
                resolvedEndpoint.Endpoint.IsExternal,
                IsHttp: true,
                protocol,
                resolvedEndpointOptions?.Anonymous ?? false);

            if (endpoints.TryGetValue(targetPort, out var existingEndpoint))
            {
                if (!string.Equals(existingEndpoint.Protocol, endpoint.Protocol, StringComparison.Ordinal))
                {
                    throw new NotSupportedException($"Endpoint '{resolvedEndpoint.Endpoint.Name}' on resource '{resource.TargetResource.Name}' shares target port {targetPort} with endpoint '{existingEndpoint.Name}' but uses a different transport. Azure sandbox ports support a single HTTP protocol per target port.");
                }

                if (existingEndpoint.Anonymous != endpoint.Anonymous)
                {
                    throw new NotSupportedException($"Endpoint '{resolvedEndpoint.Endpoint.Name}' on resource '{resource.TargetResource.Name}' shares target port {targetPort} with endpoint '{existingEndpoint.Name}' but configures a different anonymous-access policy. Azure sandbox ports support a single access policy per target port.");
                }

                endpoints[targetPort] = existingEndpoint with
                {
                    IsExternal = existingEndpoint.IsExternal || endpoint.IsExternal,
                    IsHttp = existingEndpoint.IsHttp || endpoint.IsHttp
                };
            }
            else
            {
                endpoints.Add(targetPort, endpoint);
            }
        }

        if (unmatchedEndpointOptions is { Count: > 0 })
        {
            throw new InvalidOperationException($"Resource '{resource.TargetResource.Name}' has Azure sandbox endpoint options for endpoint(s) that are not exposed by EndpointAnnotation: {string.Join(", ", unmatchedEndpointOptions)}.");
        }

        return [.. endpoints.Values.OrderBy(static endpoint => endpoint.TargetPort)];
    }

    private static string ResolveSandboxPortProtocol(IResource resource, EndpointAnnotation endpoint)
    {
        return endpoint.Transport switch
        {
            "http" => "Http",
            "http2" => "Http2",
            _ => throw new NotSupportedException($"Endpoint '{endpoint.Name}' on resource '{resource.Name}' uses transport '{endpoint.Transport}'. Azure sandbox ports currently support only HTTP and HTTP/2 endpoints.")
        };
    }

    internal static async Task DeleteExistingDeploymentAsync(
        PipelineStepContext context,
        IAzureDevComputeClient client,
        AzureDevComputeResourceScope scope,
        DeploymentStateSection stateSection,
        bool throwOnError)
    {
        List<Exception>? failures = throwOnError ? [] : null;
        var sandboxId = stateSection.Data["SandboxId"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(sandboxId))
        {
            try
            {
                await DeleteSandboxAsync(context, client, scope, sandboxId, GetStatePorts(stateSection), throwOnError).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
            {
                failures!.Add(ex);
            }
        }

        var diskImageId = stateSection.Data["DiskImageId"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(diskImageId))
        {
            try
            {
                await DeleteDiskImageAsync(context, client, scope, diskImageId, throwOnError).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !context.CancellationToken.IsCancellationRequested)
            {
                failures!.Add(ex);
            }
        }

        if (failures is [var failure])
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        else if (failures is [_, _, ..])
        {
            throw new AggregateException("Multiple failures occurred while deleting the previous Azure sandbox deployment.", failures);
        }
    }

    private static DeploymentStateSection CloneStateSection(DeploymentStateSection stateSection)
    {
        return new DeploymentStateSection(
            stateSection.SectionName,
            stateSection.Data.DeepClone().AsObject(),
            version: 0);
    }

    internal static async Task DeleteRemoteDeploymentsByResourceLabelAsync(
        PipelineStepContext context,
        IAzureDevComputeClient client,
        AzureDevComputeResourceScope scope,
        string ownerId,
        string resourceName,
        IReadOnlySet<string> excludedDeployIds,
        IReadOnlySet<string> excludedSandboxIds,
        IReadOnlySet<string> excludedDiskImageIds,
        bool throwOnError)
    {
        var labelSelector = CreateLabelSelector(ownerId, resourceName);

        List<AzureDevComputeSandbox> sandboxes;
        try
        {
            sandboxes = await client.ListSandboxesAsync(scope, labelSelector, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!throwOnError && ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "Failed to list existing sandbox deployments labeled for resource '{ResourceName}'.", resourceName);
            sandboxes = [];
        }

        foreach (var sandbox in sandboxes.Where(sandbox => ShouldDeleteLabeledDeployment(sandbox.Id, sandbox.Labels, ownerId, resourceName, excludedDeployIds, excludedSandboxIds)))
        {
            await DeleteSandboxAsync(
                context,
                client,
                scope,
                sandbox.Id,
                sandbox.Ports.Select(static port => port.Port),
                throwOnError).ConfigureAwait(false);
        }

        List<AzureDevComputeDiskImage> diskImages;
        try
        {
            diskImages = await client.ListDiskImagesAsync(scope, labelSelector, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!throwOnError && ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "Failed to list existing sandbox disk images labeled for resource '{ResourceName}'.", resourceName);
            diskImages = [];
        }

        foreach (var diskImage in diskImages.Where(diskImage => ShouldDeleteLabeledDeployment(diskImage.Id, diskImage.Labels, ownerId, resourceName, excludedDeployIds, excludedDiskImageIds)))
        {
            await DeleteDiskImageAsync(context, client, scope, diskImage.Id, throwOnError).ConfigureAwait(false);
        }
    }

    internal static async Task CleanupFailedDeploymentAsync(
        PipelineStepContext context,
        IAzureDevComputeClient client,
        AzureDevComputeResourceScope scope,
        string ownerId,
        string resourceName,
        string deployId,
        CancellationToken cancellationToken,
        bool pollUntilCancellation = false,
        TimeSpan? pollInterval = null)
    {
        var labelSelector = CreateLabelSelector(ownerId, resourceName, deployId);
        var consecutiveEmptyResults = 0;
        var delay = pollInterval ?? TimeSpan.FromSeconds(1);

        while (!cancellationToken.IsCancellationRequested &&
            (pollUntilCancellation || consecutiveEmptyResults < 3))
        {
            var foundDeployment = false;

            try
            {
                var sandboxes = await client.ListSandboxesAsync(scope, labelSelector, cancellationToken).ConfigureAwait(false);
                foreach (var sandbox in sandboxes.Where(sandbox => HasDeploymentLabels(sandbox.Labels, ownerId, resourceName, deployId)))
                {
                    foundDeployment = true;
                    await DeleteSandboxAsync(
                        context,
                        client,
                        scope,
                        sandbox.Id,
                        sandbox.Ports.Select(static port => port.Port),
                        throwOnError: false,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Logger.LogWarning(ex, "Failed to list sandboxes while reconciling failed deployment '{DeployId}'.", deployId);
                foundDeployment = true;
            }

            try
            {
                var diskImages = await client.ListDiskImagesAsync(scope, labelSelector, cancellationToken).ConfigureAwait(false);
                foreach (var diskImage in diskImages.Where(diskImage => HasDeploymentLabels(diskImage.Labels, ownerId, resourceName, deployId)))
                {
                    foundDeployment = true;
                    await DeleteDiskImageAsync(
                        context,
                        client,
                        scope,
                        diskImage.Id,
                        throwOnError: false,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Logger.LogWarning(ex, "Failed to list disk images while reconciling failed deployment '{DeployId}'.", deployId);
                foundDeployment = true;
            }

            consecutiveEmptyResults = foundDeployment ? 0 : consecutiveEmptyResults + 1;
            if (!cancellationToken.IsCancellationRequested &&
                (pollUntilCancellation || consecutiveEmptyResults < 3))
            {
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    internal static async Task<T> CreateWithResponseLossCleanupAsync<T>(
        Func<Task<T>> createResource,
        PipelineStepContext context,
        IAzureDevComputeClient client,
        AzureDevComputeResourceScope scope,
        string ownerId,
        string resourceName,
        string deployId,
        TimeSpan? responseLossReconciliationTimeout = null,
        TimeSpan? pollInterval = null)
    {
        try
        {
            return await createResource().ConfigureAwait(false);
        }
        catch (Exception createException)
        {
            var responseMayHaveBeenLost = createException switch
            {
                AzureDevComputeCreateException adcCreateException => adcCreateException.ResponseMayHaveBeenLost,
                _ => false
            };
            var exceptionToThrow = createException is AzureDevComputeCreateException adcException
                ? adcException.OriginalException
                : createException;
            var cleanupTimeout = responseMayHaveBeenLost && responseLossReconciliationTimeout is { } configuredTimeout
                ? configuredTimeout
                : TimeSpan.FromMinutes(2);
            using var cleanupCts = new CancellationTokenSource(cleanupTimeout);
            try
            {
                await CleanupFailedDeploymentAsync(
                    context,
                    client,
                    scope,
                    ownerId,
                    resourceName,
                    deployId,
                    cleanupCts.Token,
                    pollUntilCancellation: responseMayHaveBeenLost,
                    pollInterval).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                context.Logger.LogWarning(
                    cleanupException,
                    "Failed to reconcile Azure sandbox resources after create operation for deployment '{DeployId}' failed.",
                    deployId);
            }

            ExceptionDispatchInfo.Capture(exceptionToThrow).Throw();
            throw new UnreachableException();
        }
    }

    internal static bool ShouldDeleteLabeledDeployment(
        string id,
        IReadOnlyDictionary<string, string> labels,
        string ownerId,
        string resourceName,
        IReadOnlySet<string> excludedDeployIds,
        IReadOnlySet<string> excludedResourceIds)
    {
        if (!HasLabel(labels, "aspire-owner", ownerId) ||
            !HasLabel(labels, "aspire-resource", resourceName))
        {
            return false;
        }

        if (excludedResourceIds.Contains(id))
        {
            return false;
        }

        return !labels.TryGetValue("aspire-deploy", out var deployId) ||
            !excludedDeployIds.Contains(deployId);
    }

    internal static string CreateLabelSelector(string ownerId, string resourceName) =>
        $"aspire-owner={ownerId},aspire-resource={resourceName}";

    internal static string CreateLabelSelector(string ownerId, string resourceName, string deployId) =>
        $"{CreateLabelSelector(ownerId, resourceName)},aspire-deploy={deployId}";

    private static bool HasDeploymentLabels(
        IReadOnlyDictionary<string, string> labels,
        string ownerId,
        string resourceName,
        string deployId)
    {
        return HasLabel(labels, "aspire-owner", ownerId) &&
            HasLabel(labels, "aspire-resource", resourceName) &&
            HasLabel(labels, "aspire-deploy", deployId);
    }

    private static bool HasLabel(IReadOnlyDictionary<string, string> labels, string name, string value)
    {
        return labels.TryGetValue(name, out var actualValue) &&
            string.Equals(actualValue, value, StringComparison.Ordinal);
    }

    private static string GetStateResourceName(DeploymentStateSection stateSection, string sectionName)
    {
        if (stateSection.Data["ResourceName"]?.GetValue<string>() is { Length: > 0 } resourceName)
        {
            return resourceName;
        }

        return sectionName.StartsWith(SandboxStateSectionPrefix, StringComparison.Ordinal)
            ? sectionName[SandboxStateSectionPrefix.Length..]
            : sectionName;
    }

    internal static string CreateStableOwnerId(
        string appHostIdentity,
        string environmentName,
        AzureDevComputeResourceScope scope,
        string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var identity = string.Join(
            "\n",
            [
                appHostIdentity.ToLowerInvariant(),
                environmentName.ToLowerInvariant(),
                scope.SubscriptionId.ToLowerInvariant(),
                scope.ResourceGroupName.ToLowerInvariant(),
                scope.SandboxGroupName.ToLowerInvariant(),
                resourceName.ToLowerInvariant()
            ]);
        var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(identity));
        return $"aspire-{hash:x16}";
    }

    internal static string CreateLegacyStableOwnerId(
        string appHostIdentity,
        AzureDevComputeResourceScope scope,
        string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var identity = string.Join(
            "\n",
            [
                appHostIdentity.ToLowerInvariant(),
                scope.SubscriptionId.ToLowerInvariant(),
                scope.ResourceGroupName.ToLowerInvariant(),
                scope.SandboxGroupName.ToLowerInvariant(),
                resourceName.ToLowerInvariant()
            ]);
        var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(identity));
        return $"aspire-{hash:x16}";
    }

    internal static HashSet<string> GetPendingOwnerCleanupIds(
        DeploymentStateSection stateSection,
        string? currentOwnerId,
        string? ambiguousOwnerId = null)
    {
        var ownerIds = stateSection.Data["PendingOwnerCleanupIds"] is JsonArray pendingOwnerIds
            ? pendingOwnerIds.GetValues<string>().ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        if (stateSection.Data["OwnerId"]?.GetValue<string>() is { Length: > 0 } previousOwnerId &&
            !string.Equals(previousOwnerId, currentOwnerId, StringComparison.Ordinal))
        {
            ownerIds.Add(previousOwnerId);
        }

        ownerIds.RemoveWhere(ownerId => string.Equals(ownerId, currentOwnerId, StringComparison.Ordinal));
        ownerIds.RemoveWhere(ownerId => string.Equals(ownerId, ambiguousOwnerId, StringComparison.Ordinal));
        return ownerIds;
    }

    private static void SetPendingOwnerCleanupIds(
        DeploymentStateSection stateSection,
        IReadOnlySet<string> ownerIds)
    {
        if (ownerIds.Count == 0)
        {
            stateSection.Data.Remove("PendingOwnerCleanupIds");
            return;
        }

        stateSection.Data["PendingOwnerCleanupIds"] = new JsonArray(
            ownerIds
                .Order(StringComparer.Ordinal)
                .Select(static ownerId => JsonValue.Create(ownerId))
                .ToArray());
    }

    internal static JsonObject? CreatePendingLegacyDeploymentCleanup(
        DeploymentStateSection stateSection,
        string currentOwnerId,
        string legacyOwnerId)
    {
        var pendingDeploymentCleanup = GetPendingLegacyDeploymentCleanup(stateSection);
        if (stateSection.Data["OwnerId"]?.GetValue<string>() is { Length: > 0 } previousOwnerId &&
            !string.Equals(previousOwnerId, currentOwnerId, StringComparison.Ordinal) &&
            string.Equals(previousOwnerId, legacyOwnerId, StringComparison.Ordinal) &&
            HasRemoteDeploymentState(stateSection))
        {
            pendingDeploymentCleanup = stateSection.Data.DeepClone().AsObject();
            pendingDeploymentCleanup.Remove("PendingLegacyDeploymentCleanup");
        }

        return pendingDeploymentCleanup;
    }

    private static JsonObject? GetPendingLegacyDeploymentCleanup(DeploymentStateSection stateSection) =>
        stateSection.Data["PendingLegacyDeploymentCleanup"]?.DeepClone() as JsonObject;

    private static void SetPendingLegacyDeploymentCleanup(
        DeploymentStateSection stateSection,
        JsonObject? pendingDeploymentCleanup)
    {
        if (pendingDeploymentCleanup is null)
        {
            stateSection.Data.Remove("PendingLegacyDeploymentCleanup");
        }
        else
        {
            stateSection.Data["PendingLegacyDeploymentCleanup"] = pendingDeploymentCleanup.DeepClone();
        }
    }

    internal static string GetStableAppHostIdentity(IConfiguration configuration)
    {
        return configuration["AppHost:DeploymentStatePathSha256"] is { Length: > 0 } deploymentStatePathHash
            ? deploymentStatePathHash
            : throw new InvalidOperationException("AppHost:DeploymentStatePathSha256 is required to isolate Azure sandbox ownership between AppHosts.");
    }

    internal static string CreateDeploymentSecurityFingerprint(
        string immutableImageReference,
        IReadOnlyList<SandboxEndpoint> endpoints,
        IReadOnlyList<AzureDevComputeIdentitySetting>? identitySettings,
        AzureDevComputeSandboxEgressPolicy egressPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(immutableImageReference);
        ArgumentNullException.ThrowIfNull(egressPolicy);

        return new JsonObject
        {
            ["ImageReference"] = immutableImageReference,
            ["Endpoints"] = new JsonArray(
                endpoints
                    .OrderBy(static endpoint => endpoint.Name, StringComparer.Ordinal)
                    .Select(static endpoint => (JsonNode)new JsonObject
                    {
                        ["Name"] = endpoint.Name,
                        ["TargetPort"] = endpoint.TargetPort,
                        ["Protocol"] = endpoint.Protocol,
                        ["IsExternal"] = endpoint.IsExternal,
                        ["Anonymous"] = endpoint.Anonymous
                    })
                    .ToArray()),
            ["IdentitySettings"] = new JsonArray(
                (identitySettings ?? [])
                    .OrderBy(static identity => identity.Identity, StringComparer.Ordinal)
                    .ThenBy(static identity => identity.Lifecycle, StringComparer.Ordinal)
                    .Select(static identity => (JsonNode)new JsonObject
                    {
                        ["Identity"] = identity.Identity,
                        ["Lifecycle"] = identity.Lifecycle
                    })
                    .ToArray()),
            ["EgressPolicy"] = new JsonObject
            {
                ["DefaultAction"] = egressPolicy.DefaultAction,
                ["TrafficInspection"] = egressPolicy.TrafficInspection,
                ["HostRules"] = new JsonArray(
                    egressPolicy.HostRules
                        .OrderBy(static rule => rule.Pattern, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(static rule => rule.Action, StringComparer.Ordinal)
                        .Select(static rule => (JsonNode)new JsonObject
                        {
                            ["Action"] = rule.Action,
                            ["Pattern"] = rule.Pattern
                        })
                        .ToArray())
            }
        }.ToJsonString();
    }

    internal static bool HasSecurityRelevantEndpointChange(
        DeploymentStateSection previousStateSection,
        string currentFingerprint,
        bool hasRuntimeEnvironmentConfiguration,
        bool hasRuntimeCommandConfiguration = false)
    {
        if (!HasRemoteDeploymentState(previousStateSection))
        {
            return false;
        }

        // Resolved environment and command values can contain secrets, so never persist comparable
        // value-derived fingerprints. Conservatively remove the previous generation whenever either
        // runtime configuration is present, and once more when one is removed after a prior deployment.
        if (hasRuntimeEnvironmentConfiguration ||
            previousStateSection.Data["HasRuntimeEnvironmentConfiguration"]?.GetValue<bool>() == true ||
            hasRuntimeCommandConfiguration ||
            previousStateSection.Data["HasRuntimeCommandConfiguration"]?.GetValue<bool>() == true)
        {
            return true;
        }

        if (previousStateSection.Data["PendingSecurityCleanup"]?.GetValue<bool>() == true)
        {
            return true;
        }

        var previousFingerprint = previousStateSection.Data["EndpointSecurityFingerprint"]?.GetValue<string>();
        if (string.Equals(previousFingerprint, currentFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static void SetRecoveryStateIfMissing(
        DeploymentStateSection stateSection,
        AzureDevComputeResourceScope scope,
        AzureSandboxContainerResource resource)
    {
        stateSection.Data["SubscriptionId"] ??= scope.SubscriptionId;
        stateSection.Data["ResourceGroup"] ??= scope.ResourceGroupName;
        stateSection.Data["Location"] ??= scope.Region;
        stateSection.Data["SandboxGroup"] ??= scope.SandboxGroupName;
        stateSection.Data["ResourceName"] ??= resource.Name;
        stateSection.Data["SourceResourceName"] ??= resource.TargetResource.Name;
    }

    internal static void ValidateDeploymentScope(DeploymentStateSection stateSection, AzureDevComputeResourceScope scope)
    {
        if (!HasRemoteDeploymentState(stateSection))
        {
            return;
        }

        var subscriptionId = stateSection.Data["SubscriptionId"]?.GetValue<string>();
        var resourceGroup = stateSection.Data["ResourceGroup"]?.GetValue<string>();
        var location = stateSection.Data["Location"]?.GetValue<string>();
        var sandboxGroup = stateSection.Data["SandboxGroup"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(subscriptionId) ||
            string.IsNullOrWhiteSpace(resourceGroup) ||
            string.IsNullOrWhiteSpace(location) ||
            string.IsNullOrWhiteSpace(sandboxGroup))
        {
            return;
        }

        if (!string.Equals(subscriptionId, scope.SubscriptionId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(resourceGroup, scope.ResourceGroupName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(location, scope.Region, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sandboxGroup, scope.SandboxGroupName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Azure sandbox group deployment scope changed while sandbox deployment state still exists. " +
                "Run 'aspire destroy' with the previous sandbox group configuration before deploying to the new scope.");
        }
    }

    internal static bool HasRemoteDeploymentState(DeploymentStateSection stateSection) =>
        !string.IsNullOrWhiteSpace(stateSection.Data["OwnerId"]?.GetValue<string>()) ||
        !string.IsNullOrWhiteSpace(stateSection.Data["SandboxId"]?.GetValue<string>()) ||
        !string.IsNullOrWhiteSpace(stateSection.Data["DiskImageId"]?.GetValue<string>());

    internal static string CreateSandboxUrlSummary(string currentUrl, string? retainedUrl)
    {
        if (string.IsNullOrWhiteSpace(retainedUrl) ||
            string.Equals(currentUrl, retainedUrl, StringComparison.Ordinal))
        {
            return $"[{currentUrl}]({currentUrl})";
        }

        return $"Current: [{currentUrl}]({currentUrl}); retained for references configured before sandbox deployment: [{retainedUrl}]({retainedUrl})";
    }

    private static string? GetFirstStateUrl(DeploymentStateSection stateSection)
    {
        if (stateSection.Data["Ports"] is not JsonArray ports)
        {
            return null;
        }

        foreach (var port in ports.OfType<JsonObject>())
        {
            if (port["Url"]?.GetValue<string>() is { Length: > 0 } url)
            {
                return url;
            }
        }

        return null;
    }

    private static IReadOnlySet<string> GetExcludedDeployIds(string deployId, DeploymentStateSection previousStateSection)
    {
        var deployIds = new HashSet<string>(StringComparer.Ordinal)
        {
            deployId
        };

        if (previousStateSection.Data["DeployId"]?.GetValue<string>() is { Length: > 0 } previousDeployId)
        {
            deployIds.Add(previousDeployId);
        }

        return deployIds;
    }

    private static IReadOnlySet<string> GetExcludedResourceIds(string id, DeploymentStateSection previousStateSection, string stateKey)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal)
        {
            id
        };

        if (previousStateSection.Data[stateKey]?.GetValue<string>() is { Length: > 0 } previousId)
        {
            ids.Add(previousId);
        }

        return ids;
    }

    private static IEnumerable<int> GetStatePorts(DeploymentStateSection stateSection)
    {
        if (stateSection.Data["Ports"] is JsonArray ports)
        {
            foreach (var port in ports.OfType<JsonObject>())
            {
                if (port["Port"]?.GetValue<int>() is { } portNumber)
                {
                    yield return portNumber;
                }
            }

            yield break;
        }

        if (stateSection.Data["Port"]?.GetValue<int>() is { } legacyPort)
        {
            yield return legacyPort;
        }
    }

    internal static TimeSpan GetPublicEndpointReadyTimeout(AzureSandboxContainerResource resource)
    {
        return GetAzureSandboxContainerOptions(resource.TargetResource)?.PublicEndpointReadyTimeout ??
            TimeSpan.FromSeconds(PublicEndpointTimeoutSeconds);
    }

    private static async Task WaitForPublicHttpAsync(string publicUrl, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient(CreatePublicEndpointHttpHandler()) { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        Exception? lastException = null;
        HttpStatusCode? lastStatusCode = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await httpClient.GetAsync(publicUrl.TrimEnd('/'), cancellationToken).ConfigureAwait(false);
                lastStatusCode = response.StatusCode;
                if ((int)response.StatusCode < 500)
                {
                    return;
                }
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Sandbox public URL '{publicUrl}' was not ready after {timeout.TotalSeconds} seconds (last HTTP status: '{lastStatusCode}').", lastException);
    }

    internal static HttpClientHandler CreatePublicEndpointHttpHandler() =>
        new() { AllowAutoRedirect = false };

    internal static async Task DeleteSandboxAsync(
        PipelineStepContext context,
        IAzureDevComputeClient client,
        AzureDevComputeResourceScope scope,
        string sandboxId,
        IEnumerable<int> ports,
        bool throwOnError,
        CancellationToken? cancellationToken = null)
    {
        var effectiveCancellationToken = cancellationToken ?? context.CancellationToken;
        List<Exception>? failures = throwOnError ? [] : null;

        foreach (var port in ports.Distinct())
        {
            try
            {
                await client.RemovePortAsync(
                    scope,
                    sandboxId,
                    new AzureDevComputeRemovePortRequest { Port = port },
                    effectiveCancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !effectiveCancellationToken.IsCancellationRequested)
            {
                if (throwOnError)
                {
                    failures!.Add(ex);
                }
                else
                {
                    context.Logger.LogWarning(ex, "Failed to remove sandbox port {Port} from sandbox '{SandboxId}'.", port, sandboxId);
                }
            }
        }

        try
        {
            await client.DeleteSandboxAsync(scope, sandboxId, effectiveCancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !effectiveCancellationToken.IsCancellationRequested)
        {
            if (!throwOnError)
            {
                context.Logger.LogWarning(ex, "Failed to delete sandbox '{SandboxId}'.", sandboxId);
            }
            else
            {
                failures!.Add(ex);
            }
        }

        if (failures is [var failure])
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        else if (failures is [_, _, ..])
        {
            throw new AggregateException($"Multiple failures occurred while deleting Azure sandbox '{sandboxId}'.", failures);
        }
    }

    private static async Task DeleteDiskImageAsync(
        PipelineStepContext context,
        IAzureDevComputeClient client,
        AzureDevComputeResourceScope scope,
        string diskImageId,
        bool throwOnError,
        CancellationToken? cancellationToken = null)
    {
        var effectiveCancellationToken = cancellationToken ?? context.CancellationToken;
        try
        {
            await client.DeleteDiskImageAsync(scope, diskImageId, effectiveCancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!throwOnError &&
            (ex is not OperationCanceledException || !effectiveCancellationToken.IsCancellationRequested))
        {
            context.Logger.LogWarning(ex, "Failed to delete sandbox disk image '{DiskImageId}'.", diskImageId);
        }
    }

    private static string GetRequiredStateValue(DeploymentStateSection section, string name)
    {
        var value = section.Data[name]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Deployment state section '{section.SectionName}' is missing required value '{name}'.");
        }

        return value;
    }

    private static string GetRequiredOutput(AzureBicepResource resource, string name)
    {
        if (!resource.Outputs.TryGetValue(name, out var value) || value is null || string.IsNullOrWhiteSpace(value.ToString()))
        {
            throw new InvalidOperationException($"Azure resource '{resource.Name}' is missing required output '{name}'. Ensure Azure infrastructure provisioning completed successfully.");
        }

        return value.ToString()!;
    }

    internal static AzureDevComputeResourceScope CreateDataPlaneScope(AzureSandboxGroupResource sandboxGroup)
    {
        ArgumentNullException.ThrowIfNull(sandboxGroup);

        var resourceId = new global::Azure.Core.ResourceIdentifier(GetRequiredOutput(sandboxGroup, "id"));
        if (string.IsNullOrWhiteSpace(resourceId.SubscriptionId) ||
            string.IsNullOrWhiteSpace(resourceId.ResourceGroupName) ||
            string.IsNullOrWhiteSpace(resourceId.Name))
        {
            throw new InvalidOperationException(
                $"Azure sandbox group '{sandboxGroup.Name}' returned an invalid resource ID '{resourceId}'.");
        }

        return new AzureDevComputeResourceScope(
            resourceId.SubscriptionId,
            resourceId.ResourceGroupName,
            resourceId.Name,
            GetRequiredOutput(sandboxGroup, "location"));
    }

    private static string CreateSandboxResourceName(string resourceName, string deployId)
    {
        var normalized = new string(resourceName.ToLowerInvariant().Select(static c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "app";
        }

        if (normalized.Length > 32)
        {
            normalized = normalized[..32].Trim('-');
        }

        return $"{normalized}-{deployId[..8]}";
    }

    private static AzureSandboxOptions? GetAzureSandboxContainerOptions(IResource resource)
    {
        return resource.Annotations.OfType<AzureSandboxContainerOptionsAnnotation>().SingleOrDefault()?.Options;
    }

    internal static string GetStateSectionName(AzureSandboxContainerResource resource) => $"{SandboxStateSectionPrefix}{resource.Name}";

    private static string GetStaleCleanupStepName() => "destroy-stale-azure-sandboxes";

    internal static string GetDeployStepName(AzureSandboxContainerResource resource) => $"deploy-{resource.Name}";

    private static string GetDestroyStepName(AzureSandboxContainerResource resource) => $"destroy-{resource.Name}";

    internal readonly record struct SandboxEndpoint(
        string Name,
        int TargetPort,
        bool IsExternal,
        bool IsHttp,
        string Protocol,
        bool? Anonymous);

    internal sealed record ContainerImageMetadata(
        IReadOnlyList<string> Entrypoint,
        IReadOnlyList<string> Command,
        IReadOnlyDictionary<string, string> EnvironmentVariables,
        string? WorkingDirectory,
        IReadOnlySet<string> EgressHosts);

    internal sealed record ResolvedModeledCommand(
        IReadOnlyList<string>? Entrypoint,
        IReadOnlyList<string>? Command,
        IReadOnlySet<string> EgressHosts)
    {
        public void Deconstruct(out IReadOnlyList<string>? entrypoint, out IReadOnlyList<string>? command)
        {
            entrypoint = Entrypoint;
            command = Command;
        }
    }

    internal sealed record ResolvedEnvironmentVariables(
        IReadOnlyDictionary<string, string> Values,
        IReadOnlySet<string> EgressHosts);

    internal sealed record ResolvedValue(string Value, IReadOnlySet<string> EgressHosts);

    private sealed record AzureDeploymentState(string SubscriptionId, string ResourceGroup, string Location);

}
