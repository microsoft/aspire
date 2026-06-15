// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
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
using Azure.Identity;
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
/// starting their own deployment. This TCS is completed by CompleteProvisioning, FailProvisioning, or
/// CancelProvisioning — the only three completion paths — so dependent resources unblock with the prerequisite's
/// result as soon as it finishes, not when the entire batch completes.
/// </para>
/// <para>
/// The controller tracks lightweight in-memory state (AzureControllerState) under a lock. This state drives
/// command enablement in the dashboard (commands are disabled while an operation targeting the same resources
/// is running). Azure identity properties shown on the AzureEnvironmentResource are read from persisted
/// context when the environment state is published.
/// </para>
/// <para>
/// Location overrides let a user deploy a single resource to a different Azure region. Overrides are persisted
/// in the deployment state store and survive resets/reprovisioning. When a location change is requested, the
/// controller deletes the existing Azure resource first (to avoid ARM InvalidResourceLocation conflicts), sets
/// the override, and reprovisions.
/// </para>
/// <para>
/// Drift detection runs periodically. It probes ARM to verify each running resource still exists and
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
    internal const string GetAzureResourceCommandName = "get-azure-resource";
    internal const string CancelDeploymentCommandName = "cancel-deployment";
    internal const string DeleteAzureResourceCommandName = "delete-azure-resource";
    internal const string ReprovisionResourceCommandName = "reprovision";
    internal const string ResetProvisioningStateCommandName = "reset-provisioning-state";
    internal const string ChangeAzureContextCommandName = "change-azure-context";
    internal const string ReprovisionAllCommandName = "reprovision-all";
    internal const string DeleteAzureResourcesCommandName = "delete-azure-resources";
    internal const string LocationOverrideKey = "LocationOverride";
    internal const string MissingInAzureState = "Missing in Azure";
    internal const string DriftedState = "Drifted";
    internal const string CreatingArmDeploymentState = "Creating ARM Deployment";
    internal const string WaitingForDeploymentState = "Waiting for Deployment";
    private const string SubscriptionIdArgumentName = "subscriptionId";
    private const string ResourceGroupArgumentName = "resourceGroup";
    private const string LocationArgumentName = "location";
    private const string TenantIdArgumentName = "tenantId";

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
    private int _azureContextNotificationStarted;
    private bool _driftCheckQueued;

    // Drift checks are intentionally periodic and non-overlapping. The monitor queues at most one check at a time so
    // command execution and background drift probing share the same serialized control loop.
    internal TimeSpan DriftCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    // The dashboard uses declaration order when rendering commands. Highlighted commands are promoted ahead of
    // the remaining command list, so keep each definition list ordered by the desired fallback display order.
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
            IsHighlighted: true,
            ExecuteCommand: static (controller, context) => controller.ExecuteResetProvisioningStateCommandAsync(context)),
        new(
            AzureEnvironmentCommand.ChangeAzureContext,
            ChangeAzureContextCommandName,
            AzureProvisioningStrings.ChangeAzureContextCommandName,
            AzureProvisioningStrings.ChangeAzureContextCommandDescription,
            AzureProvisioningStrings.ChangeAzureContextCommandConfirmation,
            "Edit",
            IconVariant.Regular,
            IsHighlighted: true,
            ExecuteCommand: static (controller, context) => controller.ExecuteChangeAzureContextCommandAsync(context),
            Arguments: CreateAzureContextCommandArguments(),
            ValidateArguments: ValidateAzureContextCommandArguments),
        new(
            AzureEnvironmentCommand.ReprovisionAll,
            ReprovisionAllCommandName,
            AzureProvisioningStrings.ReprovisionAllCommandName,
            AzureProvisioningStrings.ReprovisionAllCommandDescription,
            AzureProvisioningStrings.ReprovisionAllCommandConfirmation,
            "ArrowSync",
            IconVariant.Regular,
            IsHighlighted: false,
            ExecuteCommand: static (controller, context) => controller.ExecuteReprovisionAllCommandAsync(context)),
        new(
            AzureEnvironmentCommand.DeleteAzureResources,
            DeleteAzureResourcesCommandName,
            AzureProvisioningStrings.DeleteAzureResourcesCommandName,
            AzureProvisioningStrings.DeleteAzureResourcesCommandDescription,
            AzureProvisioningStrings.DeleteAzureResourcesCommandConfirmation,
            "Delete",
            IconVariant.Regular,
            IsHighlighted: false,
            ExecuteCommand: static (controller, context) => controller.ExecuteDeleteAzureResourcesCommandAsync(context))
    ];

    // Keep this in the desired dashboard command order; see EnvironmentCommandDefinitions for ordering rules.
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
            IsHighlighted: false,
            ExecuteCommand: static (controller, resourceName, context) => controller.ExecuteChangeResourceLocationCommandAsync(resourceName, context),
            Arguments: CreateChangeLocationCommandArguments(deploymentStateResourceName: null)),
        new(
            AzureResourceCommand.GetAzureResource,
            GetAzureResourceCommandName,
            AzureProvisioningStrings.GetAzureResourceCommandName,
            AzureProvisioningStrings.GetAzureResourceCommandDescription,
            ConfirmationMessage: null,
            "Info",
            IconVariant.Regular,
            IsHighlighted: false,
            ExecuteCommand: static (controller, resourceName, context) => controller.ExecuteGetAzureResourceCommandAsync(resourceName, context)),
        new(
            AzureResourceCommand.CancelDeployment,
            CancelDeploymentCommandName,
            AzureProvisioningStrings.CancelDeploymentCommandName,
            AzureProvisioningStrings.CancelDeploymentCommandDescription,
            AzureProvisioningStrings.CancelDeploymentCommandConfirmation,
            "Stop",
            IconVariant.Regular,
            IsHighlighted: false,
            ExecuteCommand: static (controller, resourceName, context) => controller.ExecuteCancelResourceDeploymentCommandAsync(resourceName, context)),
        new(
            AzureResourceCommand.DeleteAzureResource,
            DeleteAzureResourceCommandName,
            AzureProvisioningStrings.DeleteAzureResourceCommandName,
            AzureProvisioningStrings.DeleteAzureResourceCommandDescription,
            AzureProvisioningStrings.DeleteAzureResourceCommandConfirmation,
            "Delete",
            IconVariant.Regular,
            IsHighlighted: false,
            ExecuteCommand: static (controller, resourceName, context) => controller.ExecuteDeleteAzureResourceCommandAsync(resourceName, context)),
        new(
            AzureResourceCommand.ForgetState,
            ForgetStateCommandName,
            AzureProvisioningStrings.ForgetStateCommandName,
            AzureProvisioningStrings.ForgetStateCommandDescription,
            AzureProvisioningStrings.ForgetStateCommandConfirmation,
            "ArrowReset",
            IconVariant.Regular,
            IsHighlighted: false,
            ExecuteCommand: static (controller, resourceName, context) => controller.ExecuteForgetStateCommandAsync(resourceName, context)),
        new(
            AzureResourceCommand.Reprovision,
            ReprovisionResourceCommandName,
            AzureProvisioningStrings.ReprovisionResourceCommandName,
            AzureProvisioningStrings.ReprovisionResourceCommandDescription,
            AzureProvisioningStrings.ReprovisionResourceCommandConfirmation,
            "ArrowSync",
            IconVariant.Regular,
            IsHighlighted: true,
            ExecuteCommand: static (controller, resourceName, context) => controller.ExecuteReprovisionResourceCommandAsync(resourceName, context))
    ];

    // The Change Azure context dialog is a cascade:
    //
    //   Tenant ID -> Subscription ID -> Resource group -> Location
    //
    // The dashboard sends update requests when a user edits a source input, and the
    // hosting interaction service then reloads any inputs that list that source in
    // DependsOnInputs. That dependency mechanism does not fire for values populated by
    // startup loads, configuration, or the current Azure context. Any downstream input
    // that must be usable immediately for an already-known upstream value must also opt
    // into AlwaysLoadOnStart.
    private static IReadOnlyList<InteractionInput> CreateAzureContextCommandArguments() =>
    [
        new()
        {
            Name = TenantIdArgumentName,
            Label = AzureProvisioningStrings.TenantLabel,
            Placeholder = AzureProvisioningStrings.TenantPlaceholder,
            InputType = InputType.Choice,
            Required = true,
            AllowCustomChoice = true,
            // The dashboard opens the dialog before dynamic options finish loading. Keep this
            // server-owned until the startup load seeds the current tenant value; otherwise the
            // client treats the empty local value as user-owned and ignores the loaded selection.
            Disabled = true,
            DynamicLoading = new InputLoadOptions
            {
                // Tenant ID has no dependencies, so startup loading is the only opportunity to
                // populate the current tenant and enumerate available tenants before the user edits
                // anything in the dialog.
                AlwaysLoadOnStart = true,
                LoadCallback = LoadTenantArgumentOptionsAsync
            }
        },
        new()
        {
            Name = SubscriptionIdArgumentName,
            Label = AzureProvisioningStrings.SubscriptionIdLabel,
            Placeholder = AzureProvisioningStrings.SubscriptionIdPlaceholder,
            InputType = InputType.Choice,
            Required = true,
            AllowCustomChoice = true,
            // Keep the subscription control unavailable until the load callback resolves the
            // selected tenant. This avoids accepting a stale subscription value from a previous
            // tenant while the dashboard is still waiting for tenant-scoped subscription options.
            Disabled = true,
            DynamicLoading = new InputLoadOptions
            {
                // Startup loading handles a tenant preselected from configuration/current context,
                // not from a user edit, and then re-enables this input.
                AlwaysLoadOnStart = true,
                LoadCallback = LoadSubscriptionArgumentOptionsAsync,
                DependsOnInputs = [TenantIdArgumentName]
            }
        },
        new()
        {
            Name = ResourceGroupArgumentName,
            Label = AzureProvisioningStrings.ResourceGroupLabel,
            Placeholder = AzureProvisioningStrings.ResourceGroupPlaceholder,
            InputType = InputType.Choice,
            Required = true,
            AllowCustomChoice = true,
            Disabled = true,
            DynamicLoading = new InputLoadOptions
            {
                // Startup loading handles a subscription preselected from configuration/current
                // context; dependency updates handle later Subscription ID edits.
                AlwaysLoadOnStart = true,
                LoadCallback = LoadResourceGroupArgumentOptionsAsync,
                DependsOnInputs = [SubscriptionIdArgumentName]
            }
        },
        new()
        {
            Name = LocationArgumentName,
            Label = AzureProvisioningStrings.LocationLabel,
            Placeholder = AzureProvisioningStrings.LocationPlaceholder,
            InputType = InputType.Choice,
            Required = true,
            AllowCustomChoice = true,
            Disabled = true,
            DynamicLoading = new InputLoadOptions
            {
                // Startup loading reflects a known resource-group location immediately; dependency
                // updates handle later edits to either upstream input.
                AlwaysLoadOnStart = true,
                LoadCallback = context => LoadLocationArgumentOptionsAsync(context),
                DependsOnInputs = [SubscriptionIdArgumentName, ResourceGroupArgumentName]
            }
        }
    ];

    internal static IReadOnlyList<InteractionInput> CreateChangeLocationCommandArguments(string? deploymentStateResourceName) =>
    [
        new()
        {
            Name = LocationArgumentName,
            Label = AzureProvisioningStrings.LocationLabel,
            Placeholder = AzureProvisioningStrings.LocationPlaceholder,
            InputType = InputType.Choice,
            Required = true,
            AllowCustomChoice = true,
            Disabled = true,
            DynamicLoading = new InputLoadOptions
            {
                AlwaysLoadOnStart = true,
                LoadCallback = context => LoadLocationArgumentOptionsAsync(context, deploymentStateResourceName)
            }
        }
    ];

    private static async Task LoadTenantArgumentOptionsAsync(LoadInputContext context)
    {
        try
        {
            var controller = context.Services.GetRequiredService<AzureProvisioningController>();
            var currentContext = await controller.GetCurrentAzureContextAsync(context.CancellationToken).ConfigureAwait(false);

            // Preserve a value the user has already typed or selected. Dynamic loading can run again
            // after dashboard updates, and replacing a non-empty value here would undo user intent.
            if (string.IsNullOrEmpty(context.Input.Value))
            {
                context.Input.Value = currentContext.TenantId;
            }

            var tenantOptions = await controller.GetTenantOptionsAsync(context.CancellationToken).ConfigureAwait(false);
            if (tenantOptions.Count > 0)
            {
                context.Input.Options = tenantOptions;
            }
        }
        finally
        {
            // Re-enable even if context or tenant enumeration fails. AllowCustomChoice lets users
            // paste a tenant ID manually and then retry the command.
            context.Input.Disabled = false;
        }
    }

    private static async Task LoadSubscriptionArgumentOptionsAsync(LoadInputContext context)
    {
        try
        {
            var controller = context.Services.GetRequiredService<AzureProvisioningController>();
            var currentContext = await controller.GetCurrentAzureContextAsync(context.CancellationToken).ConfigureAwait(false);
            var tenantId = context.AllInputs.TryGetByName(TenantIdArgumentName, out var tenantInput) && !string.IsNullOrWhiteSpace(tenantInput.Value)
                ? tenantInput.Value
                : currentContext.TenantId;

            // Seed with the current subscription so the common case is preselected. If the user picked
            // a different tenant and this subscription is not in the loaded option list, the interaction
            // loading pipeline clears the invalid value for non-custom choices. This input allows custom
            // choices, so users can still paste a subscription that enumeration did not return.
            if (string.IsNullOrEmpty(context.Input.Value))
            {
                context.Input.Value = currentContext.SubscriptionId;
            }

            var subscriptionOptions = await controller.GetSubscriptionOptionsAsync(tenantId, context.CancellationToken).ConfigureAwait(false);
            if (subscriptionOptions.Count > 0)
            {
                context.Input.Options = subscriptionOptions;
            }
        }
        finally
        {
            // The control starts disabled because it is tenant-scoped. Re-enable it even if context
            // or enumeration fails; AllowCustomChoice lets the user enter a subscription ID manually.
            context.Input.Disabled = false;
        }
    }

    private static async Task LoadResourceGroupArgumentOptionsAsync(LoadInputContext context)
    {
        try
        {
            var controller = context.Services.GetRequiredService<AzureProvisioningController>();
            var currentContext = await controller.GetCurrentAzureContextAsync(context.CancellationToken).ConfigureAwait(false);
            var subscriptionId = context.AllInputs.TryGetByName(SubscriptionIdArgumentName, out var subscriptionInput) && !string.IsNullOrWhiteSpace(subscriptionInput.Value)
                ? subscriptionInput.Value
                : currentContext.SubscriptionId;

            // Keep the persisted/current resource group as the default unless the user has already
            // provided a value in the open dialog.
            if (string.IsNullOrEmpty(context.Input.Value))
            {
                context.Input.Value = currentContext.ResourceGroup;
            }

            var resourceGroupOptions = await controller.GetResourceGroupOptionsAsync(subscriptionId, context.CancellationToken).ConfigureAwait(false);
            context.Input.Options = resourceGroupOptions.Select(static rg => KeyValuePair.Create(rg.Name, rg.Name)).ToList();
        }
        finally
        {
            // A user can create or target a resource group that is not returned by enumeration, so
            // context or enumeration failures should still leave the input editable.
            context.Input.Disabled = false;
        }
    }

    private static async Task LoadLocationArgumentOptionsAsync(LoadInputContext context, string? deploymentStateResourceName = null)
    {
        var locationPinnedToResourceGroup = false;
        try
        {
            var controller = context.Services.GetRequiredService<AzureProvisioningController>();
            var currentContext = await controller.GetCurrentAzureContextAsync(context.CancellationToken).ConfigureAwait(false);
            var subscriptionId = context.AllInputs.TryGetByName(SubscriptionIdArgumentName, out var subscriptionInput) && !string.IsNullOrWhiteSpace(subscriptionInput.Value)
                ? subscriptionInput.Value
                : currentContext.SubscriptionId;
            var resourceGroupName = context.AllInputs.TryGetByName(ResourceGroupArgumentName, out var resourceGroupInput) && !string.IsNullOrWhiteSpace(resourceGroupInput.Value)
                ? resourceGroupInput.Value
                : null;

            if (!string.IsNullOrWhiteSpace(resourceGroupName))
            {
                var resourceGroupOptions = await controller.GetResourceGroupOptionsAsync(subscriptionId, context.CancellationToken).ConfigureAwait(false);
                var (_, resourceGroupLocation) = resourceGroupOptions.FirstOrDefault(rg => rg.Name.Equals(resourceGroupName, StringComparisons.AzureResourceGroupName));
                if (!string.IsNullOrEmpty(resourceGroupLocation))
                {
                    // Existing resource groups are pinned to a single Azure region. When the selected
                    // group is known, constrain the location input to that region instead of letting the
                    // user pick a value that ARM will reject for deployments into that group.
                    context.Input.Options = [KeyValuePair.Create(resourceGroupLocation, resourceGroupLocation)];
                    context.Input.Value = resourceGroupLocation;
                    locationPinnedToResourceGroup = true;
                    context.Input.Disabled = true;
                    return;
                }
            }

            // For Change resource location, deploymentStateResourceName identifies the specific resource
            // whose effective location should be shown. For Change Azure context, there is no per-resource
            // target, so the current environment location is the fallback.
            if (string.IsNullOrEmpty(context.Input.Value))
            {
                context.Input.Value = !string.IsNullOrEmpty(deploymentStateResourceName)
                    ? await controller.GetEffectiveResourceLocationAsync(deploymentStateResourceName, context.CancellationToken).ConfigureAwait(false)
                    : currentContext.Location;
            }

            context.Input.Options = await controller.GetLocationOptionsAsync(subscriptionId, context.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!locationPinnedToResourceGroup)
            {
                // The input is disabled only when an existing resource group determines the location.
                // Otherwise the user must be able to choose or type a location even if loading fails.
                context.Input.Disabled = false;
            }
        }
    }

    private static Task ValidateAzureContextCommandArguments(InputsDialogValidationContext validationContext)
    {
        ValidateGuidArgument(validationContext, SubscriptionIdArgumentName, AzureProvisioningStrings.ValidationSubscriptionIdInvalid);
        ValidateGuidArgument(validationContext, TenantIdArgumentName, AzureProvisioningStrings.ValidationTenantIdInvalid);

        var resourceGroupInput = validationContext.Inputs[ResourceGroupArgumentName];
        if (!BaseProvisioningContextProvider.IsValidResourceGroupName(resourceGroupInput.Value))
        {
            validationContext.AddValidationError(resourceGroupInput, AzureProvisioningStrings.ValidationResourceGroupNameInvalid);
        }

        return Task.CompletedTask;
    }

    private static void ValidateGuidArgument(InputsDialogValidationContext validationContext, string inputName, string validationMessage)
    {
        var input = validationContext.Inputs[inputName];
        if (!string.IsNullOrWhiteSpace(input.Value) && !Guid.TryParse(input.Value, out _))
        {
            validationContext.AddValidationError(input, validationMessage);
        }
    }

    public async Task ResetStateAsync(DistributedApplicationModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        await RunOperationAsync(model, new ResetStateIntent(ReprovisionAfterReset: false), cancellationToken).ConfigureAwait(false);
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

        return await RunOperationAsync<bool>(model, new ChangeAzureContextIntent(Options: null), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ChangeAzureContextAsync(DistributedApplicationModel model, AzureProvisioningOptionsUpdate options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(options);

        return await RunOperationAsync<bool>(model, new ChangeAzureContextIntent(options), cancellationToken).ConfigureAwait(false);
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
            // Drift intents use AzureOperationState.None so they never set CurrentIntent; this flag
            // dedupes queued/running drift checks without disabling dashboard commands.
            if (_state.Status.CurrentIntent is not null || _driftCheckQueued)
            {
                return;
            }

            _driftCheckQueued = true;
        }

        try
        {
            await QueueAndWaitForOperationAsync(model, new DetectDriftIntent(), cancellationToken).ConfigureAwait(false);
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

    public async Task CancelResourceDeploymentAsync(DistributedApplicationModel model, string resourceName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        await RunOperationAsync(model, new CancelResourceDeploymentIntent(resourceName), cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAzureResourceAsync(DistributedApplicationModel model, string resourceName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(resourceName);

        await RunOperationAsync(model, new DeleteAzureResourceIntent(resourceName), cancellationToken).ConfigureAwait(false);
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

        var targetResources = GetTargetAzureResources(model, resourceName);
        var currentLocation = await GetEffectiveResourceLocationAsync(GetDeploymentStateResourceName(targetResources[0]), cancellationToken).ConfigureAwait(false);
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

        return await ChangeResourceLocationAsync(model, resourceName, location, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ChangeResourceLocationAsync(DistributedApplicationModel model, string resourceName, string location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);

        location = NormalizeLocation(location, await GetLocationOptionsAsync(cancellationToken).ConfigureAwait(false));

        return await RunOperationAsync<bool>(model, new ChangeResourceLocationIntent(resourceName, location), cancellationToken).ConfigureAwait(false);
    }

    private Task<ExecuteCommandResult> ExecuteResetProvisioningStateCommandAsync(ExecuteCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var model = context.Services.GetRequiredService<DistributedApplicationModel>();

        return ExecuteCommandAsync(
            () => RunOperationAsync<bool>(model, new ResetStateIntent(ReprovisionAfterReset: true), context.CancellationToken),
            AzureProvisioningStrings.ResetProvisioningStateCommandSuccess,
            () => CreateEnvironmentCommandResultDataAsync(ResetProvisioningStateCommandName, model, context.CancellationToken),
            AzureProvisioningFailureDetails.ProvisionOperation);
    }

    private Task<ExecuteCommandResult> ExecuteChangeAzureContextCommandAsync(ExecuteCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var model = context.Services.GetRequiredService<DistributedApplicationModel>();

        return ExecuteCommandAsync(
            () => ChangeAzureContextCommandAsync(model, context.Arguments, context.CancellationToken),
            AzureProvisioningStrings.ChangeAzureContextCommandSuccess,
            () => CreateEnvironmentCommandResultDataAsync(ChangeAzureContextCommandName, model, context.CancellationToken),
            AzureProvisioningFailureDetails.ProvisionOperation);
    }

    private Task<ExecuteCommandResult> ExecuteReprovisionAllCommandAsync(ExecuteCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var model = context.Services.GetRequiredService<DistributedApplicationModel>();

        return ExecuteCommandAsync(
            () => ReprovisionAllAsync(model, context.CancellationToken),
            AzureProvisioningStrings.ReprovisionAllCommandSuccess,
            () => CreateEnvironmentCommandResultDataAsync(ReprovisionAllCommandName, model, context.CancellationToken),
            AzureProvisioningFailureDetails.ProvisionOperation);
    }

    private Task<ExecuteCommandResult> ExecuteDeleteAzureResourcesCommandAsync(ExecuteCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var model = context.Services.GetRequiredService<DistributedApplicationModel>();

        return ExecuteCommandAsync(
            () => DeleteAzureResourcesAsync(model, context.CancellationToken),
            AzureProvisioningStrings.DeleteAzureResourcesCommandSuccess,
            () => CreateEnvironmentCommandResultDataAsync(DeleteAzureResourcesCommandName, model, context.CancellationToken));
    }

    private Task<ExecuteCommandResult> ExecuteChangeResourceLocationCommandAsync(string resourceName, ExecuteCommandContext context)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        ArgumentNullException.ThrowIfNull(context);

        var model = context.Services.GetRequiredService<DistributedApplicationModel>();

        return ExecuteCommandAsync(
            () => ChangeResourceLocationCommandAsync(model, resourceName, context.Arguments, context.CancellationToken),
            AzureProvisioningStrings.ChangeResourceLocationCommandSuccess,
            () => CreateResourceCommandResultDataAsync(ChangeResourceLocationCommandName, model, resourceName, context.CancellationToken),
            AzureProvisioningFailureDetails.ProvisionOperation);
    }

    private Task<ExecuteCommandResult> ExecuteGetAzureResourceCommandAsync(string resourceName, ExecuteCommandContext context)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        ArgumentNullException.ThrowIfNull(context);

        var model = context.Services.GetRequiredService<DistributedApplicationModel>();

        return ExecuteCommandAsync(
            () => Task.CompletedTask,
            AzureProvisioningStrings.GetAzureResourceCommandSuccess,
            () => CreateAzureResourceInfoCommandResultDataAsync(model, resourceName, context.CancellationToken));
    }

    private Task<ExecuteCommandResult> ExecuteCancelResourceDeploymentCommandAsync(string resourceName, ExecuteCommandContext context)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        ArgumentNullException.ThrowIfNull(context);

        var model = context.Services.GetRequiredService<DistributedApplicationModel>();

        return ExecuteCommandAsync(
            () => CancelResourceDeploymentAsync(model, resourceName, context.CancellationToken),
            AzureProvisioningStrings.CancelDeploymentCommandSuccess,
            () => CreateResourceCommandResultDataAsync(CancelDeploymentCommandName, model, resourceName, context.CancellationToken));
    }

    private Task<ExecuteCommandResult> ExecuteDeleteAzureResourceCommandAsync(string resourceName, ExecuteCommandContext context)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        ArgumentNullException.ThrowIfNull(context);

        var model = context.Services.GetRequiredService<DistributedApplicationModel>();

        return ExecuteCommandAsync(
            () => RunOperationAsync<DeleteAzureResourceResult>(model, new DeleteAzureResourceIntent(resourceName), context.CancellationToken),
            AzureProvisioningStrings.DeleteAzureResourceCommandSuccess,
            result => CreateDeleteAzureResourceCommandResultDataAsync(model, resourceName, result, context.CancellationToken));
    }

    private Task<ExecuteCommandResult> ExecuteForgetStateCommandAsync(string resourceName, ExecuteCommandContext context)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        ArgumentNullException.ThrowIfNull(context);

        var model = context.Services.GetRequiredService<DistributedApplicationModel>();

        return ExecuteCommandAsync(
            () => ForgetResourceStateAsync(model, resourceName, context.CancellationToken),
            AzureProvisioningStrings.ForgetStateCommandSuccess,
            () => CreateResourceCommandResultDataAsync(ForgetStateCommandName, model, resourceName, context.CancellationToken));
    }

    private Task<ExecuteCommandResult> ExecuteReprovisionResourceCommandAsync(string resourceName, ExecuteCommandContext context)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        ArgumentNullException.ThrowIfNull(context);

        var model = context.Services.GetRequiredService<DistributedApplicationModel>();

        return ExecuteCommandAsync(
            () => ReprovisionResourceAsync(model, resourceName, context.CancellationToken),
            AzureProvisioningStrings.ReprovisionResourceCommandSuccess,
            () => CreateResourceCommandResultDataAsync(ReprovisionResourceCommandName, model, resourceName, context.CancellationToken),
            AzureProvisioningFailureDetails.ProvisionOperation);
    }

    private async Task<bool> ChangeAzureContextCommandAsync(DistributedApplicationModel model, InteractionInputCollection arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count == 0)
        {
            // Resource command execution can still invoke the command without dashboard-provided
            // arguments. In that case fall back to the provisioning options manager prompt path,
            // which is also used outside the dashboard command argument flow.
            return await ChangeAzureContextAsync(model, cancellationToken).ConfigureAwait(false);
        }

        var location = arguments.GetString(LocationArgumentName);
        if (!string.IsNullOrWhiteSpace(location))
        {
            // Users can type either a display name or a canonical Azure location. Normalize before
            // persisting so future provisioning compares and reapplies a stable location value.
            location = NormalizeLocation(location, await GetLocationOptionsAsync(arguments.GetString(SubscriptionIdArgumentName), cancellationToken).ConfigureAwait(false));
        }

        // Convert the dialog inputs into the persisted provisioning options shape. Tenant ID was
        // added after the original subscription/resource-group/location options, so keep the lookup
        // tolerant in case an older caller submits the smaller argument set.
        var options = new AzureProvisioningOptionsUpdate(
            SubscriptionId: arguments.GetString(SubscriptionIdArgumentName),
            ResourceGroup: arguments.GetString(ResourceGroupArgumentName),
            Location: location,
            TenantId: arguments.TryGetByName(TenantIdArgumentName, out var tenantInput) ? tenantInput.Value : null);

        return await ChangeAzureContextAsync(model, options, cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> ChangeResourceLocationCommandAsync(DistributedApplicationModel model, string resourceName, InteractionInputCollection arguments, CancellationToken cancellationToken)
    {
        if (arguments.Count == 0)
        {
            // Commands invoked from non-dashboard surfaces may not include the pre-declared
            // argument collection. Preserve the interactive prompt path for those callers instead
            // of treating the command as malformed.
            return ChangeResourceLocationAsync(model, resourceName, cancellationToken);
        }

        var location = arguments.GetString(LocationArgumentName);
        if (string.IsNullOrWhiteSpace(location))
        {
            return Task.FromResult(false);
        }

        return ChangeResourceLocationAsync(model, resourceName, location, cancellationToken);
    }

    internal ResourceCommandState GetEnvironmentCommandState()
    {
        lock (_operationStateLock)
        {
            // Environment commands affect every provisionable Azure resource, so expose a simple
            // global gate: while any user-visible Azure operation is running, prevent another one
            // from entering the serialized queue.
            return _state.Status.CurrentIntent is null ? ResourceCommandState.Enabled : ResourceCommandState.Disabled;
        }
    }

    internal ResourceCommandState GetResourceCommandState(string resourceName, AzureResourceCommand command, UpdateCommandStateContext context)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        ArgumentNullException.ThrowIfNull(context);

        lock (_operationStateLock)
        {
            var currentOperation = _state.Status.CurrentIntent?.Operation;
            if (currentOperation is not null)
            {
                if (command == AzureResourceCommand.GetAzureResource)
                {
                    // The info command is read-only and useful while an operation is in flight,
                    // especially for debugging cached deployment state after a failure.
                    return ResourceCommandState.Enabled;
                }

                var currentOperationAffectsResource = currentOperation.IsAllResources || currentOperation.ResourceNames.Contains(resourceName);
                if (command == AzureResourceCommand.CancelDeployment)
                {
                    // Cancellation is the only mutating command that can be enabled during another
                    // operation, and only for resources that are currently in a deployment state the
                    // provisioner can cancel.
                    return currentOperationAffectsResource && IsCancelableDeploymentState(context.ResourceSnapshot)
                        ? ResourceCommandState.Enabled
                        : ResourceCommandState.Disabled;
                }

                return currentOperationAffectsResource
                    ? ResourceCommandState.Disabled
                    : ResourceCommandState.Enabled;
            }
        }

        return command == AzureResourceCommand.CancelDeployment && !IsCancelableDeploymentState(context.ResourceSnapshot)
            ? ResourceCommandState.Disabled
            : ResourceCommandState.Enabled;
    }

    private static bool IsCancelableDeploymentState(CustomResourceSnapshot snapshot)
        => snapshot.State?.Text is CreatingArmDeploymentState or WaitingForDeploymentState;

    private async Task RunOperationAsync(DistributedApplicationModel model, AzureIntent intent, CancellationToken cancellationToken)
    {
        _ = await QueueAndWaitForOperationAsync(
            model,
            intent,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> RunOperationAsync<T>(DistributedApplicationModel model, AzureIntent intent, CancellationToken cancellationToken)
    {
        return (T)(await QueueAndWaitForOperationAsync(
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
            // for unrelated resources in the same batch. Preserve an existing incomplete TCS because project startup
            // can already be resolving connection strings through BicepOutputReference; replacing that TCS would strand
            // those waiters even after provisioning completes.
            if (resource.AzureResource.ProvisioningTaskCompletionSource is not { Task.IsCompleted: false })
            {
                resource.AzureResource.ProvisioningTaskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            await PublishUpdateToResourceTreeAsync(resource, parentChildLookup, state => state with
            {
                State = new("Starting", KnownResourceStateStyles.Info),
                Properties = state.Properties.WithoutAzureProvisioningFailureProperties()
            }).ConfigureAwait(false);

            afterProvisionTasks.Add(AfterProvisionAsync(resource, parentChildLookup));
        }

        await ProvisionAzureResourcesAsync(azureResources, parentChildLookup, cancellationToken).ConfigureAwait(false);

        // AfterProvisionAsync is responsible for publishing each resource's terminal state.
        // Wait for those observers before publishing the aggregate environment state, but
        // inspect the per-resource TCSs below so one failed observer does not hide others.
        await Task.WhenAll(afterProvisionTasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        cancellationToken.ThrowIfCancellationRequested();

        var hasFailures = azureResources.Any(static resource =>
            resource.AzureResource.ProvisioningTaskCompletionSource?.Task is { IsFaulted: true } or { IsCanceled: true });

        await PublishAzureEnvironmentStateAsync(
            model,
            hasFailures
                ? new ResourceStateSnapshot("Failed to Provision", KnownResourceStateStyles.Error)
                : new ResourceStateSnapshot("Running", KnownResourceStateStyles.Success),
            cancellationToken).ConfigureAwait(false);

        if (hasFailures && HasMissingAzureContextFailure(azureResources))
        {
            EnsureAzureContextNotificationStarted(model);
        }

        return !hasFailures;
    }

    private static bool HasMissingAzureContextFailure(IReadOnlyList<(IResource Resource, IAzureResource AzureResource)> azureResources)
        => azureResources.Any(static resource =>
            resource.AzureResource.ProvisioningTaskCompletionSource?.Task.Exception?.InnerExceptions.Any(IsMissingAzureContextFailure) == true);

    private static Exception? GetProvisioningFailureException(IReadOnlyList<(IResource Resource, IAzureResource AzureResource)> azureResources)
        => azureResources
            .Select(static resource => resource.AzureResource.ProvisioningTaskCompletionSource?.Task.Exception)
            .FirstOrDefault(static exception => exception is not null);

    private static string CreateProvisioningFailureMessage(IReadOnlyList<(IResource Resource, IAzureResource AzureResource)> azureResources)
    {
        var failureException = GetProvisioningFailureException(azureResources);
        if (failureException is not null &&
            AzureProvisioningFailureDetails.TryCreate(failureException, AzureProvisioningFailureDetails.ProvisionOperation) is { } failure)
        {
            return $"Azure provisioning failed. {failure.ToCommandMessage()}";
        }

        return "Azure provisioning failed.";
    }

    private async Task<bool> EnsureProvisionedOrThrowAsync(
        DistributedApplicationModel model,
        IReadOnlyList<(IResource Resource, IAzureResource AzureResource)> azureResources,
        CancellationToken cancellationToken)
    {
        if (!await EnsureProvisionedCoreAsync(model, azureResources, cancellationToken).ConfigureAwait(false))
        {
            var failureException = GetProvisioningFailureException(azureResources);
            var message = CreateProvisioningFailureMessage(azureResources);
            throw failureException is null
                ? new InvalidOperationException(message)
                : new InvalidOperationException(message, failureException);
        }

        return true;
    }

    private async Task ResetResourcesAsync(
        DistributedApplicationModel model,
        IReadOnlyCollection<(IResource Resource, IAzureResource AzureResource)> azureResources,
        bool preserveOverrides,
        CancellationToken cancellationToken,
        bool preserveInferredLocationOverrides = true)
    {
        var parentChildLookup = model.Resources.OfType<IResourceWithParent>().ToLookup(r => r.Parent);
        // When preserving overrides, compare any per-resource location against the current
        // environment location. A persisted value equal to the environment is treated as inferred
        // state, not a user override, unless the caller explicitly asks to preserve inferred values.
        var environmentLocation = preserveOverrides
            ? (await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false)).Location
            : null;

        foreach (var resource in azureResources)
        {
            if (resource.AzureResource is not AzureBicepResource bicepResource)
            {
                continue;
            }

            var currentLocationOverride = preserveOverrides && preserveInferredLocationOverrides
                ? TryGetCurrentResourceLocationOverride(bicepResource, environmentLocation)
                : null;

            if (currentLocationOverride is not null)
            {
                // Apply the override before clearing cached state so the next provisioning pass
                // emits Bicep with the desired per-resource location.
                bicepResource.Parameters[AzureBicepResource.KnownParameters.Location] = currentLocationOverride;
            }
            else if (!preserveOverrides || !preserveInferredLocationOverrides)
            {
                // Remove stale inferred parameters when the environment context changed. Leaving
                // them in place would make a reset/reprovision keep deploying to the previous
                // environment location.
                bicepResource.Parameters.Remove(AzureBicepResource.KnownParameters.Location);
            }

            await ClearCachedDeploymentStateAsync(bicepResource, preserveOverrides, environmentLocation, currentLocationOverride, preserveInferredLocationOverrides, cancellationToken).ConfigureAwait(false);

            // BicepResource outputs are cached in-memory as well as in deployment state. Clear both
            // so connection strings and dependent resources do not observe values from a prior ARM
            // deployment after a reset.
            bicepResource.Outputs.Clear();
            bicepResource.SecretOutputs.Clear();

            if (bicepResource is IAzureKeyVaultResource keyVaultResource)
            {
                // Key Vault secret resolution is tied to the old deployment outputs. Force it to be
                // rebuilt after the resource is provisioned again.
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
        // Clear before DeleteSectionAsync so implementations that persist the same section instance
        // do not accidentally keep stale values if deletion is implemented as a save/remove hybrid.
        section.Data.Clear();
        await deploymentStateManager.DeleteSectionAsync(section, cancellationToken).ConfigureAwait(false);
    }

    private static List<(IResource Resource, IAzureResource AzureResource)> GetProvisionableAzureResources(DistributedApplicationModel model)
    {
        // Containers and emulators participate in the Aspire model but do not produce ARM
        // deployments. Keep them out of controller operations so command state and drift checks only
        // target resources backed by Azure deployment state.
        return [.. AzureResourcePreparer.GetAzureResourcesFromAppModel(model).Where(static resource =>
            resource.AzureResource is AzureBicepResource bicepResource &&
            !bicepResource.IsContainer() &&
            !bicepResource.IsEmulator())];
    }

    private static List<(IResource Resource, IAzureResource AzureResource)> GetTargetAzureResources(DistributedApplicationModel model, string resourceName)
    {
        var azureResources = GetProvisionableAzureResources(model);
        var targetResource = azureResources.SingleOrDefault(resource =>
            string.Equals(resource.Resource.Name, resourceName, StringComparisons.ResourceName) ||
            string.Equals(resource.AzureResource.Name, resourceName, StringComparisons.ResourceName));

        if (targetResource == default)
        {
            throw new InvalidOperationException($"Azure resource '{resourceName}' was not found or cannot be reprovisioned.");
        }

        var parentChildLookup = model.Resources.OfType<IResourceWithParent>().ToLookup(r => r.Parent);
        var visitedResources = new HashSet<string>(StringComparers.ResourceName);
        var queue = new Queue<(IResource Resource, IAzureResource AzureResource)>();
        var targetResources = new List<(IResource Resource, IAzureResource AzureResource)>();

        Enqueue(targetResource);

        // Per-resource operations need to include any provisionable Azure resource owned by the
        // selected resource, including children attached through the Aspire resource graph or
        // RoleAssignment annotations, so dependent resources stay in sync with the target.
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

    private async Task<object?> QueueAndWaitForOperationAsync(
        DistributedApplicationModel model,
        AzureIntent intent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The first Azure operation lazily starts the background pieces. This avoids creating tasks
        // in apps that reference Azure hosting packages but never provision in run mode.
        EnsureDriftMonitorStarted(model);
        EnsureOperationLoopStarted();

        var queuedOperation = new QueuedOperation(
            model,
            intent,
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            cancellationToken);

        // All dashboard, CLI, and background Azure operations enter through this queue.
        // Running them inline would reintroduce re-entrancy between command handlers and
        // provisioning callbacks; the single reader below is the synchronization boundary.
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
            // Delay before each check so the gap between drift checks is constant regardless of how long
            // the previous check ran. PeriodicTimer would fire back-to-back if a check exceeded the interval.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(DriftCheckInterval, timeProvider, stoppingToken).ConfigureAwait(false);
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
        }, stoppingToken);
    }

    private void EnsureOperationLoopStarted()
    {
        if (Interlocked.CompareExchange(ref _operationLoopStarted, 1, 0) == 0)
        {
            var stoppingToken = serviceProvider.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessOperationLoopAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Azure operation processing failed.");
                    CancelPendingOperations(stoppingToken);
                }
            }, stoppingToken);
        }
    }

    private async Task ProcessOperationLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var operation in _operationChannel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            CancelPendingOperations(stoppingToken);
        }
    }

    private void CancelPendingOperations(CancellationToken cancellationToken)
    {
        while (_operationChannel.Reader.TryRead(out var operation))
        {
            if (operation.Intent is DetectDriftIntent)
            {
                CompleteDriftCheck();
            }

            operation.Completion.TrySetCanceled(cancellationToken);
        }
    }

    private async Task ProcessQueuedOperationAsync(QueuedOperation queuedOperation)
    {
        var updatesCommandState = queuedOperation.Intent is not DetectDriftIntent;
        var shouldPromptForMissingAzureContext = false;
        if (updatesCommandState)
        {
            // Publish command-state changes before running the operation so dashboard buttons
            // disable immediately instead of remaining clickable until the first resource update.
            StartOperation(queuedOperation.Intent);
        }

        try
        {
            if (updatesCommandState)
            {
                await RefreshCommandStatesAsync(queuedOperation.Model, queuedOperation.CancellationToken).ConfigureAwait(false);
            }

            var result = await ExecuteIntentAsync(queuedOperation.Model, queuedOperation.Intent, queuedOperation.CancellationToken).ConfigureAwait(false);
            queuedOperation.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException ex) when (queuedOperation.CancellationToken.IsCancellationRequested || ex.CancellationToken == queuedOperation.CancellationToken)
        {
            queuedOperation.Completion.TrySetCanceled(queuedOperation.CancellationToken.IsCancellationRequested ? queuedOperation.CancellationToken : ex.CancellationToken);
        }
        catch (Exception ex)
        {
            if (IsMissingAzureContextFailure(ex))
            {
                shouldPromptForMissingAzureContext = true;
            }

            queuedOperation.Completion.TrySetException(ex);
        }
        finally
        {
            if (updatesCommandState)
            {
                CompleteOperation(queuedOperation.Intent);
                // Use CancellationToken.None for the final refresh because command state must be
                // re-enabled even if the operation request token was canceled after the work stopped.
                await RefreshCommandStatesAsync(queuedOperation.Model, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                // Drift detection is a background probe. It must serialize with commands, but it
                // should not make dashboard commands flicker disabled while it checks ARM state.
                CompleteDriftCheck();
            }

            if (shouldPromptForMissingAzureContext)
            {
                EnsureAzureContextNotificationStarted(queuedOperation.Model);
            }
        }
    }

    private void EnsureAzureContextNotificationStarted(DistributedApplicationModel model)
    {
        if (Interlocked.CompareExchange(ref _azureContextNotificationStarted, 1, 0) != 0)
        {
            return;
        }

        var stoppingToken = serviceProvider.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await PromptForMissingAzureContextAsync(model, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Azure context notification failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _azureContextNotificationStarted, 0);
            }
        }, stoppingToken);
    }

    private async Task PromptForMissingAzureContextAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        var azureResources = GetProvisionableAzureResources(model);
        if (azureResources.Count == 0)
        {
            return;
        }

        // This loop is the "missing Azure context" state expressed as an interaction. It intentionally
        // lives outside the Azure operation queue: waiting for a user to click the notification or fill
        // the dialog can take an arbitrary amount of time, and holding the queue would disable commands
        // that should remain usable while the dashboard is asking for context.
        while (true)
        {
            var interactionService = serviceProvider.GetRequiredService<IInteractionService>();
            if (!interactionService.IsAvailable ||
                await provisioningOptionsManager.EnsureProvisioningOptionsAsync(forcePrompt: false, cancellationToken).ConfigureAwait(false))
            {
                // Another path may have supplied the context while this notification task was waiting
                // to run. Re-check before posting so we don't show stale UI.
                return;
            }

            var notificationResult = await interactionService.PromptNotificationAsync(
                AzureProvisioningStrings.NotificationTitle,
                AzureProvisioningStrings.NotificationMessage,
                new NotificationInteractionOptions
                {
                    Intent = MessageIntent.Warning,
                    PrimaryButtonText = AzureProvisioningStrings.NotificationPrimaryButtonText
                },
                cancellationToken).ConfigureAwait(false);

            if (notificationResult.Canceled || !notificationResult.Data)
            {
                // Dismissing the notification does not change the underlying state. If Azure resources
                // still need context, loop so the notification is posted again.
                continue;
            }

            var updated = await provisioningOptionsManager.EnsureProvisioningOptionsAsync(forcePrompt: true, cancellationToken).ConfigureAwait(false);
            if (!updated)
            {
                // Canceling the configure dialog leaves the context missing too, so keep the
                // notification alive instead of treating the prompt as a one-shot operation.
                continue;
            }

            // Only the apply/reprovision work is queued. At this point the user interaction has
            // completed and the serialized operation can safely reset state and provision resources.
            await provisioningOptionsManager.PersistProvisioningOptionsAsync(cancellationToken).ConfigureAwait(false);
            await RunOperationAsync<bool>(model, new ApplyAzureContextIntent(), cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    private static bool IsMissingAzureContextFailure(Exception ex)
        => ex is MissingConfigurationException ||
            ex is AggregateException aggregateException && aggregateException.InnerExceptions.Any(IsMissingAzureContextFailure);

    private async Task<object?> ExecuteIntentAsync(DistributedApplicationModel model, AzureIntent intent, CancellationToken cancellationToken)
    {
        return intent switch
        {
            ResetStateIntent resetState => await ExecuteResetStateAsync(model, resetState, cancellationToken).ConfigureAwait(false),
            ForgetResourceStateIntent forgetResourceState => await ExecuteForgetResourceStateAsync(model, forgetResourceState, cancellationToken).ConfigureAwait(false),
            ChangeAzureContextIntent changeAzureContext => await ExecuteChangeAzureContextAsync(model, changeAzureContext, cancellationToken).ConfigureAwait(false),
            ApplyAzureContextIntent => await ExecuteApplyAzureContextAsync(model, cancellationToken).ConfigureAwait(false),
            EnsureProvisionedIntent => await ExecuteEnsureProvisionedAsync(model, cancellationToken).ConfigureAwait(false),
            ReprovisionAllIntent => await ExecuteReprovisionAllAsync(model, cancellationToken).ConfigureAwait(false),
            DeleteAzureResourcesIntent => await ExecuteDeleteAzureResourcesAsync(model, cancellationToken).ConfigureAwait(false),
            ChangeResourceLocationIntent changeResourceLocation => await ExecuteChangeResourceLocationAsync(model, changeResourceLocation, cancellationToken).ConfigureAwait(false),
            ReprovisionResourceIntent reprovisionResource => await ExecuteReprovisionResourceAsync(model, reprovisionResource, cancellationToken).ConfigureAwait(false),
            CancelResourceDeploymentIntent cancelResourceDeployment => await ExecuteCancelResourceDeploymentAsync(model, cancelResourceDeployment, cancellationToken).ConfigureAwait(false),
            DeleteAzureResourceIntent deleteAzureResource => await ExecuteDeleteAzureResourceAsync(model, deleteAzureResource, cancellationToken).ConfigureAwait(false),
            DetectDriftIntent => await ExecuteDetectDriftAsync(model, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unexpected Azure intent.")
        };
    }

    private async Task<bool> ExecuteResetStateAsync(DistributedApplicationModel model, ResetStateIntent intent, CancellationToken cancellationToken)
    {
        // Resetting the environment removes the top-level provisioning context first, then clears
        // each deployment section. This makes future prompts fall back to configuration/defaults and
        // prevents resources from showing old Azure identity properties.
        await DeleteSectionAsync("Azure", cancellationToken).ConfigureAwait(false);

        var azureResources = GetProvisionableAzureResources(model);
        await ResetResourcesAsync(model, azureResources, preserveOverrides: false, cancellationToken).ConfigureAwait(false);

        await PublishAzureEnvironmentStateAsync(model, KnownResourceStates.NotStarted, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Azure provisioning state reset for {Count} Azure resources.", azureResources.Count);
        if (!intent.ReprovisionAfterReset || !serviceProvider.GetRequiredService<IInteractionService>().IsAvailable)
        {
            return true;
        }

        return await EnsureProvisionedCoreAsync(model, azureResources, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object?> ExecuteForgetResourceStateAsync(DistributedApplicationModel model, ForgetResourceStateIntent intent, CancellationToken cancellationToken)
    {
        var targetResources = GetTargetAzureResources(model, intent.ResourceName);
        // Forgetting state is local-only. It deliberately does not call ARM delete; users choose the
        // Delete command when they want Aspire to remove live Azure resources.
        await ResetResourcesAsync(model, targetResources, preserveOverrides: false, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Azure provisioning state reset for resource {ResourceName}.", intent.ResourceName);
        return null;
    }

    private async Task<bool> ExecuteChangeAzureContextAsync(DistributedApplicationModel model, ChangeAzureContextIntent intent, CancellationToken cancellationToken)
    {
        if (intent.Options is null)
        {
            // This is the legacy/non-dashboard path. The options manager owns prompting and
            // persistence when the command is invoked without the dashboard argument collection.
            var updated = await provisioningOptionsManager.EnsureProvisioningOptionsAsync(forcePrompt: true, cancellationToken).ConfigureAwait(false);
            if (!updated)
            {
                return false;
            }

            await provisioningOptionsManager.PersistProvisioningOptionsAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await provisioningOptionsManager.ApplyProvisioningOptionsAsync(intent.Options, cancellationToken).ConfigureAwait(false);
        }

        // Changing subscription/resource group/location invalidates cached deployment state. Preserve
        // explicit user location overrides, but drop inferred overrides that only mirrored the previous
        // environment location; otherwise resources can accidentally stay pinned to the old context.
        await ResetResourcesAsync(model, GetProvisionableAzureResources(model), preserveOverrides: true, cancellationToken, preserveInferredLocationOverrides: false).ConfigureAwait(false);
        await PublishAzureEnvironmentStateAsync(model, KnownResourceStates.NotStarted, cancellationToken).ConfigureAwait(false);
        return await EnsureProvisionedOrThrowAsync(model, GetProvisionableAzureResources(model), cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ExecuteApplyAzureContextAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        // The notification prompt runs outside the operation queue so dashboard commands stay responsive
        // while the dialog is open. This queued operation only applies the saved context and reprovisions.
        await ResetResourcesAsync(model, GetProvisionableAzureResources(model), preserveOverrides: true, cancellationToken, preserveInferredLocationOverrides: false).ConfigureAwait(false);
        await PublishAzureEnvironmentStateAsync(model, KnownResourceStates.NotStarted, cancellationToken).ConfigureAwait(false);
        return await EnsureProvisionedOrThrowAsync(model, GetProvisionableAzureResources(model), cancellationToken).ConfigureAwait(false);
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
        return await EnsureProvisionedOrThrowAsync(model, GetProvisionableAzureResources(model), cancellationToken).ConfigureAwait(false);
    }

    private async Task<object?> ExecuteDeleteAzureResourcesAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        // Delete-all operates at the resource-group boundary because run-mode provisioning creates a
        // single environment resource group. Per-resource deletion uses cached deployment outputs
        // instead, since individual Azure resources may not map one-to-one to a resource group.
        await PublishAzureEnvironmentStateAsync(
            model,
            new ResourceStateSnapshot("Deleting", KnownResourceStateStyles.Info),
            cancellationToken).ConfigureAwait(false);

        string? resourceGroupName;
        try
        {
            resourceGroupName = await DeleteCurrentResourceGroupIfExistsAsync(cancellationToken).ConfigureAwait(false);

            await ResetResourcesAsync(model, GetProvisionableAzureResources(model), preserveOverrides: true, cancellationToken).ConfigureAwait(false);
            await PublishAzureEnvironmentStateAsync(model, KnownResourceStates.NotStarted, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException)
        {
            await PublishAzureEnvironmentStateAsync(
                model,
                new ResourceStateSnapshot("Failed to Delete", KnownResourceStateStyles.Error),
                cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (string.IsNullOrEmpty(resourceGroupName))
        {
            _logger.LogInformation("Azure deployment state reset without deleting a resource group because no Azure resource group was configured.");
        }
        else
        {
            _logger.LogInformation("Azure resource group {ResourceGroup} was deleted or was already absent.", resourceGroupName);
        }

        return null;
    }

    private async Task<string?> DeleteCurrentResourceGroupIfExistsAsync(CancellationToken cancellationToken)
    {
        var section = await deploymentStateManager.AcquireSectionAsync("Azure", cancellationToken).ConfigureAwait(false);
        var subscriptionId = section.Data["SubscriptionId"]?.GetValue<string>();
        var resourceGroupName = section.Data["ResourceGroup"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(subscriptionId) ||
            string.IsNullOrWhiteSpace(resourceGroupName))
        {
            return null;
        }

        var armClientProvider = serviceProvider.GetRequiredService<IArmClientProvider>();
        var tokenCredentialProvider = serviceProvider.GetRequiredService<ITokenCredentialProvider>();
        var armClient = armClientProvider.GetArmClient(tokenCredentialProvider.TokenCredential, subscriptionId);
        // Resolve the subscription through ARM before accessing resource groups so tenant/subscription
        // mismatches fail with the same Azure SDK behavior used by provisioning.
        var (subscription, _) = await armClient.GetSubscriptionAndTenantAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var response = await subscription.GetResourceGroups().GetAsync(resourceGroupName, cancellationToken).ConfigureAwait(false);
            await response.Value.DeleteAsync(WaitUntil.Completed, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogInformation("Azure resource group {ResourceGroup} was already absent.", resourceGroupName);
        }

        return resourceGroupName;
    }

    private async Task<bool> ExecuteChangeResourceLocationAsync(DistributedApplicationModel model, ChangeResourceLocationIntent intent, CancellationToken cancellationToken)
    {
        var targetResources = GetTargetAzureResources(model, intent.ResourceName);
        if (targetResources[0].AzureResource is AzureBicepResource targetBicepResource)
        {
            // ARM rejects redeploying many resource types to a different location while the old
            // resource still exists. Delete the cached live resource first, then save the override
            // that the next provisioning pass will apply.
            await DeleteCachedResourceForLocationChangeAsync(targetBicepResource, intent.Location, cancellationToken).ConfigureAwait(false);
            await SetResourceLocationOverrideAsync(targetBicepResource.Name, intent.Location, cancellationToken).ConfigureAwait(false);
        }
        await ResetResourcesAsync(model, targetResources, preserveOverrides: true, cancellationToken).ConfigureAwait(false);
        return await EnsureProvisionedOrThrowAsync(model, targetResources, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ExecuteReprovisionResourceAsync(DistributedApplicationModel model, ReprovisionResourceIntent intent, CancellationToken cancellationToken)
    {
        var targetResources = GetTargetAzureResources(model, intent.ResourceName);
        await ResetResourcesAsync(model, targetResources, preserveOverrides: true, cancellationToken).ConfigureAwait(false);
        return await EnsureProvisionedOrThrowAsync(model, targetResources, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object?> ExecuteCancelResourceDeploymentAsync(DistributedApplicationModel model, CancelResourceDeploymentIntent intent, CancellationToken cancellationToken)
    {
        var targetResources = GetTargetAzureResources(model, intent.ResourceName);
        var parentChildLookup = model.Resources.OfType<IResourceWithParent>().ToLookup(r => r.Parent);
        var canceledDeploymentCount = await CancelCachedDeploymentsAsync(targetResources, requireDeployment: true, cancellationToken).ConfigureAwait(false);

        foreach (var resource in targetResources)
        {
            await PublishUpdateToResourceTreeAsync(resource, parentChildLookup, state => state with
            {
                State = new("Canceled", KnownResourceStateStyles.Info)
            }).ConfigureAwait(false);
        }

        _logger.LogInformation("Canceled {Count} Azure deployment(s) for resource {ResourceName}.", canceledDeploymentCount, intent.ResourceName);
        return null;
    }

    private async Task<DeleteAzureResourceResult> ExecuteDeleteAzureResourceAsync(DistributedApplicationModel model, DeleteAzureResourceIntent intent, CancellationToken cancellationToken)
    {
        var targetResources = GetTargetAzureResources(model, intent.ResourceName);
        var parentChildLookup = model.Resources.OfType<IResourceWithParent>().ToLookup(r => r.Parent);

        foreach (var resource in targetResources)
        {
            // Show the whole affected resource tree as deleting before the first ARM call so the
            // dashboard reflects that child resources/role assignments are part of the operation.
            await PublishUpdateToResourceTreeAsync(resource, parentChildLookup, state => state with
            {
                State = new("Deleting", KnownResourceStateStyles.Info)
            }).ConfigureAwait(false);
        }

        IReadOnlyList<string> resourceIds;
        try
        {
            // A resource can have an in-progress deployment and already-created target resources.
            // Cancel first to stop ARM from continuing to create/update resources while deletion is
            // collecting and removing the known targets.
            await CancelCachedDeploymentsAsync(targetResources, requireDeployment: false, cancellationToken).ConfigureAwait(false);
            resourceIds = await GetAzureResourceIdsForDeletionAsync(targetResources, cancellationToken).ConfigureAwait(false);
            if (resourceIds.Count == 0)
            {
                throw new InvalidOperationException($"No cached Azure resource IDs were found for resource '{intent.ResourceName}'. Use '{ForgetStateCommandName}' to clear local state only.");
            }

            await DeleteAzureResourceIdsAsync(resourceIds, intent.ResourceName, cancellationToken).ConfigureAwait(false);
            await ResetResourcesAsync(model, targetResources, preserveOverrides: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            foreach (var resource in targetResources)
            {
                await PublishUpdateToResourceTreeAsync(resource, parentChildLookup, state => state with
                {
                    State = new("Failed to Delete", KnownResourceStateStyles.Error)
                }).ConfigureAwait(false);
            }

            throw;
        }

        _logger.LogInformation("Deleted {Count} Azure resource(s) for resource {ResourceName}.", resourceIds.Count, intent.ResourceName);
        return new DeleteAzureResourceResult(resourceIds);
    }

    private async Task<object?> ExecuteDetectDriftAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        // Drift only matters after the environment is Running. During provisioning, reset, or delete
        // operations the resource states already communicate that Azure may not match cached state.
        if (model.Resources.OfType<AzureEnvironmentResource>().SingleOrDefault() is not { } environmentResource ||
            !notificationService.TryGetCurrentState(environmentResource.Name, out var environmentEvent) ||
            environmentEvent.Snapshot.State?.Text != KnownResourceStates.Running)
        {
            return null;
        }

        var context = await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(context.SubscriptionId, out _))
        {
            // Without a valid subscription we cannot safely ask ARM whether cached resource IDs
            // exist. Leave state unchanged rather than marking everything as drifted.
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
                // Resources without cached IDs are either not provisioned yet or already reset. They
                // do not provide enough information for a live ARM existence check.
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
            // Store the intent rather than just a boolean so command-state calculation can keep
            // unaffected resources enabled while a per-resource operation is running.
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

    private static AzureControllerState CreateControllerState(AzureIntent? currentIntent)
        => new(new AzureControllerStatus(currentIntent));

    private static async Task<ExecuteCommandResult> ExecuteCommandAsync(Func<Task> action, string successMessage, Func<Task<CommandResultData>> createResultData, string? failureOperation = null)
    {
        try
        {
            await action().ConfigureAwait(false);
            return CommandResults.Success(successMessage, await createResultData().ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return CommandResults.Canceled();
        }
        catch (Exception ex)
        {
            return CreateFailureCommandResult(ex, failureOperation);
        }
    }

    private static async Task<ExecuteCommandResult> ExecuteCommandAsync<T>(Func<Task<T>> action, string successMessage, Func<T, Task<CommandResultData>> createResultData, string? failureOperation = null)
    {
        try
        {
            var result = await action().ConfigureAwait(false);
            return CommandResults.Success(successMessage, await createResultData(result).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return CommandResults.Canceled();
        }
        catch (Exception ex)
        {
            return CreateFailureCommandResult(ex, failureOperation);
        }
    }

    private static async Task<ExecuteCommandResult> ExecuteCommandAsync(Func<Task<bool>> action, string successMessage, Func<Task<CommandResultData>> createResultData, string? failureOperation = null)
    {
        try
        {
            return await action().ConfigureAwait(false)
                ? CommandResults.Success(successMessage, await createResultData().ConfigureAwait(false))
                : CommandResults.Canceled();
        }
        catch (OperationCanceledException)
        {
            return CommandResults.Canceled();
        }
        catch (Exception ex)
        {
            return CreateFailureCommandResult(ex, failureOperation);
        }
    }

    private async Task<CommandResultData> CreateEnvironmentCommandResultDataAsync(string commandName, DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        var json = await CreateCommandResultJsonAsync(commandName, resourceName: null, cancellationToken).ConfigureAwait(false);
        json["resourceCount"] = GetProvisionableAzureResources(model).Count;
        if (string.Equals(commandName, ResetProvisioningStateCommandName, StringComparison.Ordinal))
        {
            json["warning"] = "Local Azure provisioning state was reset without deleting live Azure resources. Existing Azure resources may be orphaned if a later reprovision uses a different Azure context.";
            json["recommendedActions"] = AzureProvisioningFailureDetails.CreateRecommendedActionsJsonArray(
            [
                new("delete-live-resources", $"Use '{DeleteAzureResourcesCommandName}' when you want Aspire to remove live Azure resources."),
                new("review-azure-context", "Review the final 'azureContext' fields before reprovisioning.")
            ]);
        }

        return CreateJsonResultData(json);
    }

    private async Task<CommandResultData> CreateResourceCommandResultDataAsync(string commandName, DistributedApplicationModel model, string resourceName, CancellationToken cancellationToken)
        => CreateJsonResultData(await CreateResourceCommandResultJsonAsync(commandName, model, resourceName, cancellationToken).ConfigureAwait(false));

    private async Task<CommandResultData> CreateDeleteAzureResourceCommandResultDataAsync(DistributedApplicationModel model, string resourceName, DeleteAzureResourceResult result, CancellationToken cancellationToken)
    {
        var json = await CreateResourceCommandResultJsonAsync(DeleteAzureResourceCommandName, model, resourceName, cancellationToken).ConfigureAwait(false);
        var deletedResourceIds = new JsonArray();
        foreach (var resourceId in result.ResourceIds)
        {
            deletedResourceIds.Add(JsonValue.Create(resourceId));
        }

        json["deletedResourceCount"] = result.ResourceIds.Count;
        json["deletedResourceIds"] = deletedResourceIds;
        return CreateJsonResultData(json);
    }

    private async Task<JsonObject> CreateResourceCommandResultJsonAsync(string commandName, DistributedApplicationModel model, string resourceName, CancellationToken cancellationToken)
    {
        var json = await CreateCommandResultJsonAsync(commandName, resourceName, cancellationToken).ConfigureAwait(false);
        var targetResources = GetTargetAzureResources(model, resourceName);
        var effectiveLocation = await GetEffectiveResourceLocationAsync(GetDeploymentStateResourceName(targetResources[0]), cancellationToken).ConfigureAwait(false);
        json["resourceCount"] = targetResources.Count;
        json["location"] = effectiveLocation;
        json["effectiveLocation"] = effectiveLocation;
        return json;
    }

    private async Task<CommandResultData> CreateAzureResourceInfoCommandResultDataAsync(DistributedApplicationModel model, string resourceName, CancellationToken cancellationToken)
    {
        var targetResources = GetTargetAzureResources(model, resourceName);

        // Targeting a parent Azure resource can include children and role assignments that must
        // be reprovisioned together. The info command, however, reports the resource the user
        // named so agents can map the command output back to the visible dashboard resource.
        var targetResource = targetResources[0];
        var json = await CreateCommandResultJsonAsync(GetAzureResourceCommandName, resourceName, cancellationToken).ConfigureAwait(false);
        var effectiveLocation = await GetEffectiveResourceLocationAsync(GetDeploymentStateResourceName(targetResource), cancellationToken).ConfigureAwait(false);
        json["resourceCount"] = targetResources.Count;
        json["location"] = effectiveLocation;
        json["effectiveLocation"] = effectiveLocation;

        if (targetResource.AzureResource is AzureBicepResource bicepResource)
        {
            var context = await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false);
            // Include both cached deployment state and a best-effort live probe. Cached state is
            // available offline; the live block lets agents distinguish "state exists but resource
            // was deleted" from "state exists and ARM can still find it".
            var deployment = await CreateCachedDeploymentStateInfoAsync(bicepResource, context, cancellationToken).ConfigureAwait(false);
            json["deployment"] = deployment;
            json["live"] = await CreateLiveResourceInfoAsync(
                deployment.TryGetPropertyValue("resourceId", out var resourceIdNode) ? resourceIdNode?.GetValue<string>() : null,
                context,
                cancellationToken).ConfigureAwait(false);
        }

        return CreateJsonResultData(json, displayImmediately: true);
    }

    private async Task<JsonObject> CreateCachedDeploymentStateInfoAsync(AzureBicepResource resource, AzureContextState context, CancellationToken cancellationToken)
    {
        var section = await deploymentStateManager.AcquireSectionAsync($"Azure:Deployments:{resource.Name}", cancellationToken).ConfigureAwait(false);
        var deploymentId = section.Data["Id"]?.GetValue<string>();
        // Deployment state stores JSON as strings because it is shared with the lower-level
        // provisioner. Parse lazily for the diagnostic command so malformed cached state can be
        // reported next to the raw payload instead of breaking command execution.
        var outputs = ParseDeploymentStateJson(resource.Name, "Outputs", section.Data["Outputs"]?.GetValue<string>());
        var resourceId = TryGetOutputValue(outputs, "id");
        var tenantId = Guid.TryParse(context.TenantId, out var parsedTenantId) ? parsedTenantId : (Guid?)null;

        var json = new JsonObject
        {
            ["hasState"] = section.Data.Count > 0,
            ["deploymentId"] = deploymentId,
            ["resourceId"] = resourceId,
            ["resourcePortalUrl"] = resourceId is not null ? AzurePortalUrls.GetResourceUrl(resourceId, tenantId) : null,
            ["locationOverride"] = section.Data[LocationOverrideKey]?.GetValue<string>(),
            ["checksum"] = section.Data["CheckSum"]?.GetValue<string>(),
            ["parameters"] = ParseDeploymentStateJson(resource.Name, "Parameters", section.Data["Parameters"]?.GetValue<string>()),
            ["outputs"] = outputs,
            ["scope"] = ParseDeploymentStateJson(resource.Name, "Scope", section.Data["Scope"]?.GetValue<string>())
        };

        if (section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>() is { Length: > 0 } provisioningState)
        {
            json["provisioningState"] = provisioningState;
        }

        if (deploymentId is not null &&
            ResourceIdentifier.TryParse(deploymentId, out var deploymentResourceId) &&
            deploymentResourceId is not null)
        {
            // The deployment ID is itself an ARM resource ID. If it parses, provide a portal link to
            // the deployment operation in addition to the provisioned resource link.
            json["deploymentPortalUrl"] = AzurePortalUrls.GetDeploymentUrl(deploymentResourceId);
        }

        return json;
    }

    private async Task<JsonObject> CreateLiveResourceInfoAsync(string? resourceId, AzureContextState context, CancellationToken cancellationToken)
    {
        // Start with a "not checked" shape so every early-return path is explicit and callers do not
        // need to infer why live ARM state is missing from absent properties.
        var json = new JsonObject
        {
            ["checked"] = false,
            ["exists"] = null
        };

        if (string.IsNullOrWhiteSpace(resourceId))
        {
            json["reason"] = "missing-resource-id";
            json["source"] = "aspire";
            json["code"] = "missing-resource-id";
            json["message"] = "Live Azure resource existence cannot be checked because cached deployment state does not contain a resource ID. This can happen after state is forgotten, deleted, or before provisioning completes.";
            json["recommendedActions"] = AzureProvisioningFailureDetails.CreateRecommendedActionsJsonArray(
                AzureProvisioningFailureDetails.GetRecommendedActions(AzureProvisioningFailureDetails.MissingResourceIdReason));
            return json;
        }

        if (!Guid.TryParse(context.SubscriptionId, out _))
        {
            json["reason"] = "invalid-subscription-id";
            return json;
        }

        try
        {
            var armClientProvider = serviceProvider.GetRequiredService<IArmClientProvider>();
            var tokenCredentialProvider = serviceProvider.GetRequiredService<ITokenCredentialProvider>();
            var armClient = armClientProvider.GetArmClient(tokenCredentialProvider.TokenCredential, context.SubscriptionId);
            json["checked"] = true;
            var exists = await armClient.ResourceExistsAsync(resourceId, cancellationToken).ConfigureAwait(false);
            json["exists"] = exists;

            if (!exists)
            {
                json["reason"] = AzureProvisioningFailureDetails.MissingLiveResourceReason;
                json["source"] = "azure";
                json["code"] = AzureProvisioningFailureDetails.MissingLiveResourceReason;
                json["message"] = "Cached deployment state points at an Azure resource ID, but the resource was not found in Azure. This can happen when resources are deleted outside Aspire or a resource group deletion is still in progress.";
                json["recommendedActions"] = AzureProvisioningFailureDetails.CreateRecommendedActionsJsonArray(
                    AzureProvisioningFailureDetails.GetRecommendedActions(AzureProvisioningFailureDetails.MissingLiveResourceReason));
            }
        }
        catch (CredentialUnavailableException ex)
        {
            // get-azure-resource is a diagnostic command. Return a machine-readable reason instead
            // of failing the command so local runs without Azure auth still expose cached state.
            _logger.LogDebug(ex, "Unable to query live Azure resource state for {ResourceId} because no Azure credential is available.", resourceId);
            json["reason"] = "credential-unavailable";
            json["message"] = ex.Message;
        }
        catch (RequestFailedException ex)
        {
            // Surface ARM failures as structured JSON so agents can distinguish "missing",
            // authorization failures, and transient request errors without scraping logs.
            _logger.LogDebug(ex, "Unable to query live Azure resource state for {ResourceId}.", resourceId);
            var failure = AzureProvisioningFailureDetails.FromRequestFailedException(ex, AzureProvisioningFailureDetails.LiveResourceCheckOperation);
            json["reason"] = "request-failed";
            json["source"] = "azure";
            json["status"] = ex.Status;
            json["provider"] = failure.Provider;
            json["httpStatus"] = failure.HttpStatus;
            json["errorCode"] = failure.ErrorCode;
            json["message"] = failure.ErrorMessage;
            json["operation"] = failure.Operation;
            json["requestId"] = failure.RequestId;
            json["correlationId"] = failure.CorrelationId;
            if (!failure.RecommendedActions.IsDefaultOrEmpty)
            {
                json["recommendedActions"] = AzureProvisioningFailureDetails.CreateRecommendedActionsJsonArray(failure.RecommendedActions);
            }
        }

        return json;
    }

    private JsonNode? ParseDeploymentStateJson(string resourceName, string propertyName, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            // Keep parse failures in the command payload instead of throwing so a diagnostic
            // command can still show the rest of the cached state.
            return AzureProvisioningJsonHelpers.ParseDeploymentStateJson(json);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Unable to parse cached {PropertyName} for Azure resource {ResourceName}.", propertyName, resourceName);
            return new JsonObject
            {
                ["parseError"] = ex.Message,
                ["raw"] = json
            };
        }
    }

    private static string? TryGetOutputValue(JsonNode? outputs, string outputName)
    {
        // Bicep deployment outputs are persisted in the ARM output shape:
        //   { "id": { "type": "String", "value": "/subscriptions/..." } }
        // Only the nested value is useful to commands; ignore partial/malformed output entries.
        if (outputs is not JsonObject outputsObject ||
            !outputsObject.TryGetPropertyValue(outputName, out var outputNode) ||
            outputNode is not JsonObject outputObject ||
            !outputObject.TryGetPropertyValue("value", out var valueNode))
        {
            return null;
        }

        return valueNode?.ToString();
    }

    private async Task<JsonObject> CreateCommandResultJsonAsync(string commandName, string? resourceName, CancellationToken cancellationToken)
    {
        var context = await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false);
        // Keep command output machine-readable and stable for agents/CLI automation. Additional
        // command-specific details are appended by the caller, but the Azure context fields are
        // present on every successful Azure command result.
        var json = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["command"] = commandName,
            ["success"] = true,
            ["subscriptionId"] = context.SubscriptionId,
            ["tenantId"] = context.TenantId,
            ["resourceGroup"] = context.ResourceGroup,
            ["azureLocation"] = context.Location,
            ["azureContext"] = new JsonObject
            {
                ["subscriptionId"] = context.SubscriptionId,
                ["tenantId"] = context.TenantId,
                ["resourceGroup"] = context.ResourceGroup,
                ["location"] = context.Location
            }
        };

        if (!string.IsNullOrEmpty(resourceName))
        {
            json["resourceName"] = resourceName;
        }

        return json;
    }

    private static CommandResultData CreateJsonResultData(JsonObject json, bool displayImmediately = false) =>
        new()
        {
            // Serialize through the shared helper so formatting/scrubbing stays consistent with the
            // rest of Azure provisioning diagnostics.
            Value = AzureProvisioningJsonHelpers.ToCommandResultJsonString(json),
            Format = CommandResultFormat.Json,
            DisplayImmediately = displayImmediately
        };

    private static ExecuteCommandResult CreateFailureCommandResult(Exception exception, string? operation = null)
    {
        if (AzureProvisioningFailureDetails.TryCreate(exception, operation) is not { } failure)
        {
            return CommandResults.Failure(exception.Message);
        }

        // Keep the human-facing message unchanged for existing dashboard/CLI behavior, but attach
        // a stable diagnostics array so agents and JSON clients can reason over provider, code,
        // resource type, request IDs, and recommended actions without scraping the rendered text.
        var json = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["success"] = false,
            ["message"] = exception.Message,
            ["status"] = "failed",
            ["summary"] = exception.Message,
            ["diagnostics"] = new JsonArray
            {
                failure.ToJsonObject()
            }
        };

        return CommandResults.Failure(exception.Message, CreateJsonResultData(json, displayImmediately: true));
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
            // Normalize old override values opportunistically. Older state or manual edits may store
            // display names such as "West US 2"; Bicep parameters should use canonical names such as
            // "westus2" so equality checks and ARM deployments are stable.
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
            // Per-resource overrides win over the environment location. The dashboard command uses
            // this to show the effective value users will get on the next provisioning pass.
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

    // Deployment state is keyed by the AzureBicepResource name because the provisioner owns that
    // state. Some visible resources are projected to a separate AzureBicepResource, so do not use
    // the visible Aspire resource name when a Bicep resource is available.
    private static string GetDeploymentStateResourceName((IResource Resource, IAzureResource AzureResource) resource)
        => resource.AzureResource is AzureBicepResource bicepResource ? bicepResource.Name : resource.Resource.Name;

    private async Task<int> CancelCachedDeploymentsAsync(
        IReadOnlyCollection<(IResource Resource, IAzureResource AzureResource)> targetResources,
        bool requireDeployment,
        CancellationToken cancellationToken)
    {
        var canceledDeploymentIds = new HashSet<string>(StringComparers.AzureResourceId);

        foreach (var resource in targetResources)
        {
            if (resource.AzureResource is not AzureBicepResource bicepResource)
            {
                continue;
            }

            var section = await deploymentStateManager.AcquireSectionAsync($"Azure:Deployments:{bicepResource.Name}", cancellationToken).ConfigureAwait(false);
            if (TryGetCachedDeploymentId(section) is not { } deploymentId ||
                !IsActiveCachedDeployment(section))
            {
                // Only active cached deployments can be canceled. Completed or missing deployments
                // should not turn a best-effort cleanup into an error unless the caller explicitly
                // required an active deployment below.
                continue;
            }

            if (canceledDeploymentIds.Add(deploymentId))
            {
                // Multiple Aspire resources can share one ARM deployment. Track IDs so a resource
                // tree cancellation sends at most one cancel request per deployment.
                await CancelCachedDeploymentAsync(deploymentId, loggerService.GetLogger(resource.AzureResource), cancellationToken).ConfigureAwait(false);
            }

            // Mark local deployment state canceled even if ARM had already finished between reading
            // state and sending the cancel request. That prevents future command state from treating
            // this cached deployment as still cancelable.
            section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey] = BicepProvisioner.DeploymentStateProvisioningStateCanceled;
            await deploymentStateManager.SaveSectionAsync(section, cancellationToken).ConfigureAwait(false);
        }

        if (requireDeployment && canceledDeploymentIds.Count == 0)
        {
            var resourceName = targetResources.Count == 1 ? targetResources.Single().Resource.Name : string.Join(", ", targetResources.Select(static resource => resource.Resource.Name));
            throw new InvalidOperationException($"No active cached Azure deployment was found for resource '{resourceName}'.");
        }

        return canceledDeploymentIds.Count;
    }

    private async Task CancelCachedDeploymentAsync(string deploymentId, ILogger resourceLogger, CancellationToken cancellationToken)
    {
        var armClient = await GetArmClientForResourceIdAsync(deploymentId, cancellationToken).ConfigureAwait(false);

        try
        {
            await armClient.CancelDeploymentAsync(deploymentId, cancellationToken).ConfigureAwait(false);
            resourceLogger.LogInformation("Cancellation requested for Azure deployment {DeploymentId}.", deploymentId);
        }
        catch (RequestFailedException ex) when (ex.Status == 404 || ex.Status == 409)
        {
            // ARM returns 404 when the deployment record is already gone and 409 when the deployment
            // is no longer in a cancelable state. Both mean the requested end state has effectively
            // been reached from Aspire's perspective.
            _logger.LogInformation(ex, "Azure deployment {DeploymentId} was already absent or no longer active during cancellation.", deploymentId);
            resourceLogger.LogInformation("Azure deployment {DeploymentId} was already absent or no longer active during cancellation.", deploymentId);
        }
    }

    private async Task<IReadOnlyList<string>> GetAzureResourceIdsForDeletionAsync(
        IReadOnlyCollection<(IResource Resource, IAzureResource AzureResource)> targetResources,
        CancellationToken cancellationToken)
    {
        var resourceIds = new HashSet<string>(StringComparers.AzureResourceId);

        foreach (var resource in targetResources)
        {
            if (resource.AzureResource is not AzureBicepResource bicepResource)
            {
                continue;
            }

            if (await TryGetResourceIdFromDeploymentStateAsync(bicepResource, cancellationToken).ConfigureAwait(false) is { } resourceId &&
                !IsArmDeploymentResourceId(resourceId))
            {
                // The primary output ID is usually the user-visible resource. Exclude deployment
                // resources because deleting the ARM deployment record does not delete the resources
                // the deployment created.
                resourceIds.Add(resourceId);
            }

            // Some Bicep files create more than one Azure resource. Ask ARM for the deployment
            // operation targets so delete-resource removes all created resources, not just the main
            // output ID.
            await AddDeploymentOperationTargetResourceIdsAsync(bicepResource, resourceIds, cancellationToken).ConfigureAwait(false);
        }

        // Delete children before parents by ordering longer resource IDs first. Azure child resource
        // IDs include their parent ID as a prefix, so length is a cheap dependency-safe heuristic.
        return [.. resourceIds.OrderByDescending(static resourceId => resourceId.Length)];
    }

    private async Task AddDeploymentOperationTargetResourceIdsAsync(AzureBicepResource resource, HashSet<string> resourceIds, CancellationToken cancellationToken)
    {
        var section = await deploymentStateManager.AcquireSectionAsync($"Azure:Deployments:{resource.Name}", cancellationToken).ConfigureAwait(false);
        if (TryGetCachedDeploymentId(section) is not { } deploymentId)
        {
            return;
        }

        var armClient = await GetArmClientForResourceIdAsync(deploymentId, cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var resourceId in armClient.GetDeploymentTargetResourceIdsAsync(deploymentId, cancellationToken).ConfigureAwait(false))
            {
                if (!IsArmDeploymentResourceId(resourceId))
                {
                    resourceIds.Add(resourceId);
                }
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogInformation(ex, "Azure deployment {DeploymentId} was absent while collecting target resources for {ResourceName}.", deploymentId, resource.Name);
        }
    }

    private async Task DeleteAzureResourceIdsAsync(IReadOnlyList<string> resourceIds, string resourceName, CancellationToken cancellationToken)
    {
        foreach (var resourceId in resourceIds)
        {
            // Resolve the ARM client per resource ID because a resource tree can contain resources
            // from deployment state that point at a different subscription than the current context.
            var armClient = await GetArmClientForResourceIdAsync(resourceId, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Deleting Azure resource {ResourceId} for {ResourceName}.", resourceId, resourceName);

            try
            {
                await armClient.DeleteResourceAsync(resourceId, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogInformation(ex, "Azure resource {ResourceId} was already absent while deleting resources for {ResourceName}.", resourceId, resourceName);
            }
        }
    }

    private async Task<IArmClient> GetArmClientForResourceIdAsync(string resourceId, CancellationToken cancellationToken)
    {
        string? subscriptionId = null;
        if (ResourceIdentifier.TryParse(resourceId, out var parsedResourceId) &&
            parsedResourceId is not null)
        {
            // Prefer the subscription embedded in the ARM resource ID. This makes cleanup resilient
            // after the user changes Azure context but still has cached state for old resources.
            subscriptionId = parsedResourceId.SubscriptionId;
        }

        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            subscriptionId = (await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false)).SubscriptionId;
        }

        if (!Guid.TryParse(subscriptionId, out _))
        {
            throw new MissingConfigurationException("Azure resources cannot be managed because the Azure subscription ID is missing or invalid.");
        }

        var armClientProvider = serviceProvider.GetRequiredService<IArmClientProvider>();
        var tokenCredentialProvider = serviceProvider.GetRequiredService<ITokenCredentialProvider>();
        return armClientProvider.GetArmClient(tokenCredentialProvider.TokenCredential, subscriptionId);
    }

    private static string? TryGetCachedDeploymentId(DeploymentStateSection section)
        => section.Data["Id"]?.GetValue<string>() is { Length: > 0 } deploymentId ? deploymentId : null;

    private static bool IsActiveCachedDeployment(DeploymentStateSection section)
        => string.Equals(
            section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>(),
            BicepProvisioner.DeploymentStateProvisioningStateRunning,
            StringComparison.Ordinal);

    private static bool IsArmDeploymentResourceId(string resourceId)
    {
        if (!ResourceIdentifier.TryParse(resourceId, out var parsedResourceId) ||
            parsedResourceId is null)
        {
            return false;
        }

        return string.Equals(parsedResourceId.ResourceType.ToString(), "Microsoft.Resources/deployments", StringComparisons.AzureResourceType);
    }

    private string? TryGetCurrentResourceLocationOverride(AzureBicepResource resource, string? environmentLocation)
    {
        var currentLocationValue = TryGetCurrentResourceLocation(resource);
        if (!string.IsNullOrWhiteSpace(currentLocationValue) &&
            (string.IsNullOrWhiteSpace(environmentLocation) ||
             !string.Equals(currentLocationValue, environmentLocation, StringComparisons.AzureLocation)))
        {
            // The dashboard snapshot is the most recent observed effective location. Prefer it when
            // deciding whether a reset should preserve a per-resource override.
            return currentLocationValue;
        }

        if (resource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.Location, out var parameterLocation) &&
            parameterLocation?.ToString() is { Length: > 0 } parameterLocationValue &&
            (string.IsNullOrWhiteSpace(environmentLocation) ||
             !string.Equals(parameterLocationValue, environmentLocation, StringComparisons.AzureLocation)))
        {
            // If the Bicep parameter is already different from the environment location, treat it as
            // an explicit per-resource setting that should survive reset/reprovision operations.
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
        if (TryGetExplicitLocationOverride(section) is { } locationOverride)
        {
            // Explicit overrides come from the Change location command and should survive even when
            // they match the current environment location.
            return locationOverride;
        }

        if (section.Data["Parameters"]?.GetValue<string>() is not { Length: > 0 } parametersJson)
        {
            return null;
        }

        try
        {
            var persistedLocation = AzureProvisioningJsonHelpers.ParseDeploymentStateJson(parametersJson)?[AzureBicepResource.KnownParameters.Location]?["value"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(persistedLocation))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(environmentLocation) ||
                !string.Equals(persistedLocation, environmentLocation, StringComparisons.AzureLocation))
            {
                // A resource can intentionally live in a different Azure region than the environment.
                // Preserve that persisted per-resource value across reprovisioning instead of replacing
                // it with the current environment location.
                return persistedLocation;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to parse persisted parameters while preserving Azure resource location overrides.");
        }

        return TryGetCurrentResourceLocationOverride(resource, environmentLocation);
    }

    private static string? TryGetExplicitLocationOverride(DeploymentStateSection section)
        => section.Data[LocationOverrideKey]?.GetValue<string>() is { Length: > 0 } locationOverride ? locationOverride : null;

    private static string NormalizeLocation(string location, IReadOnlyList<KeyValuePair<string, string>> locationOptions)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return location;
        }

        foreach (var option in locationOptions)
        {
            if (string.Equals(option.Key, location, StringComparisons.AzureLocation) ||
                string.Equals(option.Value, location, StringComparisons.AzureLocation))
            {
                // Prefer the option key because Azure SDK/Bicep APIs expect canonical location names
                // even though users often choose or type display names.
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
        // Last-resort normalization for manually typed display names when ARM enumeration is not
        // available. Azure canonical names are lowercase alphanumeric values such as "westus2".
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
        return await GetLocationOptionsAsync(subscriptionId: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<KeyValuePair<string, string>>> GetTenantOptionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var armClientProvider = serviceProvider.GetRequiredService<IArmClientProvider>();
            var tokenCredentialProvider = serviceProvider.GetRequiredService<ITokenCredentialProvider>();
            var armClient = armClientProvider.GetArmClient(tokenCredentialProvider.TokenCredential);

            return [.. (await armClient.GetAvailableTenantsAsync(cancellationToken).ConfigureAwait(false))
                .Select(static tenant =>
                {
                    var tenantId = tenant.TenantId?.ToString() ?? "";
                    var displayName = !string.IsNullOrEmpty(tenant.DisplayName)
                        ? tenant.DisplayName
                        : !string.IsNullOrEmpty(tenant.DefaultDomain)
                            ? tenant.DefaultDomain
                            : "Unknown";

                    var description = displayName;
                    if (!string.IsNullOrEmpty(tenant.DefaultDomain) &&
                        !string.Equals(tenant.DisplayName, tenant.DefaultDomain, StringComparison.Ordinal))
                    {
                        description += $" ({tenant.DefaultDomain})";
                    }

                    return KeyValuePair.Create(tenantId, $"{description} — {tenantId}");
                })
                .OrderBy(static option => option.Value)];
        }
        catch (Exception ex)
        {
            // Enumeration improves the dialog but is not required because Azure IDs are accepted as
            // custom choices. Log and keep the command usable in restricted/offline environments.
            _logger.LogWarning(ex, "Failed to enumerate Azure tenants for context selection.");
            return [];
        }
    }

    private async Task<IReadOnlyList<KeyValuePair<string, string>>> GetSubscriptionOptionsAsync(string? tenantId, CancellationToken cancellationToken)
    {
        try
        {
            var armClientProvider = serviceProvider.GetRequiredService<IArmClientProvider>();
            var tokenCredentialProvider = serviceProvider.GetRequiredService<ITokenCredentialProvider>();
            var armClient = armClientProvider.GetArmClient(tokenCredentialProvider.TokenCredential);

            return [.. (await armClient.GetAvailableSubscriptionsAsync(tenantId, cancellationToken).ConfigureAwait(false))
                .Select(static subscription => KeyValuePair.Create(
                    subscription.Id.SubscriptionId ?? "",
                    $"{subscription.DisplayName ?? subscription.Id.SubscriptionId} ({subscription.Id.SubscriptionId})"))
                .OrderBy(static option => option.Value)];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate Azure subscriptions for context selection.");
            return [];
        }
    }

    private async Task<IReadOnlyList<(string Name, string Location)>> GetResourceGroupOptionsAsync(string? subscriptionId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(subscriptionId, out _))
        {
            // Resource group enumeration requires a valid subscription ID. Return no options rather
            // than throwing so the dialog can still accept a manually entered resource group.
            return [];
        }

        try
        {
            var armClientProvider = serviceProvider.GetRequiredService<IArmClientProvider>();
            var tokenCredentialProvider = serviceProvider.GetRequiredService<ITokenCredentialProvider>();
            var armClient = armClientProvider.GetArmClient(tokenCredentialProvider.TokenCredential);

            return [.. await armClient.GetAvailableResourceGroupsWithLocationAsync(subscriptionId, cancellationToken).ConfigureAwait(false)];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate Azure resource groups for context selection.");
            return [];
        }
    }

    private async Task<IReadOnlyList<KeyValuePair<string, string>>> GetLocationOptionsAsync(string? subscriptionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
        {
            subscriptionId = (await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false)).SubscriptionId;
        }

        if (!Guid.TryParse(subscriptionId, out _))
        {
            // Location has a useful static fallback. Use it when the subscription is missing or
            // invalid so users can still pick common Azure regions before the context is complete.
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
            // Fall back to the AzureLocation catalog when ARM enumeration fails. This keeps the
            // location picker useful even if the user lacks permission to list locations.
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

        // Persisted dashboard/CLI choices have highest precedence. Options/configuration are
        // fallback defaults for first run or after reset, matching the provisioning context provider.
        return new AzureContextState(
            section.Data["SubscriptionId"]?.GetValue<string>() ?? provisionerOptions.Value.SubscriptionId ?? configuration["Azure:SubscriptionId"],
            section.Data["ResourceGroup"]?.GetValue<string>() ?? provisionerOptions.Value.ResourceGroup ?? configuration["Azure:ResourceGroup"],
            section.Data["Location"]?.GetValue<string>() ?? provisionerOptions.Value.Location ?? configuration["Azure:Location"],
            section.Data["TenantId"]?.GetValue<string>() ?? provisionerOptions.Value.TenantId ?? configuration["Azure:TenantId"],
            section.Data["Tenant"]?.GetValue<string>());
    }

    private bool ShouldCheckForDrift(IResource resource)
    {
        if (!notificationService.TryGetCurrentState(resource.Name, out var resourceEvent))
        {
            // No dashboard state means the resource has not been published yet, so a background ARM
            // probe would race startup rather than detecting meaningful drift.
            return false;
        }

        return resourceEvent.Snapshot.State?.Text == KnownResourceStates.Running;
    }

    private async Task<string?> TryGetResourceIdFromDeploymentStateAsync(AzureBicepResource resource, CancellationToken cancellationToken)
    {
        var section = await deploymentStateManager.AcquireSectionAsync($"Azure:Deployments:{resource.Name}", cancellationToken).ConfigureAwait(false);
        if (section.Data["Outputs"]?.GetValue<string>() is not { Length: > 0 } outputsJson)
        {
            // Resources that were never successfully deployed will not have an output ID. Treat that
            // as "no live resource to check" rather than a drift/delete failure.
            return null;
        }

        try
        {
            return AzureProvisioningJsonHelpers.ParseDeploymentStateJson(outputsJson)?["id"]?["value"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to parse cached outputs for resource {ResourceName} while checking for Azure drift.", resource.Name);
            return null;
        }
    }

    private async Task DeleteCachedResourceForLocationChangeAsync(AzureBicepResource resource, string requestedLocation, CancellationToken cancellationToken)
    {
        var currentLocation = TryGetCurrentResourceLocation(resource) ??
            await TryGetPersistedResourceLocationAsync(resource, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(currentLocation) ||
            string.Equals(currentLocation, requestedLocation, StringComparisons.AzureLocation))
        {
            // If the current location is unknown or already matches the requested location, there is
            // nothing safe or necessary to delete before reprovisioning.
            return;
        }

        if (await TryGetResourceIdFromDeploymentStateAsync(resource, cancellationToken).ConfigureAwait(false) is not { } resourceId)
        {
            // Without a cached resource ID we cannot target the old live resource. Let
            // reprovisioning proceed and surface any ARM conflict through the normal deployment path.
            return;
        }

        var context = await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(context.SubscriptionId, out _))
        {
            // Deleting for a location change is a best-effort preflight. If context is invalid, avoid
            // making a destructive call and let the subsequent provisioning validation report the
            // missing subscription configuration.
            return;
        }

        var armClientProvider = serviceProvider.GetRequiredService<IArmClientProvider>();
        var tokenCredentialProvider = serviceProvider.GetRequiredService<ITokenCredentialProvider>();
        var armClient = armClientProvider.GetArmClient(tokenCredentialProvider.TokenCredential, context.SubscriptionId);
        if (!await armClient.ResourceExistsAsync(resourceId, cancellationToken).ConfigureAwait(false))
        {
            // Cached state can point at a resource that has already been manually deleted. In that
            // case the location change only needs to update local override state and reprovision.
            return;
        }

        _logger.LogInformation(
            "Deleting Azure resource {ResourceId} before reprovisioning {ResourceName} from {CurrentLocation} to {RequestedLocation}.",
            resourceId,
            resource.Name,
            currentLocation,
            requestedLocation);

        try
        {
            await armClient.DeleteResourceAsync(resourceId, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogInformation(
                "Azure resource {ResourceId} was already absent before reprovisioning {ResourceName} from {CurrentLocation} to {RequestedLocation}.",
                resourceId,
                resource.Name,
                currentLocation,
                requestedLocation);
        }
    }

    private async Task<string?> TryGetPersistedResourceLocationAsync(AzureBicepResource resource, CancellationToken cancellationToken)
    {
        var section = await deploymentStateManager.AcquireSectionAsync($"Azure:Deployments:{resource.Name}", cancellationToken).ConfigureAwait(false);
        if (section.Data[LocationOverrideKey]?.GetValue<string>() is { Length: > 0 } locationOverride)
        {
            // The explicit override is the most reliable persisted effective location because it was
            // written by the controller rather than inferred from a deployment payload.
            return locationOverride;
        }

        if (section.Data["Parameters"]?.GetValue<string>() is not { Length: > 0 } parametersJson)
        {
            return null;
        }

        try
        {
            return AzureProvisioningJsonHelpers.ParseDeploymentStateJson(parametersJson)?[AzureBicepResource.KnownParameters.Location]?["value"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to parse persisted parameters while checking whether Azure resource {ResourceName} must be deleted for a location change.", resource.Name);
            return null;
        }
    }

    private async Task<bool> IsMissingCachedResourceAsync(AzureBicepResource resource, CancellationToken cancellationToken)
    {
        if (await TryGetResourceIdFromDeploymentStateAsync(resource, cancellationToken).ConfigureAwait(false) is not { } resourceId)
        {
            // No cached ID means there is no prior live resource to verify.
            return false;
        }

        var context = await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(context.SubscriptionId, out _))
        {
            // Missing context should fail during provisioning setup, not be interpreted as drift.
            return false;
        }

        try
        {
            var armClientProvider = serviceProvider.GetRequiredService<IArmClientProvider>();
            var tokenCredentialProvider = serviceProvider.GetRequiredService<ITokenCredentialProvider>();
            var armClient = armClientProvider.GetArmClient(tokenCredentialProvider.TokenCredential, context.SubscriptionId);
            return !await armClient.ResourceExistsAsync(resourceId, cancellationToken).ConfigureAwait(false);
        }
        catch (CredentialUnavailableException ex)
        {
            // Offline development should not clear otherwise valid cached state just because the
            // drift probe cannot authenticate.
            _logger.LogDebug(ex, "Unable to verify cached Azure resource state for {ResourceName} because no Azure credential is available.", resource.Name);
            return false;
        }
        catch (RequestFailedException ex)
        {
            // Treat probe failures as inconclusive. A transient ARM or authorization failure should
            // not force reprovisioning and potentially overwrite valid resources.
            _logger.LogDebug(ex, "Unable to verify cached Azure resource state for {ResourceName} because the Azure resource probe failed.", resource.Name);
            return false;
        }
    }

    private async Task ClearCachedDeploymentStateAsync(
        AzureBicepResource resource,
        bool preserveOverrides,
        string? environmentLocation,
        string? currentLocationOverride,
        bool preserveInferredLocationOverrides,
        CancellationToken cancellationToken)
    {
        var section = await deploymentStateManager.AcquireSectionAsync($"Azure:Deployments:{resource.Name}", cancellationToken).ConfigureAwait(false);
        var locationOverride = preserveOverrides
            ? TryGetExplicitLocationOverride(section) ?? (preserveInferredLocationOverrides
                ? currentLocationOverride ?? TryGetPreservedLocationOverride(resource, section, environmentLocation)
                : null)
            : null;

        // Clear all provisioner state in one place so outputs, deployment IDs, checksums, and old
        // parameters cannot leak into the next provisioning pass. Only the selected location override
        // is intentionally copied forward.
        section.Data.Clear();
        if (locationOverride is not null)
        {
            section.Data[LocationOverrideKey] = locationOverride;
            await deploymentStateManager.SaveSectionAsync(section, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Removing the section entirely keeps the deployment state store small and lets callers
            // distinguish "no state" from "state exists only for an override".
            await deploymentStateManager.DeleteSectionAsync(section, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshCommandStatesAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var resource in GetResourcesForCommandStateRefresh(model))
        {
            // Publishing the same state is intentional: command availability is calculated during
            // snapshot publication, so a no-op state update still refreshes dashboard command state.
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
            // Include both the visible resource and its Azure provisioning surrogate. Some command
            // annotations are attached to one while dashboard state may be keyed by the other.
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

        // Some model resources are represented by a surrogate AzureBicepResource during
        // provisioning. Publish to both so CLI wait/dashboard state stays consistent whether
        // callers address the visible resource or the Azure resource used by the provisioner.
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
                    // Walk descendants without recursion so deeply nested resource graphs do not
                    // risk stack growth while publishing a broad parent update.
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

            // ARM deployment completion only means the resources exist. Role assignment
            // propagation can lag, so do not mark the resource Running until the assigned
            // principals can actually use the provisioned resource.
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
        catch (Exception ex)
        {
            var failureDetails = AzureProvisioningFailureDetails.TryCreate(ex, AzureProvisioningFailureDetails.ProvisionOperation);
            await PublishUpdateToResourceTreeAsync(resource, parentChildLookup, state => state with
            {
                State = new("Failed to Provision", KnownResourceStateStyles.Error),
                Properties = failureDetails is null
                    ? state.Properties
                    : failureDetails.SetResourceProperties(state.Properties, AzureProvisioningFailureDetails.ProvisionOperation)
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
                    // A resource can depend on role assignments that are modeled as separate Azure
                    // resources. Wait for those provisioning TCSs before marking the main resource
                    // running so dependent app code does not race RBAC propagation.
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
        // The Lazy prevents Azure context creation until at least one resource actually needs ARM
        // provisioning; resources satisfied from existing configuration/user secrets can complete
        // without forcing Azure authentication.
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
        var resourceLogger = loggerService.GetLogger(resource.AzureResource);

        try
        {
            var beforeResourceStartedEvent = new BeforeResourceStartedEvent(resource.Resource, serviceProvider);
            await eventing.PublishAsync(beforeResourceStartedEvent, cancellationToken).ConfigureAwait(false);

            if (resource.AzureResource is not AzureBicepResource bicepResource)
            {
                // Non-Bicep Azure resources are not deployed by this controller, but they may still
                // appear in dependency graphs. Complete their TCS so downstream resources do not wait
                // forever for a deployment that will never run here.
                CompleteProvisioning(resource.AzureResource);
                resourceLogger.LogInformation("Skipping {resourceName} because it is not a Bicep resource.", resource.AzureResource.Name);
                return;
            }

            if (bicepResource.IsContainer() || bicepResource.IsEmulator())
            {
                // Local emulators/container-backed resources are represented by Azure resource types
                // but are started by DCP/container orchestration, not ARM provisioning.
                CompleteProvisioning(resource.AzureResource);
                resourceLogger.LogInformation("Skipping {resourceName} because it is not configured to be provisioned.", resource.AzureResource.Name);
            }
            else
            {
                var executionContext = serviceProvider.GetRequiredService<DistributedApplicationExecutionContext>();
                await WaitForProvisioningDependenciesAsync(bicepResource, executionContext, cancellationToken).ConfigureAwait(false);

                if (await IsMissingCachedResourceAsync(bicepResource, cancellationToken).ConfigureAwait(false))
                {
                    // Cached state can survive a user deleting the Azure resource outside Aspire.
                    // Clear it before ConfigureResourceAsync so the provisioner creates a fresh
                    // deployment rather than trusting outputs that point at a missing resource.
                    resourceLogger.LogWarning("Cached Azure deployment state for {resourceName} points to a missing Azure resource. Reprovisioning.", resource.AzureResource.Name);
                    await ClearCachedDeploymentStateAsync(
                        bicepResource,
                        preserveOverrides: true,
                        environmentLocation: (await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false)).Location,
                        currentLocationOverride: null,
                        preserveInferredLocationOverrides: true,
                        cancellationToken).ConfigureAwait(false);
                }

                if (await bicepProvisioner.ConfigureResourceAsync(bicepResource, cancellationToken).ConfigureAwait(false))
                {
                    // ConfigureResourceAsync returns true when existing local configuration is enough
                    // to satisfy the resource. Complete the TCS and publish connection-string events
                    // without creating or touching ARM resources.
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

                    var provisioningContext = await provisioningContextLazy.Value.ConfigureAwait(false);

                    // The provisioner owns Bicep compilation, state persistence, and ARM operations.
                    // The controller is responsible for sequencing, cancellation, and publishing the
                    // resource lifecycle around this call.
                    await bicepProvisioner.GetOrCreateResourceAsync(
                        bicepResource,
                        provisioningContext,
                        cancellationToken).ConfigureAwait(false);

                    CompleteProvisioning(resource.AzureResource);
                    await PublishConnectionStringAvailableEventAsync(resource.Resource, parentChildLookup, cancellationToken).ConfigureAwait(false);
                }
            }
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
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested || ex.CancellationToken == cancellationToken)
        {
            CancelProvisioning(resource.AzureResource, cancellationToken.IsCancellationRequested ? cancellationToken : ex.CancellationToken);
        }
        catch (Exception ex)
        {
            resourceLogger.LogError(ex, "Error provisioning {ResourceName}.", resource.AzureResource.Name);
            FailProvisioning(resource.AzureResource, new InvalidOperationException($"Unable to provision {resource.AzureResource.Name}.", ex));
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

    private static void CancelProvisioning(IAzureResource resource, CancellationToken cancellationToken)
    {
        resource.ProvisioningTaskCompletionSource?.TrySetCanceled(cancellationToken);
    }

    private static async Task WaitForProvisioningDependenciesAsync(
        AzureBicepResource resource,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        // Force template generation before dependency discovery. Some resources populate parameter
        // values and references lazily while generating Bicep.
        _ = resource.GetBicepTemplateString();

        var dependencies = new HashSet<IAzureResource>();
        var discoveredDependencies = await resource.GetResourceDependenciesAsync(
            executionContext,
            ResourceDependencyDiscoveryMode.Recursive,
            cancellationToken).ConfigureAwait(false);

        dependencies.UnionWith(discoveredDependencies.OfType<IAzureResource>());

        foreach (var parameter in resource.Parameters.Values)
        {
            // Parameters and references can contain nested IValueWithReferences instances that are
            // not visible from the app model graph. Include them so provisioning waits for resources
            // referenced only through generated Bicep values.
            CollectProvisioningDependencies(dependencies, parameter);
        }

        foreach (var reference in resource.References)
        {
            CollectProvisioningDependencies(dependencies, reference);
        }

        await Task.WhenAll(dependencies
            .Where(dependency => !ReferenceEquals(dependency, resource))
            .Select(dependency => dependency.ProvisioningTaskCompletionSource?.Task.WaitAsync(cancellationToken))
            .OfType<Task>()).ConfigureAwait(false);
    }

    private static void CollectProvisioningDependencies(HashSet<IAzureResource> dependencies, object? value)
    {
        CollectProvisioningDependencies(dependencies, value, []);
    }

    private static void CollectProvisioningDependencies(HashSet<IAzureResource> dependencies, object? value, HashSet<object> visited)
    {
        if (value is null || !visited.Add(value))
        {
            // Values can reference each other. Track visited objects by reference to avoid infinite
            // recursion while walking IValueWithReferences graphs.
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
            // Connection string events unblock resources that resolve connection strings lazily after
            // Azure outputs are available.
            var connectionStringAvailableEvent = new ConnectionStringAvailableEvent(targetResource, serviceProvider);
            await eventing.PublishAsync(connectionStringAvailableEvent, cancellationToken).ConfigureAwait(false);
        }

        if (parentChildLookup[targetResource] is { } children)
        {
            foreach (var child in children.OfType<IResourceWithConnectionString>().Where(static c => c is IResourceWithParent))
            {
                // Child resources such as databases often derive their connection strings from a
                // parent Azure account. Recurse so children observe the same availability signal.
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

        return [.. properties.Where(static property =>
            !s_resettableProperties.Contains(property.Name, StringComparer.Ordinal) &&
            !AzureProvisioningFailureDetails.IsFailureProperty(property.Name))];
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
        CancellationToken cancellationToken)
    {
        if (model.Resources.OfType<AzureEnvironmentResource>().SingleOrDefault() is not { } azureEnvironmentResource)
        {
            return;
        }

        var azureEnvironmentProperties = state.Text == KnownResourceStates.NotStarted
            ? ImmutableArray<ResourcePropertySnapshot>.Empty
            : BuildAzureEnvironmentProperties(await GetCurrentAzureContextAsync(cancellationToken).ConfigureAwait(false));

        // NotStarted represents "no active Azure context for this run" after reset/delete/forget.
        // Strip Azure-specific properties and URLs so the dashboard does not show stale subscription,
        // resource group, or portal links from the previous context.
        await notificationService.PublishUpdateAsync(azureEnvironmentResource, existingState => existingState with
        {
            State = state,
            Properties = state.Text == KnownResourceStates.NotStarted
                ? FilterProperties(existingState.Properties)
                : FilterProperties(existingState.Properties).SetResourcePropertyRange(azureEnvironmentProperties),
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

    private static ImmutableArray<ResourcePropertySnapshot> BuildAzureEnvironmentProperties(AzureContextState context)
    {
        return AzureResourceProperties.CreateContextProperties(
            context.SubscriptionId,
            context.ResourceGroup,
            context.TenantId,
            context.TenantDomain,
            context.Location);
    }

    private sealed class AzureOperationState(string displayName, bool isAllResources, IReadOnlySet<string> resourceNames)
    {
        // Read under _operationStateLock on the hot command-state path, so it holds only what command
        // enablement needs: target resource names and whether the operation affects all resources.
        public string DisplayName { get; } = displayName;
        public bool IsAllResources { get; } = isAllResources;
        public IReadOnlySet<string> ResourceNames { get; } = resourceNames;

        public static AzureOperationState None { get; } = new(string.Empty, false, new HashSet<string>(StringComparers.ResourceName));

        public static AzureOperationState All(string displayName) => new(displayName, true, new HashSet<string>(StringComparers.ResourceName));

        public static AzureOperationState Resource(string resourceName, string displayName) => new(displayName, false, new HashSet<string>([resourceName], StringComparers.ResourceName));
    }

    private sealed record AzureControllerState(AzureControllerStatus Status)
    {
        public static AzureControllerState Empty { get; } = new(new AzureControllerStatus(null));
    }

    private sealed record AzureControllerStatus(AzureIntent? CurrentIntent);

    private sealed record DeleteAzureResourceResult(IReadOnlyList<string> ResourceIds);

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
        GetAzureResource,
        CancelDeployment,
        DeleteAzureResource,
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
        bool IsHighlighted,
        Func<AzureProvisioningController, ExecuteCommandContext, Task<ExecuteCommandResult>> ExecuteCommand,
        IReadOnlyList<InteractionInput>? Arguments = null,
        Func<InputsDialogValidationContext, Task>? ValidateArguments = null);

    internal sealed record ResourceCommandDefinition(
        AzureResourceCommand Command,
        string Name,
        string DisplayName,
        string Description,
        string? ConfirmationMessage,
        string IconName,
        IconVariant IconVariant,
        bool IsHighlighted,
        Func<AzureProvisioningController, string, ExecuteCommandContext, Task<ExecuteCommandResult>> ExecuteCommand,
        IReadOnlyList<InteractionInput>? Arguments = null,
        Func<InputsDialogValidationContext, Task>? ValidateArguments = null);

    // Intents are the messages consumed by the single-reader operation loop. Keeping them as records
    // makes each queued operation self-describing for command-state calculation and execution.
    private abstract record AzureIntent(AzureOperationState Operation);

    private sealed record ResetStateIntent(bool ReprovisionAfterReset) : AzureIntent(AzureOperationState.All("Reset provisioning state"));

    private sealed record ChangeAzureContextIntent(AzureProvisioningOptionsUpdate? Options) : AzureIntent(AzureOperationState.All("Change Azure context"));

    private sealed record ApplyAzureContextIntent() : AzureIntent(AzureOperationState.All("Configure Azure context"));

    private sealed record EnsureProvisionedIntent() : AzureIntent(AzureOperationState.All("Provision Azure resources"));

    private sealed record ReprovisionAllIntent() : AzureIntent(AzureOperationState.All("Reprovision all Azure resources"));

    private sealed record DeleteAzureResourcesIntent() : AzureIntent(AzureOperationState.All("Delete Azure resources"));

    private sealed record ForgetResourceStateIntent(string ResourceName) : AzureIntent(AzureOperationState.Resource(ResourceName, "Reset provisioning state"));

    private sealed record ChangeResourceLocationIntent(string ResourceName, string Location) : AzureIntent(AzureOperationState.Resource(ResourceName, "Change Azure resource location"));

    private sealed record ReprovisionResourceIntent(string ResourceName) : AzureIntent(AzureOperationState.Resource(ResourceName, "Reprovision Azure resource"));

    private sealed record CancelResourceDeploymentIntent(string ResourceName) : AzureIntent(AzureOperationState.Resource(ResourceName, "Cancel Azure deployment"));

    private sealed record DeleteAzureResourceIntent(string ResourceName) : AzureIntent(AzureOperationState.Resource(ResourceName, "Delete Azure resource"));

    // Drift checks use AzureOperationState.None so they serialize with commands without disabling
    // dashboard command buttons.
    private sealed record DetectDriftIntent() : AzureIntent(AzureOperationState.None);

    private sealed record QueuedOperation(
        DistributedApplicationModel Model,
        AzureIntent Intent,
        TaskCompletionSource<object?> Completion,
        CancellationToken CancellationToken);

    private sealed record AzureContextState(string? SubscriptionId, string? ResourceGroup, string? Location, string? TenantId, string? TenantDomain);
}
