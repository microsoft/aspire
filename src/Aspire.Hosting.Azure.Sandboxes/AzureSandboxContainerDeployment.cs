// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES004
#pragma warning disable ASPIREAZURE001
#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Hashing;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
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
            if (dependency.GetDeploymentTargetAnnotation(resource.Parent)?.DeploymentTarget is not AzureSandboxContainerResource producer)
            {
                continue;
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
        var azureState = await GetAzureStateAsync(deploymentStateManager, context.CancellationToken).ConfigureAwait(false);

        var dataPlaneScope = CreateDataPlaneScope(resource.Parent);
        var client = CreateAzureDevComputeClient(context);

        var stateSection = await deploymentStateManager.AcquireSectionAsync(GetStateSectionName(resource), context.CancellationToken).ConfigureAwait(false);
        ValidateDeploymentScope(stateSection, dataPlaneScope);
        var previousStateSection = CloneStateSection(stateSection);
        var ownerId = CreateStableOwnerId(
            context.Services.GetRequiredService<IHostEnvironment>().ApplicationName,
            dataPlaneScope,
            resource.Name);
        stateSection.Data["OwnerId"] = ownerId;
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
            var imageMetadata = await ResolveContainerImageMetadataAsync(context, targetResource, imageReference).ConfigureAwait(false);
            var diskImageReference = await ResolveContainerImageReferenceForDiskImageAsync(context, imageReference).ConfigureAwait(false);
            var diskImageName = CreateSandboxResourceName(targetResource.Name, deployId);

            var diskTask = await context.ReportingStep.CreateTaskAsync($"Creating sandbox disk image for {targetResource.Name}", context.CancellationToken).ConfigureAwait(false);
            await using (diskTask.ConfigureAwait(false))
            {
                var diskImage = await CreateDiskImageAsync(context, client, dataPlaneScope, resource, diskImageReference, diskImageName, ownerId, deployId).ConfigureAwait(false);
                diskImageId = diskImage.Id;
                diskImage = await WaitForDiskImageReadyAsync(context, client, dataPlaneScope, diskImage).ConfigureAwait(false);
                await diskTask.CompleteAsync($"Created sandbox disk image {diskImageId}", CompletionState.Completed, context.CancellationToken).ConfigureAwait(false);
            }

            var environmentVariables = new Dictionary<string, string>(imageMetadata.EnvironmentVariables, StringComparer.Ordinal);
            foreach (var (key, value) in await ResolveEnvironmentVariablesAsync(context, targetResource).ConfigureAwait(false))
            {
                environmentVariables[key] = value;
            }
            AddManagedIdentityEnvironmentVariables(targetResource, environmentVariables);

            var createTask = await context.ReportingStep.CreateTaskAsync($"Creating sandbox for {targetResource.Name}", context.CancellationToken).ConfigureAwait(false);
            await using (createTask.ConfigureAwait(false))
            {
                var sandbox = await CreateSandboxAsync(context, client, dataPlaneScope, resource, diskImageId, environmentVariables, imageMetadata, ownerId, deployId).ConfigureAwait(false);
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

            var endpointSecurityFingerprint = CreateDeploymentSecurityFingerprint(endpoints, diskImageReference);
            var securityConfigurationChanged = HasSecurityRelevantEndpointChange(
                previousStateSection,
                endpointSecurityFingerprint);
            var pendingSecurityCleanup = previousStateSection.Data["PendingSecurityCleanup"]?.GetValue<bool>() == true;
            securityConfigurationChanged |= pendingSecurityCleanup;
            var previousOwnerId = previousStateSection.Data["OwnerId"]?.GetValue<string>();
            var ownerChanged = !string.IsNullOrWhiteSpace(previousOwnerId) &&
                !string.Equals(previousOwnerId, ownerId, StringComparison.Ordinal);

            stateSection.Data.Clear();
            stateSection.Data["OwnerId"] = ownerId;
            stateSection.Data["SandboxId"] = sandboxId;
            stateSection.Data["DiskImageId"] = diskImageId;
            stateSection.Data["SubscriptionId"] = dataPlaneScope.SubscriptionId;
            stateSection.Data["ResourceGroup"] = dataPlaneScope.ResourceGroupName;
            stateSection.Data["Location"] = azureState.Location;
            stateSection.Data["SandboxGroup"] = dataPlaneScope.SandboxGroupName;
            stateSection.Data["ResourceName"] = resource.Name;
            stateSection.Data["SourceResourceName"] = targetResource.Name;
            stateSection.Data["DeployId"] = deployId;
            stateSection.Data["Ports"] = portStates;
            stateSection.Data["EndpointSecurityFingerprint"] = endpointSecurityFingerprint;
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

                if (ownerChanged)
                {
                    await DeleteRemoteDeploymentsByResourceLabelAsync(
                        context,
                        client,
                        dataPlaneScope,
                        previousOwnerId!,
                        resource.Name,
                        s_noExcludedIds,
                        s_noExcludedIds,
                        s_noExcludedIds,
                        throwOnError: false).ConfigureAwait(false);
                }

                if (securityConfigurationChanged)
                {
                    stateSection.Data["PendingSecurityCleanup"] = false;
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
            if (!deploymentCommitted && !string.IsNullOrWhiteSpace(sandboxId))
            {
                using var sandboxCleanupCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try
                {
                    await DeleteSandboxAsync(
                        context,
                        client,
                        dataPlaneScope,
                        sandboxId,
                        addedPorts.Select(static endpoint => endpoint.TargetPort),
                        throwOnError: true,
                        sandboxCleanupCts.Token).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    context.Logger.LogWarning(cleanupException, "Failed to clean up sandbox '{SandboxId}' after deployment failure.", sandboxId);
                }
            }

            if (!deploymentCommitted && !string.IsNullOrWhiteSpace(diskImageId))
            {
                using var diskImageCleanupCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try
                {
                    await DeleteDiskImageAsync(
                        context,
                        client,
                        dataPlaneScope,
                        diskImageId,
                        throwOnError: true,
                        diskImageCleanupCts.Token).ConfigureAwait(false);
                }
                catch (Exception cleanupException)
                {
                    context.Logger.LogWarning(cleanupException, "Failed to clean up sandbox disk image '{DiskImageId}' after deployment failure.", diskImageId);
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
        AzureDevComputeRegistryCredentials? registryCredentials = null;
        var registry = resource.Parent.ContainerRegistry;
        if (registry is not null)
        {
            var registryEndpoint = await registry.RegistryEndpoint.GetValueAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(registryEndpoint) &&
                imageReference.StartsWith($"{registryEndpoint}/", StringComparison.OrdinalIgnoreCase))
            {
                var token = await GetAcrRefreshTokenAsync(context, registryEndpoint).ConfigureAwait(false);
                registryCredentials = new AzureDevComputeRegistryCredentials
                {
                    Username = token.Username,
                    Token = token.Token
                };
            }
        }

        return await client.CreateDiskImageAsync(
            scope,
            new AzureDevComputeCreateDiskImageRequest
            {
                Name = diskImageName,
                Labels = CreateLabels(resource, ownerId, deployId),
                Image = new AzureDevComputeDiskImageSpec
                {
                    Base = imageReference
                },
                RegistryCredentials = registryCredentials
            },
            context.CancellationToken).ConfigureAwait(false);
    }

    private static async Task<AcrRefreshToken> GetAcrRefreshTokenAsync(PipelineStepContext context, string registryEndpoint)
    {
        var azureEnvironment = context.Model.Resources.OfType<AzureEnvironmentResource>().FirstOrDefault() ??
            throw new InvalidOperationException("AzureEnvironmentResource must be present in the application model.");
        var provisioningContext = await azureEnvironment.ProvisioningContextTask.Task.ConfigureAwait(false);
        var tenantId = provisioningContext.Tenant.TenantId?.ToString()
            ?? throw new InvalidOperationException("Tenant ID is required for ACR authentication but was not available in provisioning context.");

        var acrLoginService = context.Services.GetRequiredService<IAcrLoginService>();
        var tokenCredentialProvider = context.Services.GetRequiredService<ITokenCredentialProvider>();

        return await acrLoginService.GetRefreshTokenAsync(
            registryEndpoint,
            tenantId,
            tokenCredentialProvider.TokenCredential,
            context.CancellationToken).ConfigureAwait(false);
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
        string ownerId,
        string deployId)
    {
        ValidateSandboxVolumes(resource.TargetResource);

        return client.CreateSandboxAsync(
            scope,
            new AzureDevComputeSandboxRequest
            {
                Labels = CreateLabels(resource, ownerId, deployId),
                Environment = environmentVariables.Count > 0 ? environmentVariables : null,
                IdentitySettings = ResolveIdentitySettings(resource.TargetResource),
                SkipEgressProxy = false,
                EgressPolicy = CreateEgressPolicy(),
                Entrypoint = imageMetadata.Entrypoint.Count > 0 ? [.. imageMetadata.Entrypoint] : null,
                Cmd = imageMetadata.Command.Count > 0 ? [.. imageMetadata.Command] : null,
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

    internal static AzureDevComputeSandboxEgressPolicy CreateEgressPolicy()
    {
        return new AzureDevComputeSandboxEgressPolicy
        {
            DefaultAction = "Deny",
            TrafficInspection = "Full"
        };
    }

    internal static void ValidateSandboxVolumes(IResource resource)
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
        var authorizedConnectorGateways = endpoint.AuthorizedConnectorGateways;
        var entraId = authorizedConnectorGateways.Count == 0
            ? null
            : new AzureDevComputePortEntraIdAuthConfig
            {
                Enabled = true,
                ObjectIds = authorizedConnectorGateways
                    .Select(gateway => GetRequiredOutput(gateway, "principalId"))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                TenantIds = authorizedConnectorGateways
                    .Select(gateway => GetRequiredOutput(gateway, "tenantId"))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };

        var ports = await client.AddPortAsync(
            scope,
            sandboxId,
            new AzureDevComputeAddPortRequest
            {
                Name = endpoint.Name,
                Port = endpoint.TargetPort,
                ActivationMode = entraId is null ? null : "OnDemand",
                Auth = endpoint.IsExternal
                    ? new AzureDevComputePortAuthConfig
                    {
                        Anonymous = endpoint.Anonymous ?? false,
                        EntraId = entraId
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
        var inspector = await ResolveContainerImageInspectorAsync(context).ConfigureAwait(false);
        return await ResolveContainerImageReferenceForDiskImageAsync(
            inspector,
            imageReference,
            context.CancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string> ResolveContainerImageReferenceForDiskImageAsync(
        IContainerImageInspector inspector,
        string imageReference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        var manifest = await inspector.InspectImageManifestAsync(imageReference, cancellationToken).ConfigureAwait(false);
        return ResolveLinuxAmd64ManifestReference(manifest, imageReference);
    }

    internal static string ResolveLinuxAmd64ManifestReference(string manifestJson, string imageReference)
    {
        JsonNode? manifest;
        try
        {
            manifest = JsonNode.Parse(manifestJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Container runtime returned invalid image manifest for '{imageReference}'.", ex);
        }

        if (manifest is JsonArray verboseManifests)
        {
            foreach (var item in verboseManifests.OfType<JsonObject>())
            {
                if (item["Descriptor"] is not JsonObject descriptor ||
                    descriptor["platform"] is not JsonObject platform ||
                    !string.Equals(platform["os"]?.GetValue<string>(), "linux", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(platform["architecture"]?.GetValue<string>(), "amd64", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (descriptor["digest"]?.GetValue<string>() is { Length: > 0 } digest)
                {
                    return CreateDigestImageReference(imageReference, digest);
                }
            }

            throw new InvalidOperationException($"Container image '{imageReference}' does not contain a linux/amd64 manifest with an immutable digest.");
        }

        if (manifest is not JsonObject manifestObject)
        {
            throw new InvalidOperationException($"Container runtime returned an unsupported image manifest for '{imageReference}'.");
        }

        if (manifestObject["manifests"] is not JsonArray manifests)
        {
            var descriptor = manifestObject["Descriptor"] as JsonObject ??
                manifestObject["descriptor"] as JsonObject;
            var platform = descriptor?["platform"] as JsonObject ??
                manifestObject["platform"] as JsonObject;
            if (platform is not null)
            {
                ValidateLinuxAmd64Platform(platform, imageReference);
            }
            else if (imageReference.Contains('@', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Container image '{imageReference}' is digest-pinned, but its linux/amd64 platform could not be verified.");
            }

            var digest = manifestObject["Descriptor"]?["digest"]?.GetValue<string>() ??
                manifestObject["descriptor"]?["digest"]?.GetValue<string>() ??
                manifestObject["digest"]?.GetValue<string>();
            return !string.IsNullOrWhiteSpace(digest)
                ? CreateDigestImageReference(imageReference, digest)
                : throw new InvalidOperationException($"Container image '{imageReference}' is mutable and its immutable digest could not be resolved.");
        }

        // `docker manifest inspect` returns image indexes as:
        //   { "manifests": [
        //     { "digest": "sha256:...", "platform": { "os": "linux", "architecture": "amd64" } },
        //     { "digest": "sha256:...", "platform": { "os": "unknown", "architecture": "unknown" } }
        //   ] }
        // The unknown/unknown entry is a provenance attestation. ADC disk-image conversion currently
        // accepts the tag but can boot a sandbox without a usable root filesystem, so pass the concrete
        // linux/amd64 manifest digest instead of the index tag.
        foreach (var item in manifests)
        {
            if (item is not JsonObject descriptor ||
                descriptor["platform"] is not JsonObject platform)
            {
                continue;
            }

            var os = platform["os"]?.GetValue<string>();
            var architecture = platform["architecture"]?.GetValue<string>();
            if (!string.Equals(os, "linux", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(architecture, "amd64", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (descriptor["digest"]?.GetValue<string>() is { Length: > 0 } digest)
            {
                return CreateDigestImageReference(imageReference, digest);
            }
        }

        throw new InvalidOperationException($"Container image '{imageReference}' is an image index but does not contain a linux/amd64 manifest.");
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

    private static void ValidateLinuxAmd64Platform(JsonObject platform, string imageReference)
    {
        var os = platform["os"]?.GetValue<string>();
        var architecture = platform["architecture"]?.GetValue<string>();
        if (!string.Equals(os, "linux", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(architecture, "amd64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Container image '{imageReference}' targets '{os ?? "unknown"}/{architecture ?? "unknown"}', but Azure sandbox disk images require linux/amd64.");
        }
    }

    private static async Task<ContainerImageMetadata> ResolveContainerImageMetadataAsync(PipelineStepContext context, IResource resource, string imageReference)
    {
        var (modeledEntrypoint, modeledCommand) = await ResolveModeledCommandAsync(context, resource).ConfigureAwait(false);
        if (resource is not ContainerResource || !resource.RequiresImageBuildAndPush())
        {
            return new ContainerImageMetadata(modeledEntrypoint ?? [], modeledCommand ?? [], new Dictionary<string, string>(StringComparer.Ordinal), WorkingDirectory: null);
        }

        var metadata = await InspectLocalContainerImageAsync(context, imageReference).ConfigureAwait(false);
        return metadata with
        {
            Entrypoint = modeledEntrypoint ?? metadata.Entrypoint,
            Command = modeledCommand ?? metadata.Command
        };
    }

    internal static async Task<(IReadOnlyList<string>? Entrypoint, IReadOnlyList<string>? Command)> ResolveModeledCommandAsync(PipelineStepContext context, IResource resource)
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
        foreach (var arg in args)
        {
            resolvedArgs.Add(await ResolveValueAsync(context, resource, arg).ConfigureAwait(false));
        }

        var entrypoint = resource is ContainerResource container && !string.IsNullOrWhiteSpace(container.Entrypoint)
            ? new[] { container.Entrypoint }
            : null;
        var command = resolvedArgs.Count == 0 ? null : resolvedArgs;

        return (entrypoint, command);
    }

    private static async Task<ContainerImageMetadata> InspectLocalContainerImageAsync(PipelineStepContext context, string imageReference)
    {
        var inspector = await ResolveContainerImageInspectorAsync(context).ConfigureAwait(false);
        var output = await inspector.InspectImageConfigAsync(imageReference, context.CancellationToken).ConfigureAwait(false);

        return ParseContainerImageMetadata(output, imageReference);
    }

    private static async Task<IContainerImageInspector> ResolveContainerImageInspectorAsync(PipelineStepContext context)
    {
        var runtime = await context.Services.GetRequiredService<IContainerRuntimeResolver>().ResolveAsync(context.CancellationToken).ConfigureAwait(false);
        return runtime as IContainerImageInspector
            ?? throw new NotSupportedException($"Container runtime '{runtime.Name}' does not support image inspection, which is required for Azure sandbox deployment.");
    }

    internal static ContainerImageMetadata ParseContainerImageMetadata(string output, string imageReference)
    {
        JsonNode? config;
        try
        {
            config = JsonNode.Parse(output);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Container runtime returned invalid image metadata for '{imageReference}'.", ex);
        }

        if (config is not JsonObject configObject)
        {
            throw new InvalidOperationException($"Container runtime did not return image metadata for '{imageReference}'.");
        }

        return new ContainerImageMetadata(
            ReadCommandParts(configObject["Entrypoint"]).ToArray(),
            ReadCommandParts(configObject["Cmd"]).ToArray(),
            new Dictionary<string, string>(StringComparer.Ordinal),
            configObject["WorkingDir"]?.GetValue<string>());
    }

    private static IEnumerable<string> ReadCommandParts(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item?.GetValue<string>() is { } value)
                    {
                        yield return value;
                    }
                }
                break;
            case JsonValue value when value.GetValue<string>() is { } command:
                yield return command;
                break;
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentVariablesAsync(PipelineStepContext context, IResource resource)
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
        foreach (var (key, value) in environmentVariables)
        {
            result[key] = await ResolveValueAsync(context, resource, value).ConfigureAwait(false);
        }

        return result;
    }

    internal static async Task<string> ResolveValueAsync(PipelineStepContext context, IResource resource, object? value)
    {
        var currentComputeEnvironment = resource.GetComputeEnvironment() ?? resource.GetDeploymentTargetAnnotation()?.ComputeEnvironment;

        while (true)
        {
            switch (value)
            {
                case null:
                    return string.Empty;
                case string s:
                    return s;
                case IResourceWithConnectionString connectionStringResource:
                    value = connectionStringResource.ConnectionStringExpression;
                    continue;
                case EndpointReference endpointReference
                    when TryResolveEndpointReferenceValue(endpointReference, currentComputeEnvironment, out var endpointExpression):
                    value = endpointExpression;
                    continue;
                case EndpointReferenceExpression endpointReferenceExpression
                    when TryResolveEndpointReferenceValue(endpointReferenceExpression, currentComputeEnvironment, out var endpointExpression):
                    value = endpointExpression;
                    continue;
                case ReferenceExpression referenceExpression:
                    return await ResolveReferenceExpressionAsync(context, resource, referenceExpression).ConfigureAwait(false);
                case IValueProvider valueProvider:
                    return await valueProvider
                        .GetValueAsync(new ValueProviderContext { ExecutionContext = context.ExecutionContext, Caller = resource }, context.CancellationToken)
                        .ConfigureAwait(false) ?? string.Empty;
                default:
                    return value.ToString() ?? string.Empty;
            }
        }

        static async Task<string> ResolveReferenceExpressionAsync(
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
                    ? string.Empty
                    : await ResolveReferenceExpressionAsync(context, resource, branch).ConfigureAwait(false);
            }

            var arguments = new object?[expression.ValueProviders.Count];
            for (var i = 0; i < expression.ValueProviders.Count; i++)
            {
                var resolved = await ResolveValueAsync(context, resource, expression.ValueProviders[i]).ConfigureAwait(false);
                arguments[i] = expression.StringFormats[i] is { } format
                    ? FormatReferenceValue(resolved, format)
                    : resolved;
            }

            return string.Format(CultureInfo.InvariantCulture, expression.Format, arguments);
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

    private static async Task DestroyAsync(PipelineStepContext context, AzureSandboxContainerResource resource)
    {
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        var stateSection = await deploymentStateManager.AcquireSectionAsync(GetStateSectionName(resource), context.CancellationToken).ConfigureAwait(false);

        var ownerId = stateSection.Data["OwnerId"]?.GetValue<string>();
        var applicationName = context.Services.GetRequiredService<IHostEnvironment>().ApplicationName;
        if (!HasRemoteDeploymentState(stateSection))
        {
            try
            {
                var azureState = await GetAzureStateAsync(deploymentStateManager, context.CancellationToken).ConfigureAwait(false);
                var fallbackScope = new AzureDevComputeResourceScope(
                    azureState.SubscriptionId,
                    azureState.ResourceGroup,
                    GetRequiredOutput(resource.Parent, "name"),
                    azureState.Location);
                var stableOwnerId = CreateStableOwnerId(applicationName, fallbackScope, resource.Name);
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
            }
            catch (InvalidOperationException ex)
            {
                context.Logger.LogWarning(ex, "Sandbox deployment state and Azure scope outputs were unavailable, so data-plane cleanup could not run before the Azure resource group cleanup.");
                await context.ReportingStep.CompleteAsync(
                    "No sandbox deployment state or stable cleanup scope was available.",
                    CompletionState.CompletedWithWarning,
                    context.CancellationToken).ConfigureAwait(false);
            }
            return;
        }

        var client = CreateAzureDevComputeClient(context);
        var scope = new AzureDevComputeResourceScope(
            GetRequiredStateValue(stateSection, "SubscriptionId"),
            GetRequiredStateValue(stateSection, "ResourceGroup"),
            GetRequiredStateValue(stateSection, "SandboxGroup"),
            GetRequiredStateValue(stateSection, "Location"));

        await DeleteExistingDeploymentAsync(context, client, scope, stateSection, throwOnError: true).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(ownerId))
        {
            await DeleteRemoteDeploymentsByResourceLabelAsync(context, client, scope, ownerId, resource.Name, s_noExcludedIds, s_noExcludedIds, s_noExcludedIds, throwOnError: true).ConfigureAwait(false);
        }
        var stableOwnerIdForScope = CreateStableOwnerId(applicationName, scope, resource.Name);
        if (!string.Equals(ownerId, stableOwnerIdForScope, StringComparison.Ordinal))
        {
            await DeleteRemoteDeploymentsByResourceLabelAsync(context, client, scope, stableOwnerIdForScope, resource.Name, s_noExcludedIds, s_noExcludedIds, s_noExcludedIds, throwOnError: true).ConfigureAwait(false);
        }

        await deploymentStateManager.DeleteSectionAsync(stateSection, context.CancellationToken).ConfigureAwait(false);
    }

    private static async Task DestroyStaleDeploymentsAsync(PipelineStepContext context, IReadOnlySet<string> activeStateSectionNames)
    {
        var deploymentStateManager = context.Services.GetRequiredService<IDeploymentStateManager>();
        var sandboxesSection = await deploymentStateManager.AcquireSectionAsync(SandboxStateParentSection, context.CancellationToken).ConfigureAwait(false);

        var staleResourceNames = sandboxesSection.Data
            .Where(pair => pair.Value is JsonObject)
            .Select(pair => $"{SandboxStateSectionPrefix}{pair.Key}")
            .Where(sectionName => !activeStateSectionNames.Contains(sectionName))
            .ToArray();

        foreach (var sectionName in staleResourceNames)
        {
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
                await DeleteExistingDeploymentAsync(context, client, scope, stateSection, throwOnError: true).ConfigureAwait(false);
                if (stateSection.Data["OwnerId"]?.GetValue<string>() is { Length: > 0 } ownerId)
                {
                    await DeleteRemoteDeploymentsByResourceLabelAsync(context, client, scope, ownerId, GetStateResourceName(stateSection, sectionName), s_noExcludedIds, s_noExcludedIds, s_noExcludedIds, throwOnError: true).ConfigureAwait(false);
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
            StringComparer.Ordinal);
        var unmatchedEndpointOptions = endpointOptions is null ? null : new HashSet<string>(endpointOptions.Keys, StringComparer.Ordinal);
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
            var authorizedConnectorGateways = resource.TargetResource.Annotations
                .OfType<AzureConnectorGatewayEndpointAuthorizationAnnotation>()
                .Where(annotation => string.Equals(
                    annotation.EndpointName,
                    resolvedEndpoint.Endpoint.Name,
                    StringComparison.Ordinal))
                .Select(static annotation => annotation.ConnectorGateway)
                .DistinctBy(static gateway => gateway.Name, StringComparers.ResourceName)
                .ToArray();
            if (resolvedEndpointOptions?.Anonymous == true && authorizedConnectorGateways.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Endpoint '{resolvedEndpoint.Endpoint.Name}' on resource '{resource.TargetResource.Name}' is a Connector Namespace trigger callback and cannot allow anonymous access.");
            }

            var endpoint = new SandboxEndpoint(
                resolvedEndpoint.Endpoint.Name,
                targetPort,
                resolvedEndpoint.Endpoint.IsExternal,
                IsHttp: true,
                protocol,
                resolvedEndpointOptions?.Anonymous ?? false,
                authorizedConnectorGateways);

            if (endpoints.TryGetValue(targetPort, out var existingEndpoint))
            {
                if (!string.Equals(existingEndpoint.Protocol, endpoint.Protocol, StringComparison.Ordinal))
                {
                    throw new NotSupportedException($"Endpoint '{resolvedEndpoint.Endpoint.Name}' on resource '{resource.TargetResource.Name}' shares target port {targetPort} with endpoint '{existingEndpoint.Name}' but uses a different transport. Azure sandbox ports support a single HTTP protocol per target port.");
                }

                endpoints[targetPort] = existingEndpoint with
                {
                    IsExternal = existingEndpoint.IsExternal || endpoint.IsExternal,
                    IsHttp = existingEndpoint.IsHttp || endpoint.IsHttp,
                    Anonymous = MergeAnonymousAccess(existingEndpoint.Anonymous, endpoint.Anonymous),
                    AuthorizedConnectorGateways = existingEndpoint.AuthorizedConnectorGateways
                        .Concat(endpoint.AuthorizedConnectorGateways)
                        .DistinctBy(static gateway => gateway.Name, StringComparers.ResourceName)
                        .ToArray()
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

    private static bool? MergeAnonymousAccess(bool? existing, bool? current)
    {
        if (existing == false || current == false)
        {
            return false;
        }

        return existing ?? current;
    }

    private static async Task DeleteExistingDeploymentAsync(
        PipelineStepContext context,
        IAzureDevComputeClient client,
        AzureDevComputeResourceScope scope,
        DeploymentStateSection stateSection,
        bool throwOnError)
    {
        var sandboxId = stateSection.Data["SandboxId"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(sandboxId))
        {
            await DeleteSandboxAsync(context, client, scope, sandboxId, GetStatePorts(stateSection), throwOnError).ConfigureAwait(false);
        }

        var diskImageId = stateSection.Data["DiskImageId"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(diskImageId))
        {
            await DeleteDiskImageAsync(context, client, scope, diskImageId, throwOnError).ConfigureAwait(false);
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
        string applicationName,
        AzureDevComputeResourceScope scope,
        string resourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var identity = string.Join(
            "\n",
            [
                applicationName.ToLowerInvariant(),
                scope.SubscriptionId.ToLowerInvariant(),
                scope.ResourceGroupName.ToLowerInvariant(),
                scope.SandboxGroupName.ToLowerInvariant(),
                resourceName.ToLowerInvariant()
            ]);
        var hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(identity));
        return $"aspire-{hash:x16}";
    }

    internal static string CreateDeploymentSecurityFingerprint(
        IReadOnlyList<SandboxEndpoint> endpoints,
        string imageReference)
    {
        return $"{imageReference}|{string.Join(
            "|",
            endpoints
                .OrderBy(static endpoint => endpoint.Name, StringComparer.Ordinal)
                .Select(static endpoint =>
                    $"{endpoint.Name}:{endpoint.TargetPort}:{endpoint.Protocol}:{endpoint.IsExternal}:{endpoint.Anonymous}:{string.Join(",", endpoint.AuthorizedConnectorGateways.Select(static gateway => gateway.Name).Order(StringComparers.ResourceName))}"))}";
    }

    internal static bool HasSecurityRelevantEndpointChange(
        DeploymentStateSection previousStateSection,
        string currentFingerprint)
    {
        if (!HasRemoteDeploymentState(previousStateSection))
        {
            return false;
        }

        if (previousStateSection.Data["PendingSecurityCleanup"]?.GetValue<bool>() == true)
        {
            return true;
        }

        var previousFingerprint = previousStateSection.Data["EndpointSecurityFingerprint"]?.GetValue<string>();
        return !string.Equals(previousFingerprint, currentFingerprint, StringComparison.Ordinal);
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
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
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

    private static async Task DeleteSandboxAsync(
        PipelineStepContext context,
        IAzureDevComputeClient client,
        AzureDevComputeResourceScope scope,
        string sandboxId,
        IEnumerable<int> ports,
        bool throwOnError,
        CancellationToken? cancellationToken = null)
    {
        var effectiveCancellationToken = cancellationToken ?? context.CancellationToken;

        try
        {
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
                catch (Exception ex) when (!throwOnError && ex is not OperationCanceledException)
                {
                    context.Logger.LogWarning(ex, "Failed to remove sandbox port {Port} from sandbox '{SandboxId}'.", port, sandboxId);
                }
            }

            await client.DeleteSandboxAsync(scope, sandboxId, effectiveCancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!throwOnError && ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "Failed to delete sandbox '{SandboxId}'.", sandboxId);
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
        try
        {
            await client.DeleteDiskImageAsync(scope, diskImageId, cancellationToken ?? context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!throwOnError && ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "Failed to delete sandbox disk image '{DiskImageId}'.", diskImageId);
        }
    }

    private static async Task<AzureDeploymentState> GetAzureStateAsync(IDeploymentStateManager deploymentStateManager, CancellationToken cancellationToken)
    {
        var azureState = await deploymentStateManager.AcquireSectionAsync("Azure", cancellationToken).ConfigureAwait(false);
        return new AzureDeploymentState(
            GetRequiredStateValue(azureState, "SubscriptionId"),
            GetRequiredStateValue(azureState, "ResourceGroup"),
            GetRequiredStateValue(azureState, "Location"));
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
        bool? Anonymous,
        IReadOnlyList<AzureConnectorGatewayResource> AuthorizedConnectorGateways);

    internal sealed record ContainerImageMetadata(IReadOnlyList<string> Entrypoint, IReadOnlyList<string> Command, IReadOnlyDictionary<string, string> EnvironmentVariables, string? WorkingDirectory);

    private sealed record AzureDeploymentState(string SubscriptionId, string ResourceGroup, string Location);

}
