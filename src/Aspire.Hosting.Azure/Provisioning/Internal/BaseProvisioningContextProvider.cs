#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.Pipelines;
using Azure;
using Azure.Core;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

/// <summary>
/// Base implementation for <see cref="IProvisioningContextProvider"/> that provides common functionality.
/// </summary>
internal abstract partial class BaseProvisioningContextProvider(
    IInteractionService interactionService,
    IOptions<AzureProvisionerOptions> options,
    IHostEnvironment environment,
    ILogger logger,
    IArmClientProvider armClientProvider,
    IUserPrincipalProvider userPrincipalProvider,
    ITokenCredentialProvider tokenCredentialProvider,
    IDeploymentStateManager deploymentStateManager,
    DistributedApplicationExecutionContext distributedApplicationExecutionContext) : IProvisioningContextProvider
{
    internal const string LocationName = "Location";
    internal const string SubscriptionIdName = "SubscriptionId";
    internal const string ResourceGroupName = "ResourceGroup";
    internal const string TenantName = "Tenant";

    protected readonly IInteractionService _interactionService = interactionService;
    protected readonly AzureProvisionerOptions _options = options.Value;
    protected readonly IHostEnvironment _environment = environment;
    protected readonly ILogger _logger = logger;
    protected readonly IArmClientProvider _armClientProvider = armClientProvider;
    protected readonly IUserPrincipalProvider _userPrincipalProvider = userPrincipalProvider;
    protected readonly ITokenCredentialProvider _tokenCredentialProvider = tokenCredentialProvider;
    protected readonly DistributedApplicationExecutionContext _distributedApplicationExecutionContext = distributedApplicationExecutionContext;

    [GeneratedRegex(@"^[a-zA-Z0-9_\-\.\(\)]+$")]
    private static partial Regex ResourceGroupValidCharacters();

    protected static bool IsValidResourceGroupName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 90)
        {
            return false;
        }

        // Only allow valid characters - letters, digits, underscores, hyphens, periods, and parentheses
        if (!ResourceGroupValidCharacters().IsMatch(name))
        {
            return false;
        }

        // Must start with a letter
        if (!char.IsLetter(name[0]))
        {
            return false;
        }

        // Cannot end with a period
        if (name.EndsWith('.'))
        {
            return false;
        }

        // No consecutive periods
        return !name.Contains("..");
    }

    public virtual async Task<ProvisioningContext> CreateProvisioningContextAsync(CancellationToken cancellationToken = default)
    {
        var subscriptionId = _options.SubscriptionId ?? throw new MissingConfigurationException("An Azure subscription id is required. Set the Azure:SubscriptionId configuration value.");

        var credential = _tokenCredentialProvider.TokenCredential;

        if (_tokenCredentialProvider is DefaultTokenCredentialProvider defaultProvider)
        {
            defaultProvider.LogCredentialType();
        }

        var armClient = _armClientProvider.GetArmClient(credential, subscriptionId);

        var (subscriptionResource, tenantResource) = await armClient.GetSubscriptionAndTenantAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Default subscription: {name} ({subscriptionId})", subscriptionResource.DisplayName, subscriptionResource.Id);
        _logger.LogInformation("Tenant: {tenantId}", tenantResource.TenantId);

        if (string.IsNullOrEmpty(_options.Location))
        {
            throw new MissingConfigurationException("An azure location/region is required. Set the Azure:Location configuration value.");
        }

        // Acquire Azure state section for reading/writing configuration
        var azureStateSection = await deploymentStateManager.AcquireSectionAsync("Azure", cancellationToken).ConfigureAwait(false);

        string resourceGroupName;
        bool createIfAbsent;

        if (string.IsNullOrEmpty(_options.ResourceGroup))
        {
            // Generate an resource group name since none was provided
            // Create a unique resource group name and save it in deployment state
            resourceGroupName = GetDefaultResourceGroupName();

            createIfAbsent = true;

            azureStateSection.Data["ResourceGroup"] = resourceGroupName;
        }
        else
        {
            resourceGroupName = _options.ResourceGroup;
            createIfAbsent = _options.AllowResourceGroupCreation ?? false;
        }

        // In publish mode, always allow resource group creation
        if (_distributedApplicationExecutionContext.IsPublishMode)
        {
            createIfAbsent = true;
            _options.AllowResourceGroupCreation = true;
        }

        var resourceGroups = subscriptionResource.GetResourceGroups();

        IResourceGroupResource? resourceGroup;

        var location = new AzureLocation(_options.Location);
        try
        {
            var response = await resourceGroups.GetAsync(resourceGroupName, cancellationToken).ConfigureAwait(false);
            resourceGroup = response.Value;

            _logger.LogInformation("Using existing resource group {rgName}.", resourceGroup.Name);
        }
        catch (Exception)
        {
            if (!createIfAbsent)
            {
                throw;
            }

            // REVIEW: Is it possible to do this without an exception?

            _logger.LogInformation("Creating resource group {rgName} in {location}...", resourceGroupName, location);

            var rgData = new ResourceGroupData(location);
            rgData.Tags.Add("aspire", "true");

            try
            {
                var operation = await resourceGroups.CreateOrUpdateAsync(WaitUntil.Completed, resourceGroupName, rgData, cancellationToken).ConfigureAwait(false);
                resourceGroup = operation.Value;
            }
            catch (RequestFailedException createEx)
            {
                var errorMessage = AzureBicepResource.ExtractDetailedErrorMessage(createEx);
                throw new ProvisioningFailedException($"Failed to create resource group '{resourceGroupName}': {errorMessage}", createEx);
            }

            _logger.LogInformation("Resource group {rgName} created.", resourceGroup.Name);
        }

        var principal = await _userPrincipalProvider.GetUserPrincipalAsync(cancellationToken).ConfigureAwait(false);

        // Persist the provisioning options to deployment state so they can be reused in the future
        var azureSection = azureStateSection.Data;
        azureSection["Location"] = _options.Location;
        azureSection["SubscriptionId"] = _options.SubscriptionId;
        azureSection["ResourceGroup"] = resourceGroupName;
        if (!string.IsNullOrEmpty(_options.TenantId))
        {
            azureSection["TenantId"] = _options.TenantId;
        }
        if (_options.AllowResourceGroupCreation.HasValue)
        {
            azureSection["AllowResourceGroupCreation"] = _options.AllowResourceGroupCreation.Value;
        }

        await deploymentStateManager.SaveSectionAsync(azureStateSection, cancellationToken).ConfigureAwait(false);

        return new ProvisioningContext(
                    credential,
                    armClient,
                    subscriptionResource,
                    resourceGroup,
                    tenantResource,
                    location,
                    principal,
                    _distributedApplicationExecutionContext);
    }

    protected abstract string GetDefaultResourceGroupName();

    protected async Task<(List<KeyValuePair<string, string>>? tenantOptions, bool fetchSucceeded)> TryGetTenantsAsync(CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string>>? tenantOptions = null;
        var fetchSucceeded = false;

        try
        {
            var credential = _tokenCredentialProvider.TokenCredential;
            var armClient = _armClientProvider.GetArmClient(credential);
            var availableTenants = await armClient.GetAvailableTenantsAsync(cancellationToken).ConfigureAwait(false);
            var tenantList = availableTenants.ToList();

            if (tenantList.Count > 0)
            {
                tenantOptions = tenantList
                    .Select(t =>
                    {
                        var tenantId = t.TenantId?.ToString() ?? "";

                        // Build display name: prefer DisplayName, fall back to domain, then to "Unknown"
                        var displayName = !string.IsNullOrEmpty(t.DisplayName)
                            ? t.DisplayName
                            : !string.IsNullOrEmpty(t.DefaultDomain)
                                ? t.DefaultDomain
                                : "Unknown";

                        // Build full description
                        var description = displayName;
                        if (!string.IsNullOrEmpty(t.DefaultDomain) && t.DisplayName != t.DefaultDomain)
                        {
                            description += $" ({t.DefaultDomain})";
                        }
                        description += $" — {tenantId}";

                        return KeyValuePair.Create(tenantId, description);
                    })
                    .OrderBy(kvp => kvp.Value)
                    .ToList();
                fetchSucceeded = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate available tenants. Falling back to manual input.");
        }

        return (tenantOptions, fetchSucceeded);
    }

    protected async Task<(List<KeyValuePair<string, string>>? subscriptionOptions, bool fetchSucceeded)> TryGetSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string>>? subscriptionOptions = null;
        var fetchSucceeded = false;

        try
        {
            var credential = _tokenCredentialProvider.TokenCredential;
            var armClient = _armClientProvider.GetArmClient(credential);
            var availableSubscriptions = await armClient.GetAvailableSubscriptionsAsync(tenantId, cancellationToken).ConfigureAwait(false);
            var subscriptionList = availableSubscriptions.ToList();

            if (subscriptionList.Count > 0)
            {
                subscriptionOptions = [.. subscriptionList
                                .Select(sub => KeyValuePair.Create(sub.Id.SubscriptionId ?? "", $"{sub.DisplayName ?? sub.Id.SubscriptionId} ({sub.Id.SubscriptionId})"))
                                .OrderBy(kvp => kvp.Value)];
                fetchSucceeded = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate available subscriptions. Falling back to manual input.");
        }

        return (subscriptionOptions, fetchSucceeded);
    }

    protected async Task<(List<KeyValuePair<string, string>>? subscriptionOptions, bool fetchSucceeded)> TryGetSubscriptionsAsync(CancellationToken cancellationToken)
    {
        return await TryGetSubscriptionsAsync(_options.TenantId, cancellationToken).ConfigureAwait(false);
    }

    protected async Task<(List<(string Name, string Location)>? resourceGroupOptions, bool fetchSucceeded)> TryGetResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        List<(string Name, string Location)>? resourceGroupOptions = null;

        // SubscriptionId is always a GUID. Check if we have a valid GUID before trying to use it.
        if (Guid.TryParse(subscriptionId, out _))
        {
            try
            {
                var credential = _tokenCredentialProvider.TokenCredential;
                var armClient = _armClientProvider.GetArmClient(credential);
                var availableResourceGroups = await armClient.GetAvailableResourceGroupsWithLocationAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
                var resourceGroupList = availableResourceGroups.ToList();

                if (resourceGroupList.Count > 0)
                {
                    resourceGroupOptions = resourceGroupList;
                    return (resourceGroupOptions, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate available resource groups with locations.");
            }
        }
        else
        {
            _logger.LogDebug("SubscriptionId '{SubscriptionId}' isn't a valid GUID. Skipping getting available resource groups from client.", subscriptionId);
        }

        return (resourceGroupOptions, false);
    }

    protected async Task<(List<KeyValuePair<string, string>> locationOptions, bool fetchSucceeded)> TryGetLocationsAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string>>? locationOptions = null;

        // SubscriptionId is always a GUID. Check if we have a valid GUID before trying to use it.
        // Fallback to static list of Azure locations if the subscriptionId is not valid or there is an error.
        if (Guid.TryParse(subscriptionId, out _))
        {
            try
            {
                var credential = _tokenCredentialProvider.TokenCredential;
                var armClient = _armClientProvider.GetArmClient(credential);
                var availableLocations = await armClient.GetAvailableLocationsAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
                var locationList = availableLocations.ToList();

                if (locationList.Count > 0)
                {
                    locationOptions = locationList
                        .Select(loc => KeyValuePair.Create(loc.Name, loc.DisplayName))
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate available locations. Falling back to manual input.");
            }
        }
        else
        {
            _logger.LogDebug("SubscriptionId '{SubscriptionId}' isn't a valid GUID. Skipping getting available locations from client.", subscriptionId);
        }

        return locationOptions is not null
            ? (locationOptions, true)
            : (GetStaticAzureLocations(), false);
    }

    /// <summary>
    /// Gets static Azure locations as fallback when dynamic loading fails.
    /// </summary>
    private static List<KeyValuePair<string, string>> GetStaticAzureLocations()
    {
        return typeof(AzureLocation).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(AzureLocation))
            .Select(p => (AzureLocation)p.GetValue(null)!)
            .Select(location => KeyValuePair.Create(location.Name, location.DisplayName ?? location.Name))
            .OrderBy(kvp => kvp.Value)
            .ToList();
    }
}
