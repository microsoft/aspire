#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using Aspire.Hosting.Azure.Resources;
using Aspire.Hosting.Azure.Utils;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

/// <summary>
/// Run mode implementation of <see cref="IProvisioningContextProvider"/>.
/// </summary>
internal sealed class RunModeProvisioningContextProvider(
    IInteractionService interactionService,
    IOptions<AzureProvisionerOptions> options,
    IHostEnvironment environment,
    ILogger<RunModeProvisioningContextProvider> logger,
    IArmClientProvider armClientProvider,
    IUserPrincipalProvider userPrincipalProvider,
    ITokenCredentialProvider tokenCredentialProvider,
    IDeploymentStateManager deploymentStateManager,
    DistributedApplicationExecutionContext distributedApplicationExecutionContext) : BaseProvisioningContextProvider(
        interactionService,
        options,
        environment,
        logger,
        armClientProvider,
        userPrincipalProvider,
        tokenCredentialProvider,
        deploymentStateManager,
        distributedApplicationExecutionContext), IAzureProvisioningOptionsManager
{
    private readonly SemaphoreSlim _provisioningOptionsLock = new(1, 1);

    protected override string GetDefaultResourceGroupName()
    {
        var prefix = "rg-aspire";

        if (!string.IsNullOrWhiteSpace(_options.ResourceGroupPrefix))
        {
            prefix = _options.ResourceGroupPrefix;
        }

        var suffix = RandomNumberGenerator.GetHexString(8, lowercase: true);

        var maxApplicationNameSize = ResourceGroupNameHelpers.MaxResourceGroupNameLength - prefix.Length - suffix.Length - 2; // extra '-'s

        var normalizedApplicationName = ResourceGroupNameHelpers.NormalizeResourceGroupName(_environment.ApplicationName.ToLowerInvariant());
        if (normalizedApplicationName.Length > maxApplicationNameSize)
        {
            normalizedApplicationName = normalizedApplicationName[..maxApplicationNameSize];
        }

        // Run mode always includes random suffix for uniqueness
        return $"{prefix}-{normalizedApplicationName}-{suffix}";
    }

    public async Task<bool> EnsureProvisioningOptionsAsync(bool forcePrompt, CancellationToken cancellationToken = default)
    {
        await RehydrateProvisioningOptionsAsync(cancellationToken).ConfigureAwait(false);

        if (!_interactionService.IsAvailable)
        {
            if (forcePrompt)
            {
                throw new MissingConfigurationException("Azure provisioning options can't be changed because the interaction service is unavailable.");
            }

            return HasProvisioningOptions();
        }

        if (!forcePrompt && HasProvisioningOptions())
        {
            return true;
        }

        await _provisioningOptionsLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await RehydrateProvisioningOptionsAsync(cancellationToken).ConfigureAwait(false);

            if (!forcePrompt && HasProvisioningOptions())
            {
                return true;
            }

            var result = await RetrieveAzureProvisioningOptionsAsync(forcePrompt, cancellationToken).ConfigureAwait(false);
            if (result)
            {
                _logger.LogDebug("Azure provisioning options have been handled successfully.");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve Azure provisioning options.");
            throw;
        }
        finally
        {
            _provisioningOptionsLock.Release();
        }
    }

    public override async Task<ProvisioningContext> CreateProvisioningContextAsync(CancellationToken cancellationToken = default)
    {
        await RehydrateProvisioningOptionsAsync(cancellationToken).ConfigureAwait(false);

        var result = await EnsureProvisioningOptionsAsync(forcePrompt: false, cancellationToken).ConfigureAwait(false);
        if (!result)
        {
            if (!_interactionService.IsAvailable)
            {
                return await base.CreateProvisioningContextAsync(cancellationToken).ConfigureAwait(false);
            }

            throw new MissingConfigurationException("Azure provisioning options were not provided.");
        }

        return await base.CreateProvisioningContextAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PersistProvisioningOptionsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_options.ResourceGroup))
        {
            _options.ResourceGroup = GetDefaultResourceGroupName();
            _options.AllowResourceGroupCreation ??= true;
        }

        await SaveProvisioningOptionsAsync(_options.ResourceGroup, cancellationToken).ConfigureAwait(false);
    }

    private bool HasProvisioningOptions() =>
        !string.IsNullOrEmpty(_options.Location) &&
        !string.IsNullOrEmpty(_options.SubscriptionId);

    private async Task RehydrateProvisioningOptionsAsync(CancellationToken cancellationToken)
    {
        var azureSection = await _deploymentStateManager.AcquireSectionAsync("Azure", cancellationToken).ConfigureAwait(false);

        _options.ResourceGroupPrefix = _configuredOptions.ResourceGroupPrefix;
        _options.AllowResourceGroupCreation = _configuredOptions.AllowResourceGroupCreation;
        _options.Location = _configuredOptions.Location;
        _options.SubscriptionId = _configuredOptions.SubscriptionId;
        _options.ResourceGroup = _configuredOptions.ResourceGroup;
        _options.TenantId = _configuredOptions.TenantId;

        var data = azureSection.Data;
        if (data["Location"]?.GetValue<string>() is { Length: > 0 } location)
        {
            _options.Location = location;
        }

        if (data["SubscriptionId"]?.GetValue<string>() is { Length: > 0 } subscriptionId)
        {
            _options.SubscriptionId = subscriptionId;
        }

        if (data["ResourceGroup"]?.GetValue<string>() is { Length: > 0 } resourceGroup)
        {
            _options.ResourceGroup = resourceGroup;
        }

        if (data["TenantId"]?.GetValue<string>() is { Length: > 0 } tenantId)
        {
            _options.TenantId = tenantId;
        }

        if (data["AllowResourceGroupCreation"]?.GetValue<bool?>() is bool allowResourceGroupCreation)
        {
            _options.AllowResourceGroupCreation = allowResourceGroupCreation;
        }
    }

    private async Task<bool> RetrieveAzureProvisioningOptionsAsync(bool forcePrompt, CancellationToken cancellationToken = default)
    {
        while (forcePrompt || _options.Location == null || _options.SubscriptionId == null)
        {
            var shouldPrompt = true;
            if (!forcePrompt)
            {
                var messageBarResult = await _interactionService.PromptNotificationAsync(
                     AzureProvisioningStrings.NotificationTitle,
                     AzureProvisioningStrings.NotificationMessage,
                     new NotificationInteractionOptions
                     {
                         Intent = MessageIntent.Warning,
                         PrimaryButtonText = AzureProvisioningStrings.NotificationPrimaryButtonText
                     },
                     cancellationToken)
                     .ConfigureAwait(false);

                if (messageBarResult.Canceled)
                {
                    return false;
                }

                shouldPrompt = messageBarResult.Data;
            }

            if (shouldPrompt)
            {
                var inputs = CreateProvisioningInputs(forcePrompt, cancellationToken);
                var result = await _interactionService.PromptInputsAsync(
                    AzureProvisioningStrings.InputsTitle,
                    AzureProvisioningStrings.InputsMessage,
                    inputs,
                    new InputsDialogInteractionOptions
                    {
                        EnableMessageMarkdown = true,
                        ValidationCallback = (validationContext) =>
                        {
                            // Only validate tenant if it's included in the inputs
                            if (validationContext.Inputs.TryGetByName(TenantName, out var tenantInput))
                            {
                                if (!string.IsNullOrWhiteSpace(tenantInput.Value) && !Guid.TryParse(tenantInput.Value, out _))
                                {
                                    validationContext.AddValidationError(tenantInput, AzureProvisioningStrings.ValidationTenantIdInvalid);
                                }
                            }

                            var subscriptionInput = validationContext.Inputs[SubscriptionIdName];
                            if (!string.IsNullOrWhiteSpace(subscriptionInput.Value) && !Guid.TryParse(subscriptionInput.Value, out _))
                            {
                                validationContext.AddValidationError(subscriptionInput, AzureProvisioningStrings.ValidationSubscriptionIdInvalid);
                            }

                            var resourceGroupInput = validationContext.Inputs[ResourceGroupName];
                            if (!IsValidResourceGroupName(resourceGroupInput.Value))
                            {
                                validationContext.AddValidationError(resourceGroupInput, AzureProvisioningStrings.ValidationResourceGroupNameInvalid);
                            }

                            return Task.CompletedTask;
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                if (result.Canceled)
                {
                    return false;
                }

                ApplyProvisioningInputs(result.Data);
                return true;
            }

            if (!forcePrompt)
            {
                return false;
            }

            forcePrompt = false;
        }

        return true;
    }

    private List<InteractionInput> CreateProvisioningInputs(bool forcePrompt, CancellationToken cancellationToken)
    {
        var inputs = new List<InteractionInput>();
        var includeTenantInput = forcePrompt || string.IsNullOrEmpty(_options.SubscriptionId);

        if (includeTenantInput)
        {
            inputs.Add(new InteractionInput
            {
                Name = TenantName,
                InputType = InputType.Choice,
                Label = AzureProvisioningStrings.TenantLabel,
                Required = true,
                AllowCustomChoice = true,
                Placeholder = AzureProvisioningStrings.TenantPlaceholder,
                Value = _options.TenantId,
                DynamicLoading = new InputLoadOptions
                {
                    AlwaysLoadOnStart = true,
                    LoadCallback = async (context) =>
                    {
                        var (tenantOptions, fetchSucceeded) =
                            await TryGetTenantsAsync(cancellationToken).ConfigureAwait(false);

                        context.Input.Options = fetchSucceeded
                            ? tenantOptions!
                            : [];
                    }
                }
            });
        }

        var allowSubscriptionEdit = forcePrompt || string.IsNullOrEmpty(_options.SubscriptionId);
        inputs.Add(new InteractionInput
        {
            Name = SubscriptionIdName,
            InputType = allowSubscriptionEdit ? InputType.Choice : InputType.Text,
            Label = AzureProvisioningStrings.SubscriptionIdLabel,
            Required = true,
            AllowCustomChoice = true,
            Placeholder = AzureProvisioningStrings.SubscriptionIdPlaceholder,
            Disabled = includeTenantInput || !allowSubscriptionEdit,
            Value = _options.SubscriptionId,
            DynamicLoading = allowSubscriptionEdit
                ? new InputLoadOptions
                {
                    AlwaysLoadOnStart = !includeTenantInput,
                    LoadCallback = async (context) =>
                    {
                        var tenantId = includeTenantInput && context.AllInputs.TryGetByName(TenantName, out var tenantInput)
                            ? tenantInput.Value
                            : _options.TenantId;

                        var (subscriptionOptions, fetchSucceeded) =
                            await TryGetSubscriptionsAsync(tenantId, cancellationToken).ConfigureAwait(false);

                        context.Input.Options = fetchSucceeded
                            ? subscriptionOptions!
                            : [];
                        context.Input.Disabled = false;
                    },
                    DependsOnInputs = includeTenantInput ? [TenantName] : []
                }
                : null
        });

        var defaultResourceGroupNameSet = false;
        var useTextResourceGroupInput = forcePrompt;
        inputs.Add(new InteractionInput
        {
            Name = ResourceGroupName,
            InputType = useTextResourceGroupInput ? InputType.Text : InputType.Choice,
            Label = AzureProvisioningStrings.ResourceGroupLabel,
            Placeholder = AzureProvisioningStrings.ResourceGroupPlaceholder,
            AllowCustomChoice = !useTextResourceGroupInput,
            Disabled = false,
            Value = _options.ResourceGroup ?? (useTextResourceGroupInput ? GetDefaultResourceGroupName() : null),
            DynamicLoading = useTextResourceGroupInput
                ? null
                : new InputLoadOptions
                {
                    AlwaysLoadOnStart = true,
                    LoadCallback = async (context) =>
                    {
                        var subscriptionId = context.AllInputs[SubscriptionIdName].Value ?? string.Empty;

                        var (resourceGroupOptions, fetchSucceeded) = await TryGetResourceGroupsWithLocationAsync(subscriptionId, cancellationToken).ConfigureAwait(false);

                        if (fetchSucceeded && resourceGroupOptions is not null)
                        {
                            context.Input.Options = resourceGroupOptions.Select(rg => KeyValuePair.Create(rg.Name, rg.Name)).ToList();
                        }
                        else
                        {
                            context.Input.Options = [];

                            if (string.IsNullOrEmpty(context.Input.Value) && !defaultResourceGroupNameSet)
                            {
                                context.Input.Value = GetDefaultResourceGroupName();
                                defaultResourceGroupNameSet = true;
                            }
                        }

                        context.Input.Disabled = false;
                    },
                    DependsOnInputs = [SubscriptionIdName]
                }
        });

        inputs.Add(new InteractionInput
        {
            Name = LocationName,
            InputType = InputType.Choice,
            Label = AzureProvisioningStrings.LocationLabel,
            Placeholder = AzureProvisioningStrings.LocationPlaceholder,
            Required = true,
            Disabled = false,
            Value = _options.Location,
            DynamicLoading = new InputLoadOptions
            {
                AlwaysLoadOnStart = true,
                LoadCallback = async (context) =>
                {
                    var subscriptionId = context.AllInputs[SubscriptionIdName].Value ?? string.Empty;
                    var resourceGroupName = context.AllInputs[ResourceGroupName].Value ?? string.Empty;

                    var (resourceGroupOptions, fetchSucceeded) = await TryGetResourceGroupsWithLocationAsync(subscriptionId, cancellationToken).ConfigureAwait(false);

                    if (fetchSucceeded && resourceGroupOptions is not null)
                    {
                        var (_, resourceGroupLocation) = resourceGroupOptions.FirstOrDefault(rg => rg.Name.Equals(resourceGroupName, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(resourceGroupLocation))
                        {
                            context.Input.Options = [KeyValuePair.Create(resourceGroupLocation, resourceGroupLocation)];
                            context.Input.Value = resourceGroupLocation;
                            context.Input.Disabled = true;
                            return;
                        }
                    }

                    var (locationOptions, _) = await TryGetLocationsAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
                    context.Input.Options = locationOptions;
                    context.Input.Disabled = false;
                },
                DependsOnInputs = [SubscriptionIdName, ResourceGroupName]
            }
        });

        return inputs;
    }

    private void ApplyProvisioningInputs(InteractionInputCollection inputs)
    {
        if (inputs.TryGetByName(TenantName, out var tenantInput))
        {
            _options.TenantId = tenantInput.Value;
        }

        _options.Location = inputs[LocationName].Value;
        _options.SubscriptionId = inputs[SubscriptionIdName].Value;
        _options.ResourceGroup = inputs[ResourceGroupName].Value;
        _options.AllowResourceGroupCreation = true;
    }
}
