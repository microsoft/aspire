// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only
#pragma warning disable ASPIREUSERSECRETS001 // Type is for evaluation purposes only
#pragma warning disable ASPIREBROWSERAUTOMATION001 // Type is for evaluation purposes only

using System.Globalization;
using Aspire.Hosting.Browsers.Resources;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal sealed class BrowserConfigurationManager(
    IConfiguration configuration,
    IUserSecretsManager userSecretsManager,
    DistributedApplicationModel applicationModel,
    BrowserConfigurationStore configurationStore,
    ResourceNotificationService resourceNotificationService,
    ILogger<BrowserConfigurationManager> logger)
{
    internal const string ScopeInputName = "scope";
    internal const string BrowserInputName = "browser";
    internal const string UserDataModeInputName = "userDataMode";
    internal const string ProfileInputName = "profile";
    internal const string SaveToUserSecretsInputName = "saveToUserSecrets";
    internal const string ResourceScopeValue = "resource";
    internal const string GlobalScopeValue = "global";
    // Choice inputs store a string value, so the "default profile" option needs a sentinel that cannot be confused with
    // Chromium's real "Default" profile directory. The sentinel maps back to null before configuration is saved.
    internal const string BrowserDefaultProfileValue = "__aspire_browser_default__";

    /// <summary>
    /// Creates the static argument definitions for the configure-tracked-browser command.
    /// Called at command registration time (before services are available).
    /// </summary>
    internal static IReadOnlyList<InteractionInput> CreateArgumentDefinitions(BrowserResource resource, bool userSecretsAvailable)
    {
        var parentResourceName = resource.ParentResource.Name;
        var browserOptions = new List<KeyValuePair<string, string>>();
        AddKnownBrowser("msedge", BrowserCommandStrings.ConfigureTrackedBrowserEdgeOption);
        AddKnownBrowser("chrome", BrowserCommandStrings.ConfigureTrackedBrowserChromeOption);
        AddKnownBrowser("chromium", BrowserCommandStrings.ConfigureTrackedBrowserChromiumOption);

        var scopeInput = new InteractionInput
        {
            Name = ScopeInputName,
            Label = BrowserCommandStrings.ConfigureTrackedBrowserScopeLabel,
            InputType = InputType.Choice,
            Required = true,
            Value = ResourceScopeValue,
            Options =
            [
                new(ResourceScopeValue, string.Format(CultureInfo.CurrentCulture, BrowserCommandStrings.ConfigureTrackedBrowserResourceScopeOption, parentResourceName)),
                new(GlobalScopeValue, BrowserCommandStrings.ConfigureTrackedBrowserGlobalScopeOption)
            ]
        };

        var browserInput = new InteractionInput
        {
            Name = BrowserInputName,
            Label = BrowserCommandStrings.ConfigureTrackedBrowserBrowserLabel,
            Description = BrowserCommandStrings.ConfigureTrackedBrowserBrowserDescription,
            InputType = InputType.Choice,
            Required = true,
            AllowCustomChoice = true,
            Options = browserOptions,
            DynamicLoading = new InputLoadOptions
            {
                AlwaysLoadOnStart = true,
                LoadCallback = context =>
                {
                    var configurationManager = context.Services.GetRequiredService<BrowserConfigurationManager>();
                    configurationManager.LoadBrowserValue(resource, context);
                    return Task.CompletedTask;
                }
            }
        };

        var userDataModeInput = new InteractionInput
        {
            Name = UserDataModeInputName,
            Label = BrowserCommandStrings.ConfigureTrackedBrowserUserDataModeLabel,
            InputType = InputType.Choice,
            Required = true,
            Options =
            [
                new(nameof(BrowserUserDataMode.Shared), nameof(BrowserUserDataMode.Shared)),
                new(nameof(BrowserUserDataMode.Isolated), nameof(BrowserUserDataMode.Isolated))
            ],
            DynamicLoading = new InputLoadOptions
            {
                AlwaysLoadOnStart = true,
                LoadCallback = context =>
                {
                    var configurationManager = context.Services.GetRequiredService<BrowserConfigurationManager>();
                    configurationManager.LoadUserDataModeOptions(resource, context);
                    return Task.CompletedTask;
                }
            }
        };

        var profileInput = new InteractionInput
        {
            Name = ProfileInputName,
            Label = BrowserCommandStrings.ConfigureTrackedBrowserProfileLabel,
            Description = BrowserCommandStrings.ConfigureTrackedBrowserProfileDescription,
            InputType = InputType.Choice,
            Required = false,
            AllowCustomChoice = true,
            DynamicLoading = new InputLoadOptions
            {
                AlwaysLoadOnStart = true,
                DependsOnInputs = [BrowserInputName, UserDataModeInputName],
                LoadCallback = context =>
                {
                    var configurationManager = context.Services.GetRequiredService<BrowserConfigurationManager>();
                    configurationManager.LoadProfileOptions(resource, context);
                    return Task.CompletedTask;
                }
            }
        };

        var saveInput = new InteractionInput
        {
            Name = SaveToUserSecretsInputName,
            Label = BrowserCommandStrings.ConfigureTrackedBrowserSaveToUserSecretsLabel,
            Description = userSecretsAvailable
                ? BrowserCommandStrings.ConfigureTrackedBrowserSaveToUserSecretsDescriptionConfigured
                : BrowserCommandStrings.ConfigureTrackedBrowserSaveToUserSecretsDescriptionNotConfigured,
            EnableDescriptionMarkdown = true,
            InputType = InputType.Boolean,
            Value = userSecretsAvailable ? "true" : null,
            Disabled = !userSecretsAvailable
        };

        return [scopeInput, browserInput, userDataModeInput, profileInput, saveInput];

        void AddKnownBrowser(string browser, string displayName)
        {
            if (ChromiumBrowserResolver.TryResolveExecutable(browser) is not null)
            {
                browserOptions.Add(new(browser, displayName));
            }
        }
    }

    public async Task<ExecuteCommandResult> ConfigureAsync(BrowserResource resource, InteractionInputCollection arguments, CancellationToken _)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var selected = BrowserConfigurationSelection.FromInputs(arguments);
        var resolvedConfigurations = ResolveEffectiveConfigurations(resource, selected);
        Apply(resource, selected);

        foreach (var (browserResource, browserConfiguration) in resolvedConfigurations)
        {
            await PublishConfigurationSnapshotAsync(browserResource, browserConfiguration).ConfigureAwait(false);
        }

        var scopeName = selected.Scope == BrowserConfigurationScope.Resource
            ? resource.ParentResource.Name
            : BrowserCommandStrings.ConfigureTrackedBrowserGlobalScopeResult;
        var resultMessage = selected.SaveToUserSecrets
            ? BrowserCommandStrings.ConfigureTrackedBrowserSaved
            : BrowserCommandStrings.ConfigureTrackedBrowserApplied;

        return new ExecuteCommandResult
        {
            Success = true,
            Message = string.Format(
                CultureInfo.CurrentCulture,
                resultMessage,
                scopeName)
        };
    }

    private void LoadBrowserValue(BrowserResource resource, LoadInputContext context)
    {
        if (context.Input.Value is null)
        {
            var currentConfiguration = resource.ResolveCurrentConfiguration(configuration, configurationStore);
            context.Input.Value = currentConfiguration.Browser;
        }
    }

    private void LoadUserDataModeOptions(BrowserResource resource, LoadInputContext context)
    {
        if (context.Input.Value is null)
        {
            var currentConfiguration = resource.ResolveCurrentConfiguration(configuration, configurationStore);
            context.Input.Value = currentConfiguration.UserDataMode.ToString();
        }
    }

    private void LoadProfileOptions(BrowserResource resource, LoadInputContext context)
    {
        var currentConfiguration = resource.ResolveCurrentConfiguration(configuration, configurationStore);
        if (context.Input.Value is null)
        {
            context.Input.Value = currentConfiguration.Profile ?? BrowserDefaultProfileValue;
        }

        var browser = context.AllInputs[BrowserInputName].Value ?? currentConfiguration.Browser;
        var userDataModeValue = context.AllInputs[UserDataModeInputName].Value ?? currentConfiguration.UserDataMode.ToString();
        var profile = context.Input.Value;

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BrowserDefaultProfileValue] = BrowserCommandStrings.ConfigureTrackedBrowserDefaultProfileOption
        };

        var disableProfileInput = true;
        if (Enum.TryParse<BrowserUserDataMode>(userDataModeValue, ignoreCase: true, out var userDataMode) &&
            userDataMode == BrowserUserDataMode.Shared &&
            !string.IsNullOrWhiteSpace(browser))
        {
            disableProfileInput = false;

            try
            {
                // Profile discovery only makes sense for Shared mode. The Shared directory is a persistent
                // Aspire-managed Chromium user data directory with profile subdirectories such as "Default" and
                // "Profile 1". Isolated mode is per-AppHost and may not exist until the first browser launch, so
                // offering profile choices there would be misleading.
                var browserConfiguration = new BrowserConfiguration(
                    browser,
                    Profile: null,
                    BrowserUserDataMode.Shared,
                    configuration["AppHost:PathSha256"]);
                var userDataDirectory = BrowserUserDataPathResolver.Resolve(browserConfiguration, createDirectory: false);
                foreach (var browserProfile in ChromiumBrowserResolver.GetAvailableProfiles(userDataDirectory))
                {
                    options[browserProfile.DirectoryName] = FormatProfileOption(browserProfile);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                logger.LogDebug(ex, "Unable to discover tracked browser profiles for '{Browser}'.", browser);
            }
        }
        else
        {
            context.Input.Value = BrowserDefaultProfileValue;
        }

        if (!string.IsNullOrWhiteSpace(profile) && !options.ContainsKey(profile))
        {
            options[profile] = profile;
        }

        context.Input.Options = [.. options.Select(static pair => new KeyValuePair<string, string>(pair.Key, pair.Value))];
        context.Input.Disabled = disableProfileInput;
    }

    private static string FormatProfileOption(ChromiumBrowserProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.DisplayName) &&
            !string.Equals(profile.DirectoryName, profile.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                BrowserCommandStrings.ConfigureTrackedBrowserProfileOptionWithDisplayName,
                profile.DirectoryName,
                profile.DisplayName);
        }

        return profile.DirectoryName;
    }

    internal Task ValidateInputsAsync(BrowserResource resource, InputsDialogValidationContext context)
    {
        var inputs = context.Inputs;
        var browser = inputs[BrowserInputName];
        var hasValidationErrors = false;
        if (string.IsNullOrWhiteSpace(browser.Value))
        {
            context.AddValidationError(browser, BrowserCommandStrings.ConfigureTrackedBrowserBrowserRequired);
            hasValidationErrors = true;
        }

        var userDataMode = inputs[UserDataModeInputName];
        if (!Enum.TryParse<BrowserUserDataMode>(userDataMode.Value, ignoreCase: true, out var parsedUserDataMode))
        {
            context.AddValidationError(userDataMode, BrowserCommandStrings.ConfigureTrackedBrowserUserDataModeRequired);
            hasValidationErrors = true;
        }

        var profile = inputs[ProfileInputName];
        if (parsedUserDataMode == BrowserUserDataMode.Isolated &&
            !string.IsNullOrWhiteSpace(profile.Value) &&
            !string.Equals(profile.Value, BrowserDefaultProfileValue, StringComparison.Ordinal))
        {
            context.AddValidationError(profile, BrowserCommandStrings.ConfigureTrackedBrowserProfileRequiresShared);
            hasValidationErrors = true;
        }

        var saveToUserSecrets = inputs[SaveToUserSecretsInputName];
        if (IsSaveToUserSecretsRequested(inputs) && !userSecretsManager.IsAvailable)
        {
            context.AddValidationError(saveToUserSecrets, BrowserCommandStrings.ConfigureTrackedBrowserUserSecretsUnavailable);
            hasValidationErrors = true;
        }

        if (!hasValidationErrors)
        {
            try
            {
                // Resolve the final effective configuration so explicit WithBrowserAutomation values are validated before
                // applying runtime settings or mutating user secrets.
                _ = ResolveEffectiveConfigurations(resource, BrowserConfigurationSelection.FromInputs(inputs));
            }
            catch (InvalidOperationException ex)
            {
                context.AddValidationError(userDataMode, ex.Message);
            }
        }

        return Task.CompletedTask;
    }

    private List<(BrowserResource Resource, BrowserConfiguration Configuration)> ResolveEffectiveConfigurations(
        BrowserResource commandResource,
        BrowserConfigurationSelection selected)
    {
        var selectedConfiguration = ToBrowserConfiguration(selected);
        IEnumerable<BrowserResource> resources = selected.Scope == BrowserConfigurationScope.Global
            ? applicationModel.Resources.OfType<BrowserResource>()
            : [commandResource];

        return [.. resources.Select(resource =>
            (resource, ResolveEffectiveConfiguration(resource, commandResource, selected, selectedConfiguration)))];
    }

    private BrowserConfiguration ResolveEffectiveConfiguration(
        BrowserResource resource,
        BrowserResource commandResource,
        BrowserConfigurationSelection selected,
        BrowserConfiguration selectedConfiguration)
    {
        var (resourceConfiguration, globalConfiguration) = configurationStore.GetConfigurations(resource.ParentResource.Name);
        if (selected.Scope == BrowserConfigurationScope.Global)
        {
            globalConfiguration = selectedConfiguration;
        }
        else if (ReferenceEquals(resource, commandResource))
        {
            resourceConfiguration = selectedConfiguration;
        }

        return BrowserConfiguration.Resolve(
            configuration,
            resource.ParentResource.Name,
            resource.ExplicitConfigurationValues,
            resourceConfiguration,
            globalConfiguration);
    }

    private BrowserConfiguration ToBrowserConfiguration(BrowserConfigurationSelection selected)
    {
        return new BrowserConfiguration(
            selected.Browser,
            selected.Profile,
            selected.UserDataMode,
            configuration["AppHost:PathSha256"]);
    }

    private void Apply(BrowserResource resource, BrowserConfigurationSelection selected)
    {
        var configurationPrefix = selected.Scope == BrowserConfigurationScope.Resource
            ? $"{BrowserAutomationBuilderExtensions.BrowserConfigurationSectionName}:{resource.ParentResource.Name}"
            : BrowserAutomationBuilderExtensions.BrowserConfigurationSectionName;

        if (selected.SaveToUserSecrets)
        {
            // IUserSecretsManager persists one key at a time, so a later failure can leave earlier secret mutations
            // on disk. Only update the runtime store after every requested mutation succeeds, so the current AppHost
            // never observes a partial save.
            SaveValue($"{configurationPrefix}:{BrowserAutomationBuilderExtensions.BrowserConfigurationKey}", selected.Browser);
            SaveValue($"{configurationPrefix}:{BrowserAutomationBuilderExtensions.UserDataModeConfigurationKey}", selected.UserDataMode.ToString());

            var profileKey = $"{configurationPrefix}:{BrowserAutomationBuilderExtensions.ProfileConfigurationKey}";
            if (selected.Profile is { } profile)
            {
                SaveValue(profileKey, profile);
            }
            else
            {
                DeleteValue(profileKey);
            }
        }

        configurationStore.Set(selected.Scope, resource.ParentResource.Name, ToBrowserConfiguration(selected));
    }

    private void SaveValue(string key, string value)
    {
        if (!userSecretsManager.TrySetSecret(key, value))
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.CurrentCulture,
                    BrowserCommandStrings.ConfigureTrackedBrowserSaveFailed,
                    key));
        }
    }

    private void DeleteValue(string key)
    {
        if (!userSecretsManager.TryDeleteSecret(key))
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.CurrentCulture,
                    BrowserCommandStrings.ConfigureTrackedBrowserSaveFailed,
                    key));
        }
    }

    private Task PublishConfigurationSnapshotAsync(BrowserResource resource, BrowserConfiguration browserConfiguration)
    {
        return resourceNotificationService.PublishUpdateAsync(resource, snapshot => snapshot with
        {
            Properties = SetConfigurationProperties(snapshot.Properties, browserConfiguration)
        });
    }

    private static System.Collections.Immutable.ImmutableArray<ResourcePropertySnapshot> SetConfigurationProperties(
        System.Collections.Immutable.ImmutableArray<ResourcePropertySnapshot> properties,
        BrowserConfiguration browserConfiguration)
    {
        properties = properties
            .SetResourceProperty(BrowserAutomationBuilderExtensions.BrowserPropertyName, browserConfiguration.Browser)
            .SetResourceProperty(BrowserAutomationBuilderExtensions.UserDataModePropertyName, browserConfiguration.UserDataMode.ToString());

        return browserConfiguration.Profile is { } profile
            ? properties.SetResourceProperty(BrowserAutomationBuilderExtensions.ProfilePropertyName, profile)
            : RemoveProperty(properties, BrowserAutomationBuilderExtensions.ProfilePropertyName);
    }

    private static System.Collections.Immutable.ImmutableArray<ResourcePropertySnapshot> RemoveProperty(
        System.Collections.Immutable.ImmutableArray<ResourcePropertySnapshot> properties,
        string name)
    {
        for (var i = 0; i < properties.Length; i++)
        {
            if (string.Equals(properties[i].Name, name, StringComparisons.ResourcePropertyName))
            {
                return properties.RemoveAt(i);
            }
        }

        return properties;
    }

    private readonly record struct BrowserConfigurationSelection(
        BrowserConfigurationScope Scope,
        string Browser,
        BrowserUserDataMode UserDataMode,
        string? Profile,
        bool SaveToUserSecrets)
    {
        public static BrowserConfigurationSelection FromInputs(InteractionInputCollection inputs)
        {
            var scope = string.Equals(inputs[ScopeInputName].Value, GlobalScopeValue, StringComparison.Ordinal)
                ? BrowserConfigurationScope.Global
                : BrowserConfigurationScope.Resource;
            var browser = inputs[BrowserInputName].Value ?? string.Empty;
            var userDataMode = Enum.Parse<BrowserUserDataMode>(inputs[UserDataModeInputName].Value!, ignoreCase: true);
            var profileValue = inputs[ProfileInputName].Value;
            var profile = string.IsNullOrWhiteSpace(profileValue) ||
                string.Equals(profileValue, BrowserDefaultProfileValue, StringComparison.Ordinal)
                    ? null
                    : profileValue;
            var saveToUserSecrets = IsSaveToUserSecretsRequested(inputs);

            return new BrowserConfigurationSelection(scope, browser, userDataMode, profile, saveToUserSecrets);
        }
    }

    private static bool IsSaveToUserSecretsRequested(InteractionInputCollection inputs)
    {
        return inputs[SaveToUserSecretsInputName].Value is { Length: > 0 } saveValue &&
            bool.TryParse(saveValue, out var saveToUserSecrets) &&
            saveToUserSecrets;
    }
}

#pragma warning restore ASPIREUSERSECRETS001
#pragma warning restore ASPIREINTERACTION001
