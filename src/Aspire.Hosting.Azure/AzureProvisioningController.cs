// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Azure.Resources;
using Azure;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Coordinates Azure run-mode provisioning, recovery, and drift detection through a single serialized control loop.
/// </summary>
/// <remarks>
/// <para>
/// The controller uses a channel-based queue with a single reader to serialize all Azure operations. Every
/// public method (provision, reprovision, reset, change-location, change-context, delete, drift-check) wraps
/// a typed intent record and writes it to the channel. A background loop dequeues one intent at a time,
/// executes it, and completes the caller's TaskCompletionSource with the result. This eliminates races between
/// concurrent dashboard commands, CLI commands, and the periodic drift monitor.
/// </para>
/// <para>
/// Within a provisioning pass, individual resources are fanned out concurrently but ordered by dependency.
/// Each resource gets a per-resource ProvisioningTaskCompletionSource that downstream resources await before
/// starting their own deployment. This TCS is completed by CompleteProvisioning/FailProvisioning — the only
/// two completion paths — so dependent resources unblock as soon as their prerequisites finish, not when the
/// entire batch completes.
/// </para>
/// <para>
/// The controller tracks lightweight in-memory state (AzureControllerState) under a lock. This state drives
/// command enablement in the dashboard (commands are disabled while an operation targeting the same resources
/// is running) and provides the Azure identity properties shown on the AzureEnvironmentResource.
/// </para>
/// <para>
/// Location overrides let a user deploy a single resource to a different Azure region. Overrides are persisted
/// in the deployment state store and survive resets/reprovisioning. When a location change is requested, the
/// controller deletes the existing Azure resource first (to avoid ARM InvalidResourceLocation conflicts), sets
/// the override, and reprovisions.
/// </para>
/// <para>
/// Drift detection runs on a periodic timer. It probes ARM to verify each running resource still exists and
/// marks missing resources as "Missing in Azure" / the environment as "Drifted". The drift monitor queues at
/// most one check at a time through the same serialized channel.
/// </para>
/// <para>
/// The controller only orchestrates run-mode behavior. Deployment state persistence, Bicep compilation, and
/// ARM deployment are delegated to BicepProvisioner. Publish-time resource creation flows through separate
/// publishing contexts.
/// </para>
/// </remarks>
internal sealed class AzureProvisioningController(
    IConfiguration configuration,
    IOptions<AzureProvisionerOptions> provisionerOptions,
    IServiceProvider serviceProvider,
    IBicepProvisioner bicepProvisioner,
    IDeploymentStateManager deploymentStateManager,
    IDistributedApplicationEventing eventing,
    IProvisioningContextProvider provisioningContextProvider,
    IAzureProvisioningOptionsManager provisioningOptionsManager,
    ResourceNotificationService notificationService,
    ResourceLoggerService loggerService,
    ILogger<AzureProvisioningController> logger)
{
    internal const string ForgetStateCommandName = "forget-state";
    internal const string ChangeResourceLocationCommandName = "change-location";
    internal const string ReprovisionResourceCommandName = "reprovision";
    internal const string ResetProvisioningStateCommandName = "reset-provisioning-state";
    internal const string ChangeAzureContextCommandName = "change-azure-context";
    internal const string ReprovisionAllCommandName = "reprovision-all";
    internal const string DeleteAzureResourcesCommandName = "delete-azure-resources";
    internal const string LocationOverrideKey = "LocationOverride";
    internal const string MissingInAzureState = "Missing in Azure";
    internal const string DriftedState = "Drifted";

    private static readonly string[] s_resettableProperties =
    [
        "azure.subscription.id",
        "azure.resource.group",
        "azure.tenant.domain",
        "azure.tenant.id",
        "azure.location",
        CustomResourceKnownProperties.Source
    ];

    private readonly Channel<QueuedOperation> _operationChannel = Channel.CreateUnbounded<QueuedOperation>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ILogger<AzureProvisioningController> _logger = logger;
    private readonly object _operationStateLock = new();
    private AzureControllerState _state = AzureControllerState.Empty;
    private int _operationLoopStarted;
    private int _driftMonitorStarted;
    private bool _driftCheckQueued;

    // Drift checks are intentionally periodic and non-overlapping. The monitor queues at most one check at a time so
    // command execution and background drift probing share the same serialized control loop.
    internal TimeSpan DriftCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    internal static ImmutableArray<EnvironmentCommandDefinition> EnvironmentCommandDefinitions { get; } =
    [
        new(
            AzureEnvironmentCommand.ResetProvisioningState,
            ResetProvisioningStateCommandName,
            AzureProvisioningStrings.ResetProvisioningStateCommandName,
            AzureProvisioningStrings.ResetProvisioningStateCommandDescription,
            AzureProvisioningStrings.ResetProvisioningStateCommandConfirmation,
            "ArrowSync",
            IconVariant.Regular,
            IsHighlighted: true),
        new(
            AzureEnvironmentCommand.ChangeAzureContext,
            ChangeAzureContextCommandName,
            AzureProvisioningStrings.ChangeAzureContextCommandName,
            AzureProvisioningStrings.ChangeAzureContextCommandDescription,
            AzureProvisioningStrings.ChangeAzureContextCommandConfirmation,
            "Edit",
            IconVariant.Regular,
            IsHighlighted: true),
        new(
            AzureEnvironmentCommand.ReprovisionAll,
            ReprovisionAllCommandName,
            AzureProvisioningStrings.ReprovisionAllCommandName,
            AzureProvisioningStrings.ReprovisionAllCommandDescription,
            AzureProvisioningStrings.ReprovisionAllCommandConfirmation,
            "ArrowSync",
            IconVariant.Regular,
            IsHighlighted: true),
        new(
            AzureEnvironmentCommand.DeleteAzureResources,
            DeleteAzureResourcesCommandName,
            AzureProvisioningStrings.DeleteAzureResourcesCommandName,
            AzureProvisioningStrings.DeleteAzureResourcesCommandDescription,
            AzureProvisioningStrings.DeleteAzureResourcesCommandConfirmation,
            "Delete",
            IconVariant.Regular,
            IsHighlighted: true)
    ];

    internal static ImmutableArray<ResourceCommandDefinition> ResourceCommandDefinitions { get; } =
    [
        new(
            AzureResourceCommand.ChangeLocation,
            ChangeResourceLocationCommandName,
            AzureProvisioningStrings.ChangeResourceLocationCommandName,
            AzureProvisioningStrings.ChangeResourceLocationCommandDescription,
            ConfirmationMessage: null,
            "Location",
            IconVariant.Regular,
            IsHighlighted: false),
        new(
            AzureResourceCommand.ForgetState,
            ForgetStateCommandName,
            AzureProvisioningStrings.ForgetStateCommandName,
            AzureProvisioningStrings.ForgetStateCommandDescription,
            AzureProvisioningStrings.ForgetStateCommandConfirmation,
            "ArrowReset",
            IconVariant.Regular,
            IsHighlighted: false),
        new(
            AzureResourceCommand.Reprovision,
            ReprovisionResourceCommandName,
            AzureProvisioningStrings.ReprovisionResourceCommandName,
            AzureProvisioningStrings.ReprovisionResourceCommandDescription,
            AzureProvisioningStrings.ReprovisionResourceCommandConfirmation,
            "ArrowSync",
            IconVariant.Regular,
            IsHighlighted: true)
    ];

    public async Task ResetStateAsync(DistributedApplicationModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        await RunOperationAsync(model, new ResetStateIntent(), cancellationToken).ConfigureAwait(false);
    }

    public async Task ForgetResourceStateAsync(DistributedApplicationModel model, string resourceName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        await RunOperationAsync(model, new ForgetResourceStateIntent(resourceName), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ChangeAzureContextAsync(DistributedApplicationModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        return await RunOperationAsync<bool>(model, new ChangeAzureContextIntent(), cancellationToken).ConfigureAwait(false);
    }

    public Task EnsureProvisionedAsync(DistributedApplicationModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        return RunOperationAsync(model, new EnsureProvisionedIntent(), cancellationToken);
    }

    public async Task<bool> ReprovisionAllAsync(DistributedApplicationModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        return await RunOperationAsync<bool>(model, new ReprovisionAllIntent(), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAzureResourcesAsync(DistributedApplicationModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        await RunOperationAsync(model, new DeleteAzureResourcesIntent(), cancellationToken).ConfigureAwait(false);
    }

    public async Task CheckForDriftAsync(DistributedApplicationModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        lock (_operationStateLock)
        {
            if (_state.Status.CurrentIntent is not null || _driftCheckQueued)
            {
                return;
            }

            _driftCheckQueued = true;
        }

        try
        {
            await EnqueueOperationAsync(model, new DetectDriftIntent(), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_operationStateLock)
            {
                _driftCheckQueued = false;
            }

            throw;
        }
    }

    public async Task<bool> ReprovisionResourceAsync(DistributedApplicationModel model, string resourceName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        return await RunOperationAsync<bool>(model, new ReprovisionResourceIntent(resourceName), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ChangeResourceLocationAsync(DistributedApplicationModel model, string resourceName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        var interactionService = serviceProvider.GetRequiredService<IInteractionService>();
        if (!interactionService.IsAvailable)
        {
            throw new MissingConfigurationException("Azure resource location can't be changed because the interaction service is unavailable.");
        }

        var currentLocation = await GetEffectiveResourceLocationAsync(resourceName, cancellationToken).ConfigureAwait(false);
        var locationOptions = await GetLocationOptionsAsync(cancellationToken).ConfigureAwait(false);
        var useChoiceInput = locationOptions.Count > 0;

        var result = await interactionService.PromptInputsAsync(
            AzureProvisioningStrings.ChangeResourceLocationPromptTitle,
            string.Format(CultureInfo.CurrentCulture, AzureProvisioningStrings.ChangeResourceLocationPromptMessage, resourceName),
            [
                new InteractionInput
                {
                    Name = AzureBicepResource.KnownParameters.Location,
                    Label = AzureProvisioningStrings.LocationLabel,
                    Placeholder = AzureProvisioningStrings.LocationPlaceholder,
                    InputType = useChoiceInput ? InputType.Choice : InputType.Text,
                    AllowCustomChoice = true,
                    Required = true,
                    Value = currentLocation,
                    Options = locationOptions
                }
            ],
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.Canceled)
        {
            return false;
        }

        var location = result.Data[AzureBicepResource.KnownParameters.Location].Value;
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        location = NormalizeLocation(location, locationOptions);

        return await RunOperationAsync<bool>(model, new ChangeResourceLocationIntent(resourceName, location), cancellationToken).ConfigureAwait(false);
    }

    internal Task<ExecuteCommandResult> ExecuteEnvironmentCommandAsync(AzureEnvironmentCommand command, ExecuteCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var model = context.ServiceProvider.GetRequiredService<DistributedApplicationModel>();

        return command switch
        {
            AzureEnvironmentCommand.ResetProvisioningState => ExecuteCommandAsync(
                () => ResetStateAsync(model, context.CancellationToken),
                AzureProvisioningStrings.ResetProvisioningStateCommandSuccess),
            AzureEnvironmentCommand.ChangeAzureContext => ExecuteCommandAsync(
                () => ChangeAzureContextAsync(model, context.CancellationToken),
                AzureProvisioningStrings.ChangeAzureContextCommandSuccess),
            AzureEnvironmentCommand.ReprovisionAll => ExecuteCommandAsync(
                () => ReprovisionAllAsync(model, context.CancellationToken),
                AzureProvisioningStrings.ReprovisionAllCommandSuccess),
            AzureEnvironmentCommand.DeleteAzureResources => ExecuteCommandAsync(
                () => DeleteAzureResourcesAsync(model, context.CancellationToken),
                AzureProvisioningStrings.DeleteAzureResourcesCommandSuccess),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
    }

    internal Task<ExecuteCommandResult> ExecuteResourceCommandAsync(AzureResourceCommand command, string resourceName, ExecuteCommandContext context)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        ArgumentNullException.ThrowIfNull(context);

        var model = context.ServiceProvider.GetRequiredService<DistributedApplicationModel>();

        return command switch
        {
            AzureResourceCommand.ChangeLocation => ExecuteCommandAsync(
                () => ChangeResourceLocationAsync(model, resourceName, context.CancellationToken),
                AzureProvisioningStrings.ChangeResourceLocationCommandSuccess),
            AzureResourceCommand.ForgetState => ExecuteCommandAsync(
                () => ForgetResourceStateAsync(model, resourceName, context.CancellationToken),
                AzureProvisioningStrings.ForgetStateCommandSuccess),
            AzureResourceCommand.Reprovision => ExecuteCommandAsync(
                () => ReprovisionResourceAsync(model, resourceName, context.CancellationToken),
                AzureProvisioningStrings.ReprovisionResourceCommandSuccess),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
    }

    internal ResourceCommandState GetEnvironmentCommandState()
    {
        lock (_operationStateLock)
        {
            return _state.Status.CurrentIntent is null ? ResourceCommandState.Enabled : ResourceCommandState.Disabled;
        }
    }

    internal ResourceCommandState GetResourceCommandState(string resourceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        lock (_operationStateLock)
        {
            var currentOperation = _state.Status.CurrentIntent?.Operation;
            if (currentOperation is null)
            {
                return ResourceCommandState.Enabled;
            }

            return currentOperation.IsAllResources || currentOperation.ResourceNames.Contains(resourceName)
                ? ResourceCommandState.Disabled
                : ResourceCommandState.Enabled;
        }
    }

    private async Task RunOperationAsync(DistributedApplicationModel model, AzureIntent intent, CancellationToken cancellationToken)
    {
        _ = await EnqueueOperationAsync(
            model,
            intent,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RunOperationAsync<T>(DistributedApplicationModel model, AzureIntent intent, CancellationToken cancellationToken)
    {
        return (T)(await EnqueueOperationAsync(
            model,
            intent,
            cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<bool> EnsureProvisionedCoreAsync(
        DistributedApplicationModel model,
        IReadOnlyList<(IResource Resource, IAzureResource AzureResource)> azureResources,
        CancellationToken cancellationToken)
    {
        if (azureResources.Count == 0)
        {
            return true;
        }

        await PublishAzureEnvironmentStateAsync(
            model,
            new ResourceStateSnapshot("Starting", KnownResourceStateStyles.Info),
            cancellationToken).ConfigureAwait(false);

        var parentChildLookup = model.Resources.OfType<IResourceWithParent>().ToLookup(r => r.Parent);
        var afterProvisionTasks = new List<Task>(azureResources.Count);

        foreach (var resource in azureResources)
        {
            await ApplyResourceOverridesAsync(resource.AzureResource, cancellationToken).ConfigureAwait(false);

            // Per-resource provisioning completion is used to sequence dependent Azure resources. A resource completes
            // this TCS as soon as its own cached state is applied or its deployment finishes so dependents do not wait
            // for unrelated resources in the same batch.
            resource.AzureResource.ProvisioningTaskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

            await PublishUpdateToResourceTreeAsync(resource, parentChildLookup, state => state with
            {
                State = new("Starting", KnownResourceStateStyles.Info)
            }).ConfigureAwait(false);

            afterProvisionTasks.Add(AfterProvisionAsync(resource, parentChildLookup));
        }

        await ProvisionAzureResourcesAsync(azureResources, parentChildLookup, cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(afterProvisionTasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        var hasFailures = azureResources.Any(static resource =>
            resource.AzureResource.ProvisioningTaskCompletionSource?.Task.IsFaulted == true);

        await PublishAzureEnvironmentStateAsync(
            model,
            hasFailures
                ? new ResourceStateSnapshot("Failed to Provision", KnownResourceStateStyles.Error)
                : new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            cancellationToken).ConfigureAwait(false);

        return !hasFailures;
    }

    private async Task ResetResourcesAsync(
        DistributedApplicationModel model,
        IReadOnlyCollection<(IResource Resource, IAzureResource AzureResource)> azureResources,
        bool preserveOverrides,
        CancellationToken cancellationToken)
    {
        var parentChildLookup = model.Resources.OfType<IResourceWithParent>().ToLookup(r => r.Parent);
        var environmentLocation = preserveOverrides
            ? (await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false)).Location
            : null;

        foreach (var resource in azureResources)
        {
            if (resource.AzureResource is not AzureBicepResource bicepResource)
            {
                continue;
            }

            var currentLocationOverride = preserveOverrides
                ? TryGetCurrentResourceLocationOverride(bicepResource, environmentLocation)
                : null;

            if (currentLocationOverride is not null)
            {
                bicepResource.Parameters[AzureBicepResource.KnownParameters.Location] = currentLocationOverride;
            }

            await ClearCachedDeploymentStateAsync(bicepResource, preserveOverrides, environmentLocation, currentLocationOverride, cancellationToken).ConfigureAwait(false);

            bicepResource.Outputs.Clear();
            bicepResource.SecretOutputs.Clear();

            if (bicepResource is IAzureKeyVaultResource keyVaultResource)
            {
                keyVaultResource.SecretResolver = null;
            }

            await PublishUpdateToResourceTreeAsync(resource, parentChildLookup, state => state with
            {
                State = KnownResourceStates.NotStarted,
                Properties = FilterProperties(state.Properties),
                Urls = [],
                CreationTimeStamp = null,
                StartTimeStamp = null,
                StopTimeStamp = null
            }).ConfigureAwait(false);
        }
    }

    private async Task DeleteSectionAsync(string sectionName, CancellationToken cancellationToken)
    {
        var section = await deploymentStateManager.AcquireSectionAsync(sectionName, cancellationToken).ConfigureAwait(false);
        section.Data.Clear();
        await deploymentStateManager.DeleteSectionAsync(section, cancellationToken).ConfigureAwait(false);
    }

    private static List<(IResource Resource, IAzureResource AzureResource)> GetProvisionableAzureResources(DistributedApplicationModel model)
    {
        return [.. AzureResourcePreparer.GetAzureResourcesFromAppModel(model).Where(static resource =>
            resource.AzureResource is AzureBicepResource bicepResource &&
            !bicepResource.IsContainer() &&
            !bicepResource.IsEmulator())];
    }

    private static List<(IResource Resource, IAzureResource AzureResource)> GetTargetAzureResources(DistributedApplicationModel model, string resourceName)
    {
        var azureResources = GetProvisionableAzureResources(model);
        var targetResource = azureResources.SingleOrDefault(resource =>
            string.Equals(resource.Resource.Name, resourceName, StringComparison.Ordinal) ||
            string.Equals(resource.AzureResource.Name, resourceName, StringComparison.Ordinal));

        if (targetResource == default)
        {
            throw new InvalidOperationException($"Azure resource '{resourceName}' was not found or cannot be reprovisioned.");
        }

        var parentChildLookup = model.Resources.OfType<IResourceWithParent>().ToLookup(r => r.Parent);
        var visitedResources = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(IResource Resource, IAzureResource AzureResource)>();
        var targetResources = new List<(IResource Resource, IAzureResource AzureResource)>();

        Enqueue(targetResource);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            targetResources.Add(current);

            foreach (var child in parentChildLookup[current.Resource])
            {
                if (TryGetAzureResource(azureResources, child, out var childResource))
                {
                    Enqueue(childResource);
                }
            }

            if (!ReferenceEquals(current.Resource, current.AzureResource))
            {
                foreach (var child in parentChildLookup[current.AzureResource])
                {
                    if (TryGetAzureResource(azureResources, child, out var childResource))
                    {
                        Enqueue(childResource);
                    }
                }
            }

            if (current.AzureResource.TryGetAnnotationsOfType<RoleAssignmentResourceAnnotation>(out var roleAssignments))
            {
                foreach (var roleAssignment in roleAssignments)
                {
                    if (TryGetAzureResource(azureResources, roleAssignment.RolesResource, out var roleAssignmentResource))
                    {
                        Enqueue(roleAssignmentResource);
                    }
                }
            }
        }

        return targetResources;

        void Enqueue((IResource Resource, IAzureResource AzureResource) resource)
        {
            if (visitedResources.Add(resource.Resource.Name))
            {
                queue.Enqueue(resource);
            }
        }
    }

    private static bool TryGetAzureResource(
        IReadOnlyList<(IResource Resource, IAzureResource AzureResource)> azureResources,
        IResource target,
        out (IResource Resource, IAzureResource AzureResource) azureResource)
    {
        foreach (var resource in azureResources)
        {
            if (ReferenceEquals(resource.Resource, target) || ReferenceEquals(resource.AzureResource, target))
            {
                azureResource = resource;
                return true;
            }
        }

        azureResource = default;
        return false;
    }

    private async Task<object?> EnqueueOperationAsync(
        DistributedApplicationModel model,
        AzureIntent intent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDriftMonitorStarted(model);
        EnsureOperationLoopStarted();

        var queuedOperation = new QueuedOperation(
            model,
            intent,
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            cancellationToken);

        await _operationChannel.Writer.WriteAsync(queuedOperation, cancellationToken).ConfigureAwait(false);
        return await queuedOperation.Completion.Task.ConfigureAwait(false);
    }

    private void EnsureDriftMonitorStarted(DistributedApplicationModel model)
    {
        if (Interlocked.CompareExchange(ref _driftMonitorStarted, 1, 0) != 0)
        {
            return;
        }

        var stoppingToken = serviceProvider.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        var timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;

        _ = Task.Run(async () =>
        {
            try
            {
                using var periodic = new PeriodicTimer(DriftCheckInterval, timeProvider);

                while (await periodic.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    try
                    {
                        await CheckForDriftAsync(model, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Azure drift check failed.");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }, stoppingToken);
    }

    private void EnsureOperationLoopStarted()
    {
        if (Interlocked.CompareExchange(ref _operationLoopStarted, 1, 0) == 0)
        {
            _ = Task.Run(ProcessOperationLoopAsync);
        }
    }

    private async Task ProcessOperationLoopAsync()
    {
        await foreach (var operation in _operationChannel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (operation.CancellationToken.IsCancellationRequested)
            {
                if (operation.Intent is DetectDriftIntent)
                {
                    CompleteDriftCheck();
                }

                operation.Completion.TrySetCanceled(operation.CancellationToken);
                continue;
            }

            await ProcessQueuedOperationAsync(operation).ConfigureAwait(false);
        }
    }

    private async Task ProcessQueuedOperationAsync(QueuedOperation queuedOperation)
    {
        var updatesCommandState = queuedOperation.Intent is not DetectDriftIntent;
        if (updatesCommandState)
        {
            StartOperation(queuedOperation.Intent);
            await RefreshCommandStatesAsync(queuedOperation.Model, queuedOperation.CancellationToken).ConfigureAwait(false);
        }

        try
        {
            var result = await ExecuteIntentAsync(queuedOperation.Model, queuedOperation.Intent, queuedOperation.CancellationToken).ConfigureAwait(false);
            queuedOperation.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException ex) when (queuedOperation.CancellationToken.IsCancellationRequested || ex.CancellationToken == queuedOperation.CancellationToken)
        {
            queuedOperation.Completion.TrySetCanceled(queuedOperation.CancellationToken.IsCancellationRequested ? queuedOperation.CancellationToken : ex.CancellationToken);
        }
        catch (Exception ex)
        {
            queuedOperation.Completion.TrySetException(ex);
        }
        finally
        {
            if (updatesCommandState)
            {
                CompleteOperation(queuedOperation.Intent);
                await RefreshCommandStatesAsync(queuedOperation.Model, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                CompleteDriftCheck();
            }
        }
    }

    private async Task<object?> ExecuteIntentAsync(DistributedApplicationModel model, AzureIntent intent, CancellationToken cancellationToken)
    {
        return intent switch
        {
            ResetStateIntent => await ExecuteResetStateAsync(model, cancellationToken).ConfigureAwait(false),
            ForgetResourceStateIntent forgetResourceState => await ExecuteForgetResourceStateAsync(model, forgetResourceState, cancellationToken).ConfigureAwait(false),
            ChangeAzureContextIntent => await ExecuteChangeAzureContextAsync(model, cancellationToken).ConfigureAwait(false),
            EnsureProvisionedIntent => await ExecuteEnsureProvisionedAsync(model, cancellationToken).ConfigureAwait(false),
            ReprovisionAllIntent => await ExecuteReprovisionAllAsync(model, cancellationToken).ConfigureAwait(false),
            DeleteAzureResourcesIntent => await ExecuteDeleteAzureResourcesAsync(model, cancellationToken).ConfigureAwait(false),
            ChangeResourceLocationIntent changeResourceLocation => await ExecuteChangeResourceLocationAsync(model, changeResourceLocation, cancellationToken).ConfigureAwait(false),
            ReprovisionResourceIntent reprovisionResource => await ExecuteReprovisionResourceAsync(model, reprovisionResource, cancellationToken).ConfigureAwait(false),
            DetectDriftIntent => await ExecuteDetectDriftAsync(model, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(intent))
        };
    }

    private async Task<object?> ExecuteResetStateAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        await DeleteSectionAsync("Azure", cancellationToken).ConfigureAwait(false);

        var azureResources = GetProvisionableAzureResources(model);
        await ResetResourcesAsync(model, azureResources, preserveOverrides: false, cancellationToken).ConfigureAwait(false);

        await PublishAzureEnvironmentStateAsync(model, KnownResourceStates.NotStarted, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Azure provisioning state reset for {Count} Azure resources.", azureResources.Count);
        return null;
    }

    private async Task<object?> ExecuteForgetResourceStateAsync(DistributedApplicationModel model, ForgetResourceStateIntent intent, CancellationToken cancellationToken)
    {
        var targetResources = GetTargetAzureResources(model, intent.ResourceName);
        await ResetResourcesAsync(model, targetResources, preserveOverrides: false, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Azure provisioning state reset for resource {ResourceName}.", intent.ResourceName);
        return null;
    }

    private async Task<bool> ExecuteChangeAzureContextAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        var updated = await provisioningOptionsManager.EnsureProvisioningOptionsAsync(forcePrompt: true, cancellationToken).ConfigureAwait(false);
        if (!updated)
        {
            return false;
        }

        await provisioningOptionsManager.PersistProvisioningOptionsAsync(cancellationToken).ConfigureAwait(false);
        await ResetResourcesAsync(model, GetProvisionableAzureResources(model), preserveOverrides: true, cancellationToken).ConfigureAwait(false);
        await PublishAzureEnvironmentStateAsync(model, KnownResourceStates.NotStarted, cancellationToken).ConfigureAwait(false);
        return await EnsureProvisionedCoreAsync(model, GetProvisionableAzureResources(model), cancellationToken).ConfigureAwait(false);
    }

    private async Task<object?> ExecuteEnsureProvisionedAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        var azureResources = GetProvisionableAzureResources(model);
        await EnsureProvisionedCoreAsync(model, azureResources, cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task<bool> ExecuteReprovisionAllAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        await ResetResourcesAsync(model, GetProvisionableAzureResources(model), preserveOverrides: true, cancellationToken).ConfigureAwait(false);
        await PublishAzureEnvironmentStateAsync(model, KnownResourceStates.NotStarted, cancellationToken).ConfigureAwait(false);
        return await EnsureProvisionedCoreAsync(model, GetProvisionableAzureResources(model), cancellationToken).ConfigureAwait(false);
    }

    private async Task<object?> ExecuteDeleteAzureResourcesAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        await PublishAzureEnvironmentStateAsync(
            model,
            new ResourceStateSnapshot("Deleting", KnownResourceStateStyles.Info),
            cancellationToken).ConfigureAwait(false);

        var provisioningContext = await provisioningContextProvider.CreateProvisioningContextAsync(cancellationToken).ConfigureAwait(false);
        await provisioningContext.ResourceGroup.DeleteAsync(WaitUntil.Completed, cancellationToken).ConfigureAwait(false);

        await ResetResourcesAsync(model, GetProvisionableAzureResources(model), preserveOverrides: true, cancellationToken).ConfigureAwait(false);
        await PublishAzureEnvironmentStateAsync(model, KnownResourceStates.NotStarted, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Deleted Azure resource group {ResourceGroup}.", provisioningContext.ResourceGroup.Name);
        return null;
    }

    private async Task<bool> ExecuteChangeResourceLocationAsync(DistributedApplicationModel model, ChangeResourceLocationIntent intent, CancellationToken cancellationToken)
    {
        var targetResources = GetTargetAzureResources(model, intent.ResourceName);
        if (targetResources[0].AzureResource is AzureBicepResource targetBicepResource)
        {
            await DeleteCachedResourceForLocationChangeAsync(targetBicepResource, intent.Location, cancellationToken).ConfigureAwait(false);
        }

        await SetResourceLocationOverrideAsync(intent.ResourceName, intent.Location, cancellationToken).ConfigureAwait(false);
        await ResetResourcesAsync(model, targetResources, preserveOverrides: true, cancellationToken).ConfigureAwait(false);
        return await EnsureProvisionedCoreAsync(model, targetResources, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ExecuteReprovisionResourceAsync(DistributedApplicationModel model, ReprovisionResourceIntent intent, CancellationToken cancellationToken)
    {
        var targetResources = GetTargetAzureResources(model, intent.ResourceName);
        await ResetResourcesAsync(model, targetResources, preserveOverrides: true, cancellationToken).ConfigureAwait(false);
        return await EnsureProvisionedCoreAsync(model, targetResources, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object?> ExecuteDetectDriftAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        if (model.Resources.OfType<AzureEnvironmentResource>().SingleOrDefault() is not { } environmentResource ||
            !notificationService.TryGetCurrentState(environmentResource.Name, out var environmentEvent) ||
            environmentEvent.Snapshot.State?.Text != KnownResourceStates.Running)
        {
            return null;
        }

        var context = await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(context.SubscriptionId, out _))
        {
            return null;
        }

        var armClientProvider = serviceProvider.GetRequiredService<IArmClientProvider>();
        var tokenCredentialProvider = serviceProvider.GetRequiredService<ITokenCredentialProvider>();
        var armClient = armClientProvider.GetArmClient(tokenCredentialProvider.TokenCredential, context.SubscriptionId);
        var parentChildLookup = model.Resources.OfType<IResourceWithParent>().ToLookup(r => r.Parent);
        List<string>? driftedResources = null;

        foreach (var resource in GetProvisionableAzureResources(model))
        {
            if (!ShouldCheckForDrift(resource.Resource) ||
                await TryGetResourceIdFromDeploymentStateAsync((AzureBicepResource)resource.AzureResource, cancellationToken).ConfigureAwait(false) is not { } resourceId)
            {
                continue;
            }

            if (await armClient.ResourceExistsAsync(resourceId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            driftedResources ??= [];
            driftedResources.Add(resource.Resource.Name);

            await PublishUpdateToResourceTreeAsync(resource, parentChildLookup, state => state with
            {
                State = new(MissingInAzureState, KnownResourceStateStyles.Error)
            }).ConfigureAwait(false);
        }

        if (driftedResources is null)
        {
            return null;
        }

        await PublishAzureEnvironmentStateAsync(
            model,
            new ResourceStateSnapshot(DriftedState, KnownResourceStateStyles.Error),
            cancellationToken).ConfigureAwait(false);

        _logger.LogWarning("Azure drift detected for resources: {ResourceNames}.", string.Join(", ", driftedResources));

        return null;
    }

    private void StartOperation(AzureIntent intent)
    {
        lock (_operationStateLock)
        {
            _state = CreateControllerState(intent);
        }
    }

    private void CompleteOperation(AzureIntent intent)
    {
        lock (_operationStateLock)
        {
            if (ReferenceEquals(_state.Status.CurrentIntent, intent))
            {
                _state = CreateControllerState(currentIntent: null);
            }
        }
    }

    private void CompleteDriftCheck()
    {
        lock (_operationStateLock)
        {
            _driftCheckQueued = false;
        }
    }

    private AzureControllerState CreateControllerState(AzureIntent? currentIntent)
        => new(CreateControllerSpec(), new AzureControllerStatus(currentIntent));

    private AzureControllerSpec CreateControllerSpec()
    {
        var options = provisionerOptions.Value;
        return new(options.SubscriptionId, options.ResourceGroup, options.Location, options.TenantId);
    }

    private static async Task<ExecuteCommandResult> ExecuteCommandAsync(Func<Task> action, string successMessage)
    {
        try
        {
            await action().ConfigureAwait(false);
            return CommandResults.Success(successMessage);
        }
        catch (Exception ex)
        {
            return CommandResults.Failure(ex);
        }
    }

    private static async Task<ExecuteCommandResult> ExecuteCommandAsync(Func<Task<bool>> action, string successMessage)
    {
        try
        {
            return await action().ConfigureAwait(false)
                ? CommandResults.Success(successMessage)
                : CommandResults.Canceled();
        }
        catch (Exception ex)
        {
            return CommandResults.Failure(ex);
        }
    }

    private async Task ApplyResourceOverridesAsync(IAzureResource azureResource, CancellationToken cancellationToken)
    {
        if (azureResource is not AzureBicepResource bicepResource)
        {
            return;
        }

        var section = await deploymentStateManager.AcquireSectionAsync($"Azure:Deployments:{bicepResource.Name}", cancellationToken).ConfigureAwait(false);
        if (section.Data[LocationOverrideKey]?.GetValue<string>() is { Length: > 0 } locationOverride)
        {
            var normalizedLocation = NormalizeLocation(locationOverride, await GetLocationOptionsAsync(cancellationToken).ConfigureAwait(false));
            if (!string.Equals(normalizedLocation, locationOverride, StringComparison.Ordinal))
            {
                section.Data[LocationOverrideKey] = normalizedLocation;
                await deploymentStateManager.SaveSectionAsync(section, cancellationToken).ConfigureAwait(false);
            }

            bicepResource.Parameters[AzureBicepResource.KnownParameters.Location] = normalizedLocation;
        }
    }

    private async Task<string?> GetEffectiveResourceLocationAsync(string resourceName, CancellationToken cancellationToken)
    {
        var section = await deploymentStateManager.AcquireSectionAsync($"Azure:Deployments:{resourceName}", cancellationToken).ConfigureAwait(false);
        if (section.Data[LocationOverrideKey]?.GetValue<string>() is { Length: > 0 } locationOverride)
        {
            return locationOverride;
        }

        return (await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false)).Location;
    }

    private async Task SetResourceLocationOverrideAsync(string resourceName, string location, CancellationToken cancellationToken)
    {
        var section = await deploymentStateManager.AcquireSectionAsync($"Azure:Deployments:{resourceName}", cancellationToken).ConfigureAwait(false);
        section.Data[LocationOverrideKey] = location;
        await deploymentStateManager.SaveSectionAsync(section, cancellationToken).ConfigureAwait(false);
    }

    private string? TryGetCurrentResourceLocationOverride(AzureBicepResource resource, string? environmentLocation)
    {
        var currentLocationValue = TryGetCurrentResourceLocation(resource);
        if (!string.IsNullOrWhiteSpace(currentLocationValue) &&
            (string.IsNullOrWhiteSpace(environmentLocation) ||
             !string.Equals(currentLocationValue, environmentLocation, StringComparison.OrdinalIgnoreCase)))
        {
            return currentLocationValue;
        }

        if (resource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.Location, out var parameterLocation) &&
            parameterLocation?.ToString() is { Length: > 0 } parameterLocationValue &&
            (string.IsNullOrWhiteSpace(environmentLocation) ||
             !string.Equals(parameterLocationValue, environmentLocation, StringComparison.OrdinalIgnoreCase)))
        {
            return parameterLocationValue;
        }

        return null;
    }

    private string? TryGetCurrentResourceLocation(AzureBicepResource resource)
    {
        if (!notificationService.TryGetCurrentState(resource.Name, out var resourceEvent))
        {
            return null;
        }

        return resourceEvent.Snapshot.Properties
            .FirstOrDefault(static p => string.Equals(p.Name, "azure.location", StringComparison.Ordinal))
            ?.Value?.ToString();
    }

    private string? TryGetPreservedLocationOverride(AzureBicepResource resource, DeploymentStateSection section, string? environmentLocation)
    {
        if (section.Data[LocationOverrideKey]?.GetValue<string>() is { Length: > 0 } locationOverride)
        {
            return locationOverride;
        }

        if (section.Data["Parameters"]?.GetValue<string>() is not { Length: > 0 } parametersJson)
        {
            return null;
        }

        try
        {
            var persistedLocation = JsonNode.Parse(parametersJson)?[AzureBicepResource.KnownParameters.Location]?["value"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(persistedLocation))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(environmentLocation) ||
                !string.Equals(persistedLocation, environmentLocation, StringComparison.OrdinalIgnoreCase))
            {
                return persistedLocation;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to parse persisted parameters while preserving Azure resource location overrides.");
        }

        return TryGetCurrentResourceLocationOverride(resource, environmentLocation);
    }

    private static string NormalizeLocation(string location, IReadOnlyList<KeyValuePair<string, string>> locationOptions)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return location;
        }

        foreach (var option in locationOptions)
        {
            if (string.Equals(option.Key, location, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option.Value, location, StringComparison.OrdinalIgnoreCase))
            {
                return option.Key;
            }
        }

        var canonicalLocation = CanonicalizeLocation(location);
        if (!string.Equals(canonicalLocation, location, StringComparison.Ordinal))
        {
            return canonicalLocation;
        }

        return location;
    }

    private static string CanonicalizeLocation(string location)
    {
        Span<char> buffer = stackalloc char[location.Length];
        var index = 0;

        foreach (var c in location)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[index++] = char.ToLowerInvariant(c);
            }
        }

        return index == 0 ? location : new string(buffer[..index]);
    }

    private async Task<IReadOnlyList<KeyValuePair<string, string>>> GetLocationOptionsAsync(CancellationToken cancellationToken)
    {
        var subscriptionId = (await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false)).SubscriptionId;
        if (!Guid.TryParse(subscriptionId, out _))
        {
            return GetStaticLocationOptions();
        }

        try
        {
            var armClientProvider = serviceProvider.GetRequiredService<IArmClientProvider>();
            var tokenCredentialProvider = serviceProvider.GetRequiredService<ITokenCredentialProvider>();
            var armClient = armClientProvider.GetArmClient(tokenCredentialProvider.TokenCredential);

            return [.. (await armClient.GetAvailableLocationsAsync(subscriptionId, cancellationToken).ConfigureAwait(false))
                .Select(location => KeyValuePair.Create(location.Name, location.DisplayName))];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate Azure locations for resource override.");
            return GetStaticLocationOptions();
        }
    }

    private static IReadOnlyList<KeyValuePair<string, string>> GetStaticLocationOptions()
    {
        return [.. typeof(AzureLocation)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(static p => p.PropertyType == typeof(AzureLocation))
            .Select(static p => (AzureLocation)p.GetValue(null)!)
            .Select(static location => KeyValuePair.Create(location.Name, location.DisplayName ?? location.Name))];
    }

    private async Task<AzureContextState> GetCurrentAzureContextAsync(CancellationToken cancellationToken)
    {
        var section = await deploymentStateManager.AcquireSectionAsync("Azure", cancellationToken).ConfigureAwait(false);

        return new AzureContextState(
            section.Data["SubscriptionId"]?.GetValue<string>() ?? provisionerOptions.Value.SubscriptionId ?? configuration["Azure:SubscriptionId"],
            section.Data["ResourceGroup"]?.GetValue<string>() ?? provisionerOptions.Value.ResourceGroup ?? configuration["Azure:ResourceGroup"],
            section.Data["Location"]?.GetValue<string>() ?? provisionerOptions.Value.Location ?? configuration["Azure:Location"],
            section.Data["TenantId"]?.GetValue<string>() ?? provisionerOptions.Value.TenantId ?? configuration["Azure:TenantId"]);
    }

    private bool ShouldCheckForDrift(IResource resource)
    {
        if (!notificationService.TryGetCurrentState(resource.Name, out var resourceEvent))
        {
            return false;
        }

        return resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running;
    }

    private async Task<string?> TryGetResourceIdFromDeploymentStateAsync(AzureBicepResource resource, CancellationToken cancellationToken)
    {
        var section = await deploymentStateManager.AcquireSectionAsync($"Azure:Deployments:{resource.Name}", cancellationToken).ConfigureAwait(false);
        if (section.Data["Outputs"]?.GetValue<string>() is not { Length: > 0 } outputsJson)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(outputsJson)?["id"]?["value"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to parse cached outputs for resource {ResourceName} while checking for Azure drift.", resource.Name);
            return null;
        }
    }

    private async Task DeleteCachedResourceForLocationChangeAsync(AzureBicepResource resource, string requestedLocation, CancellationToken cancellationToken)
    {
        var currentLocation = TryGetCurrentResourceLocation(resource);
        if (string.IsNullOrWhiteSpace(currentLocation) ||
            string.Equals(currentLocation, requestedLocation, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (await TryGetResourceIdFromDeploymentStateAsync(resource, cancellationToken).ConfigureAwait(false) is not { } resourceId)
        {
            return;
        }

        var context = await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(context.SubscriptionId, out _))
        {
            return;
        }

        var armClientProvider = serviceProvider.GetRequiredService<IArmClientProvider>();
        var tokenCredentialProvider = serviceProvider.GetRequiredService<ITokenCredentialProvider>();
        var armClient = armClientProvider.GetArmClient(tokenCredentialProvider.TokenCredential, context.SubscriptionId);
        if (!await armClient.ResourceExistsAsync(resourceId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _logger.LogInformation(
            "Deleting Azure resource {ResourceId} before reprovisioning {ResourceName} from {CurrentLocation} to {RequestedLocation}.",
            resourceId,
            resource.Name,
            currentLocation,
            requestedLocation);

        await armClient.DeleteResourceAsync(resourceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsMissingCachedResourceAsync(AzureBicepResource resource, CancellationToken cancellationToken)
    {
        if (await TryGetResourceIdFromDeploymentStateAsync(resource, cancellationToken).ConfigureAwait(false) is not { } resourceId)
        {
            return false;
        }

        var context = await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(context.SubscriptionId, out _))
        {
            return false;
        }

        var armClientProvider = serviceProvider.GetRequiredService<IArmClientProvider>();
        var tokenCredentialProvider = serviceProvider.GetRequiredService<ITokenCredentialProvider>();
        var armClient = armClientProvider.GetArmClient(tokenCredentialProvider.TokenCredential, context.SubscriptionId);
        return !await armClient.ResourceExistsAsync(resourceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearCachedDeploymentStateAsync(
        AzureBicepResource resource,
        bool preserveOverrides,
        string? environmentLocation,
        string? currentLocationOverride,
        CancellationToken cancellationToken)
    {
        var section = await deploymentStateManager.AcquireSectionAsync($"Azure:Deployments:{resource.Name}", cancellationToken).ConfigureAwait(false);
        var locationOverride = preserveOverrides
            ? currentLocationOverride ?? TryGetPreservedLocationOverride(resource, section, environmentLocation)
            : null;

        section.Data.Clear();
        if (locationOverride is not null)
        {
            section.Data[LocationOverrideKey] = locationOverride;
            await deploymentStateManager.SaveSectionAsync(section, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await deploymentStateManager.DeleteSectionAsync(section, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshCommandStatesAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var resource in GetResourcesForCommandStateRefresh(model))
        {
            await notificationService.PublishUpdateAsync(resource, static state => state).ConfigureAwait(false);
        }
    }

    private static IEnumerable<IResource> GetResourcesForCommandStateRefresh(DistributedApplicationModel model)
    {
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var resources = new List<IResource>();

        if (model.Resources.OfType<AzureEnvironmentResource>().SingleOrDefault() is { } environmentResource)
        {
            Add(environmentResource);
        }

        foreach (var (resource, azureResource) in GetProvisionableAzureResources(model))
        {
            Add(resource);
            Add(azureResource);
        }

        return resources;

        void Add(IResource resource)
        {
            if (seenNames.Add(resource.Name))
            {
                resources.Add(resource);
            }
        }
    }

    private async Task PublishUpdateToResourceTreeAsync(
        (IResource Resource, IAzureResource AzureResource) resource,
        ILookup<IResource, IResourceWithParent> parentChildLookup,
        Func<CustomResourceSnapshot, CustomResourceSnapshot> stateFactory)
    {
        async Task PublishAsync(IResource targetResource)
        {
            await notificationService.PublishUpdateAsync(targetResource, stateFactory).ConfigureAwait(false);
        }

        await PublishAsync(resource.AzureResource).ConfigureAwait(false);

        if (resource.Resource != resource.AzureResource)
        {
            await PublishAsync(resource.Resource).ConfigureAwait(false);
        }

        var childResources = parentChildLookup[resource.Resource].ToList();

        for (var i = 0; i < childResources.Count; i++)
        {
            var child = childResources[i];

            foreach (var grandChild in parentChildLookup[child])
            {
                if (!childResources.Contains(grandChild))
                {
                    childResources.Add(grandChild);
                }
            }

            await PublishAsync(child).ConfigureAwait(false);
        }
    }

    private async Task AfterProvisionAsync(
        (IResource Resource, IAzureResource AzureResource) resource,
        ILookup<IResource, IResourceWithParent> parentChildLookup)
    {
        try
        {
            await resource.AzureResource.ProvisioningTaskCompletionSource!.Task.ConfigureAwait(false);

            var rolesFailed = await WaitForRoleAssignmentsAsync(resource, parentChildLookup).ConfigureAwait(false);
            if (!rolesFailed)
            {
                await PublishUpdateToResourceTreeAsync(resource, parentChildLookup, state => state with
                {
                    State = new("Running", KnownResourceStateStyles.Success)
                }).ConfigureAwait(false);
            }
        }
        catch (MissingConfigurationException)
        {
            await PublishUpdateToResourceTreeAsync(resource, parentChildLookup, state => state with
            {
                State = new("Missing subscription configuration", KnownResourceStateStyles.Error)
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await PublishUpdateToResourceTreeAsync(resource, parentChildLookup, state => state with
            {
                State = new("Failed to Provision", KnownResourceStateStyles.Error)
            }).ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitForRoleAssignmentsAsync(
        (IResource Resource, IAzureResource AzureResource) resource,
        ILookup<IResource, IResourceWithParent> parentChildLookup)
    {
        var rolesFailed = false;
        if (resource.AzureResource.TryGetAnnotationsOfType<RoleAssignmentResourceAnnotation>(out var roleAssignments))
        {
            try
            {
                foreach (var roleAssignment in roleAssignments)
                {
                    await roleAssignment.RolesResource.ProvisioningTaskCompletionSource!.Task.ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                rolesFailed = true;
                await PublishUpdateToResourceTreeAsync(resource, parentChildLookup, state => state with
                {
                    State = new("Failed to Provision Roles", KnownResourceStateStyles.Error)
                }).ConfigureAwait(false);
            }
        }

        return rolesFailed;
    }

    private async Task ProvisionAzureResourcesAsync(
        IReadOnlyList<(IResource Resource, IAzureResource AzureResource)> azureResources,
        ILookup<IResource, IResourceWithParent> parentChildLookup,
        CancellationToken cancellationToken)
    {
        // Share one provisioning context across the batch, but let each resource complete its own provisioning TCS so
        // dependent resources can continue as soon as their prerequisites are ready.
        var provisioningContextLazy = new Lazy<Task<ProvisioningContext>>(() => provisioningContextProvider.CreateProvisioningContextAsync(cancellationToken));
        var tasks = new List<Task>(azureResources.Count);

        foreach (var resource in azureResources)
        {
            tasks.Add(ProcessResourceAsync(provisioningContextLazy, resource, parentChildLookup, cancellationToken));
        }

        var task = Task.WhenAll(tasks);
        await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task ProcessResourceAsync(
        Lazy<Task<ProvisioningContext>> provisioningContextLazy,
        (IResource Resource, IAzureResource AzureResource) resource,
        ILookup<IResource, IResourceWithParent> parentChildLookup,
        CancellationToken cancellationToken)
    {
        // This method owns the lifecycle for a single Azure resource within a batch. It is also responsible for
        // completing the per-resource TCS that dependency waits observe.
        var beforeResourceStartedEvent = new BeforeResourceStartedEvent(resource.Resource, serviceProvider);
        await eventing.PublishAsync(beforeResourceStartedEvent, cancellationToken).ConfigureAwait(false);

        var resourceLogger = loggerService.GetLogger(resource.AzureResource);

        if (resource.AzureResource is not AzureBicepResource bicepResource)
        {
            CompleteProvisioning(resource.AzureResource);
            resourceLogger.LogInformation("Skipping {resourceName} because it is not a Bicep resource.", resource.AzureResource.Name);
            return;
        }

        if (bicepResource.IsContainer() || bicepResource.IsEmulator())
        {
            CompleteProvisioning(resource.AzureResource);
            resourceLogger.LogInformation("Skipping {resourceName} because it is not configured to be provisioned.", resource.AzureResource.Name);
        }
        else
        {
            await WaitForProvisioningDependenciesAsync(bicepResource, cancellationToken).ConfigureAwait(false);
            if (await IsMissingCachedResourceAsync(bicepResource, cancellationToken).ConfigureAwait(false))
            {
                resourceLogger.LogWarning("Cached Azure deployment state for {resourceName} points to a missing Azure resource. Reprovisioning.", resource.AzureResource.Name);
                await ClearCachedDeploymentStateAsync(
                    bicepResource,
                    preserveOverrides: true,
                    environmentLocation: (await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false)).Location,
                    currentLocationOverride: null,
                    cancellationToken).ConfigureAwait(false);
            }

            if (await bicepProvisioner.ConfigureResourceAsync(configuration, bicepResource, cancellationToken).ConfigureAwait(false))
            {
                CompleteProvisioning(resource.AzureResource);
                resourceLogger.LogInformation("Using connection information stored in user secrets for {resourceName}.", resource.AzureResource.Name);
                await PublishConnectionStringAvailableEventAsync(resource.Resource, parentChildLookup, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (resource.AzureResource.IsExisting())
                {
                    resourceLogger.LogInformation("Resolving {resourceName} as existing resource...", resource.AzureResource.Name);
                }
                else
                {
                    resourceLogger.LogInformation("Provisioning {resourceName}...", resource.AzureResource.Name);
                }

                try
                {
                    var provisioningContext = await provisioningContextLazy.Value.ConfigureAwait(false);

                    await bicepProvisioner.GetOrCreateResourceAsync(
                        bicepResource,
                        provisioningContext,
                        cancellationToken).ConfigureAwait(false);

                    CompleteProvisioning(resource.AzureResource);
                    await PublishConnectionStringAvailableEventAsync(resource.Resource, parentChildLookup, cancellationToken).ConfigureAwait(false);
                }
                catch (AzureCliNotOnPathException ex)
                {
                    resourceLogger.LogCritical("Using Azure resources during local development requires the installation of the Azure CLI. See https://aka.ms/dotnet/aspire/azcli for instructions.");
                    FailProvisioning(resource.AzureResource, ex);
                }
                catch (MissingConfigurationException ex)
                {
                    resourceLogger.LogCritical("Resource could not be provisioned because Azure subscription, location, and resource group information is missing. See https://aka.ms/dotnet/aspire/azure/provisioning for more details.");
                    FailProvisioning(resource.AzureResource, ex);
                }
                catch (Exception ex)
                {
                    resourceLogger.LogError(ex, "Error provisioning {ResourceName}.", resource.AzureResource.Name);
                    FailProvisioning(resource.AzureResource, new InvalidOperationException($"Unable to resolve references from {resource.AzureResource.Name}", ex));
                }
            }
        }
    }

    private static void CompleteProvisioning(IAzureResource resource)
    {
        resource.ProvisioningTaskCompletionSource?.TrySetResult();
    }

    private static void FailProvisioning(IAzureResource resource, Exception exception)
    {
        resource.ProvisioningTaskCompletionSource?.TrySetException(exception);
    }

    private static async Task WaitForProvisioningDependenciesAsync(AzureBicepResource resource, CancellationToken cancellationToken)
    {
        _ = resource.GetBicepTemplateString();

        var dependencies = new HashSet<IAzureResource>();

        foreach (var parameter in resource.Parameters.Values)
        {
            CollectProvisioningDependencies(dependencies, parameter);
        }

        foreach (var reference in resource.References)
        {
            CollectProvisioningDependencies(dependencies, reference);
        }

        foreach (var dependency in dependencies)
        {
            if (ReferenceEquals(dependency, resource))
            {
                continue;
            }

            if (dependency.ProvisioningTaskCompletionSource is { } provisioning)
            {
                await provisioning.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void CollectProvisioningDependencies(HashSet<IAzureResource> dependencies, object? value)
    {
        CollectProvisioningDependencies(dependencies, value, []);
    }

    private static void CollectProvisioningDependencies(HashSet<IAzureResource> dependencies, object? value, HashSet<object> visited)
    {
        if (value is null || !visited.Add(value))
        {
            return;
        }

        if (value is IAzureResource azureResource)
        {
            dependencies.Add(azureResource);
        }

        if (value is IValueWithReferences valueWithReferences)
        {
            foreach (var reference in valueWithReferences.References)
            {
                CollectProvisioningDependencies(dependencies, reference, visited);
            }
        }
    }

    private async Task PublishConnectionStringAvailableEventAsync(
        IResource targetResource,
        ILookup<IResource, IResourceWithParent> parentChildLookup,
        CancellationToken cancellationToken)
    {
        if (targetResource is IResourceWithConnectionString)
        {
            var connectionStringAvailableEvent = new ConnectionStringAvailableEvent(targetResource, serviceProvider);
            await eventing.PublishAsync(connectionStringAvailableEvent, cancellationToken).ConfigureAwait(false);
        }

        if (parentChildLookup[targetResource] is { } children)
        {
            foreach (var child in children.OfType<IResourceWithConnectionString>().Where(static c => c is IResourceWithParent))
            {
                await PublishConnectionStringAvailableEventAsync(child, parentChildLookup, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static ImmutableArray<ResourcePropertySnapshot> FilterProperties(ImmutableArray<ResourcePropertySnapshot> properties)
    {
        if (properties.IsDefaultOrEmpty)
        {
            return [];
        }

        return [.. properties.Where(static property => !s_resettableProperties.Contains(property.Name, StringComparer.Ordinal))];
    }

    private async Task PublishAzureEnvironmentStateAsync(
        DistributedApplicationModel model,
        string state,
        CancellationToken cancellationToken)
    {
        await PublishAzureEnvironmentStateAsync(
            model,
            new ResourceStateSnapshot(state, state == KnownResourceStates.NotStarted ? KnownResourceStateStyles.Info : KnownResourceStateStyles.Success),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishAzureEnvironmentStateAsync(
        DistributedApplicationModel model,
        ResourceStateSnapshot state,
        CancellationToken _)
    {
        if (model.Resources.OfType<AzureEnvironmentResource>().SingleOrDefault() is not { } azureEnvironmentResource)
        {
            return;
        }

        await notificationService.PublishUpdateAsync(azureEnvironmentResource, existingState => existingState with
        {
            State = state,
            Properties = state.Text == KnownResourceStates.NotStarted
                ? FilterProperties(existingState.Properties)
                : FilterProperties(existingState.Properties).SetResourcePropertyRange(BuildAzureEnvironmentProperties(ReadControllerState().Spec)),
            Urls = state.Text == KnownResourceStates.NotStarted ? [] : existingState.Urls,
            CreationTimeStamp = state.Text == KnownResourceStates.NotStarted ? null : existingState.CreationTimeStamp,
            StartTimeStamp = state.Text == KnownResourceStates.NotStarted ? null : existingState.StartTimeStamp,
            StopTimeStamp = state.Text == KnownResourceStates.NotStarted ? null : existingState.StopTimeStamp
        }).ConfigureAwait(false);

        if (state.Text == KnownResourceStates.NotStarted)
        {
            loggerService.GetLogger(azureEnvironmentResource).LogInformation("Azure provisioning state has been reset.");
        }
    }

    private AzureControllerState ReadControllerState()
    {
        lock (_operationStateLock)
        {
            return _state;
        }
    }

    private static ImmutableArray<ResourcePropertySnapshot> BuildAzureEnvironmentProperties(AzureControllerSpec spec)
    {
        var properties = ImmutableArray<ResourcePropertySnapshot>.Empty;

        if (!string.IsNullOrEmpty(spec.SubscriptionId))
        {
            properties = properties.SetResourceProperty("azure.subscription.id", spec.SubscriptionId);
        }

        if (!string.IsNullOrEmpty(spec.ResourceGroup))
        {
            properties = properties.SetResourceProperty("azure.resource.group", spec.ResourceGroup);
        }

        if (!string.IsNullOrEmpty(spec.Location))
        {
            properties = properties.SetResourceProperty("azure.location", spec.Location);
        }

        if (!string.IsNullOrEmpty(spec.TenantId))
        {
            properties = properties.SetResourceProperty("azure.tenant.id", spec.TenantId);
        }

        return properties;
    }

    private sealed class AzureOperationState(string displayName, bool isAllResources, IReadOnlySet<string> resourceNames)
    {
        public string DisplayName { get; } = displayName;
        public bool IsAllResources { get; } = isAllResources;
        public IReadOnlySet<string> ResourceNames { get; } = resourceNames;

        public static AzureOperationState None { get; } = new(string.Empty, false, new HashSet<string>(StringComparer.Ordinal));

        public static AzureOperationState All(string displayName) => new(displayName, true, new HashSet<string>(StringComparer.Ordinal));

        public static AzureOperationState Resource(string resourceName, string displayName) => new(displayName, false, new HashSet<string>([resourceName], StringComparer.Ordinal));
    }

    private sealed record AzureControllerState(AzureControllerSpec Spec, AzureControllerStatus Status)
    {
        public static AzureControllerState Empty { get; } = new(new(null, null, null, null), new(null));
    }

    private sealed record AzureControllerSpec(string? SubscriptionId, string? ResourceGroup, string? Location, string? TenantId);

    private sealed record AzureControllerStatus(AzureIntent? CurrentIntent);

    internal enum AzureEnvironmentCommand
    {
        ResetProvisioningState,
        ChangeAzureContext,
        ReprovisionAll,
        DeleteAzureResources
    }

    internal enum AzureResourceCommand
    {
        ChangeLocation,
        ForgetState,
        Reprovision
    }

    internal sealed record EnvironmentCommandDefinition(
        AzureEnvironmentCommand Command,
        string Name,
        string DisplayName,
        string Description,
        string ConfirmationMessage,
        string IconName,
        IconVariant IconVariant,
        bool IsHighlighted);

    internal sealed record ResourceCommandDefinition(
        AzureResourceCommand Command,
        string Name,
        string DisplayName,
        string Description,
        string? ConfirmationMessage,
        string IconName,
        IconVariant IconVariant,
        bool IsHighlighted);

    private abstract record AzureIntent(AzureOperationState Operation);

    private sealed record ResetStateIntent() : AzureIntent(AzureOperationState.All("Reset provisioning state"));

    private sealed record ChangeAzureContextIntent() : AzureIntent(AzureOperationState.All("Change Azure context"));

    private sealed record EnsureProvisionedIntent() : AzureIntent(AzureOperationState.All("Provision Azure resources"));

    private sealed record ReprovisionAllIntent() : AzureIntent(AzureOperationState.All("Reprovision all Azure resources"));

    private sealed record DeleteAzureResourcesIntent() : AzureIntent(AzureOperationState.All("Delete Azure resources"));

    private sealed record ForgetResourceStateIntent(string ResourceName) : AzureIntent(AzureOperationState.Resource(ResourceName, "Reset provisioning state"));

    private sealed record ChangeResourceLocationIntent(string ResourceName, string Location) : AzureIntent(AzureOperationState.Resource(ResourceName, "Change Azure resource location"));

    private sealed record ReprovisionResourceIntent(string ResourceName) : AzureIntent(AzureOperationState.Resource(ResourceName, "Reprovision Azure resource"));

    private sealed record DetectDriftIntent() : AzureIntent(AzureOperationState.None);

    private sealed record QueuedOperation(
        DistributedApplicationModel Model,
        AzureIntent Intent,
        TaskCompletionSource<object?> Completion,
        CancellationToken CancellationToken);

    private sealed record AzureContextState(string? SubscriptionId, string? ResourceGroup, string? Location, string? TenantId);
}
