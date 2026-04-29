// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREBROWSERLOGS001 // Type is for evaluation purposes only

using System.Globalization;
using Aspire.Hosting.Resources;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

/// <summary>
/// Resolved browser configuration used for one tracked browser session.
/// </summary>
/// <remarks>
/// Resolution keeps "which browser/profile did the caller ask for?" separate from "which user data directory
/// does that imply?". The later user-data-directory decision belongs to <see cref="BrowserHostRegistry"/>, where
/// the resolved browser executable path is available.
/// </remarks>
internal readonly record struct BrowserConfiguration(
    string Browser,
    string? Profile,
    BrowserUserDataMode UserDataMode,
    string? AppHostKey)
{
    /// <summary>
    /// The default mode points at an Aspire-managed persistent user data directory shared across every Aspire
    /// AppHost on the machine, so cookies, sign-ins, and extensions persist between runs.
    /// </summary>
    internal const BrowserUserDataMode DefaultUserDataMode = BrowserUserDataMode.Shared;

    /// <summary>
    /// Resolves explicit method arguments, resource-scoped configuration, global configuration, and defaults.
    /// </summary>
    internal static BrowserConfiguration Resolve(
        IConfiguration configuration,
        string resourceName,
        BrowserConfigurationExplicitValues explicitValues,
        BrowserConfiguration? resourceRuntimeConfiguration = null,
        BrowserConfiguration? globalRuntimeConfiguration = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var browserLogsSection = configuration.GetSection(BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName);
        var resourceSection = browserLogsSection.GetSection(resourceName);

        // Resolution order is explicit argument -> resource-specific config -> global browser-log config -> default.
        // Resolve user-data mode before browser so the browser default can prefer Edge for shared state and Chrome for
        // disposable isolated state.
        var resolvedProfile = ResolveProfile(explicitValues, resourceRuntimeConfiguration, globalRuntimeConfiguration, resourceSection, browserLogsSection);
        var resolvedUserDataMode = ResolveUserDataMode(explicitValues, resourceRuntimeConfiguration, globalRuntimeConfiguration, resourceSection, browserLogsSection);
        var resolvedBrowser = ResolveBrowser(explicitValues, resourceRuntimeConfiguration, globalRuntimeConfiguration, resourceSection, browserLogsSection, resolvedUserDataMode);

        if (string.IsNullOrWhiteSpace(resolvedBrowser))
        {
            throw new InvalidOperationException(MessageStrings.BrowserLogsEmptyBrowserConfiguration);
        }

        if (resolvedProfile is not null && string.IsNullOrWhiteSpace(resolvedProfile))
        {
            throw new InvalidOperationException(MessageStrings.BrowserLogsEmptyProfileConfiguration);
        }

        if (resolvedUserDataMode == BrowserUserDataMode.Isolated && resolvedProfile is not null)
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.CurrentCulture,
                    MessageStrings.BrowserLogsProfileRequiresSharedUserDataMode,
                    BrowserLogsBuilderExtensions.ProfileConfigurationKey,
                    resolvedProfile,
                    BrowserLogsBuilderExtensions.UserDataModeConfigurationKey,
                    BrowserUserDataMode.Isolated,
                    BrowserUserDataMode.Shared));
        }

        // Stable per-AppHost key sourced from DistributedApplicationBuilder. Only Isolated mode actually needs it
        // (its user-data path includes the AppHost segment), but it is always captured here so the registry never
        // has to re-read configuration. The same SHA value is used for other per-AppHost persisted state.
        var appHostKey = configuration["AppHost:PathSha256"];

        return new BrowserConfiguration(resolvedBrowser, resolvedProfile, resolvedUserDataMode, appHostKey);
    }

    /// <summary>
    /// Selects the default browser for the default user data mode.
    /// </summary>
    internal static string GetDefaultBrowser(Func<string, string?> resolveBrowserExecutable) =>
        GetDefaultBrowser(DefaultUserDataMode, resolveBrowserExecutable);

    /// <summary>
    /// Selects the default browser for the effective user data mode.
    /// </summary>
    internal static string GetDefaultBrowser(BrowserUserDataMode userDataMode, Func<string, string?> resolveBrowserExecutable)
    {
        if (userDataMode == BrowserUserDataMode.Shared &&
            resolveBrowserExecutable("msedge") is not null)
        {
            return "msedge";
        }

        if (resolveBrowserExecutable("chrome") is not null)
        {
            return "chrome";
        }

        if (resolveBrowserExecutable("msedge") is not null)
        {
            return "msedge";
        }

        return "chrome";
    }

    private static ConfigurationValue<BrowserUserDataMode> ParseUserDataMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ConfigurationValue<BrowserUserDataMode>.Missing;
        }

        if (Enum.TryParse<BrowserUserDataMode>(value, ignoreCase: true, out var parsed))
        {
            return ConfigurationValue<BrowserUserDataMode>.Present(parsed);
        }

        throw new InvalidOperationException(
            string.Format(
                CultureInfo.CurrentCulture,
                MessageStrings.BrowserLogsInvalidUserDataModeConfiguration,
                value,
                BrowserLogsBuilderExtensions.UserDataModeConfigurationKey,
                BrowserUserDataMode.Shared,
                BrowserUserDataMode.Isolated));
    }

    private static string GetDefaultBrowser(BrowserUserDataMode userDataMode) =>
        GetDefaultBrowser(userDataMode, ChromiumBrowserResolver.TryResolveExecutable);

    private static string? ResolveProfile(
        BrowserConfigurationExplicitValues explicitValues,
        BrowserConfiguration? resourceRuntimeConfiguration,
        BrowserConfiguration? globalRuntimeConfiguration,
        IConfigurationSection resourceSection,
        IConfigurationSection browserLogsSection)
        => ResolveValue(
            FromOptionalString(explicitValues.Profile),
            resourceRuntimeConfiguration,
            globalRuntimeConfiguration,
            static configuration => configuration.Profile,
            resourceSection,
            browserLogsSection,
            BrowserLogsBuilderExtensions.ProfileConfigurationKey,
            FromOptionalString,
            static () => null);

    private static BrowserUserDataMode ResolveUserDataMode(
        BrowserConfigurationExplicitValues explicitValues,
        BrowserConfiguration? resourceRuntimeConfiguration,
        BrowserConfiguration? globalRuntimeConfiguration,
        IConfigurationSection resourceSection,
        IConfigurationSection browserLogsSection)
        => ResolveValue(
            explicitValues.UserDataMode is { } explicitUserDataMode
                ? ConfigurationValue<BrowserUserDataMode>.Present(explicitUserDataMode)
                : ConfigurationValue<BrowserUserDataMode>.Missing,
            resourceRuntimeConfiguration,
            globalRuntimeConfiguration,
            static configuration => configuration.UserDataMode,
            resourceSection,
            browserLogsSection,
            BrowserLogsBuilderExtensions.UserDataModeConfigurationKey,
            ParseUserDataMode,
            static () => DefaultUserDataMode);

    private static string ResolveBrowser(
        BrowserConfigurationExplicitValues explicitValues,
        BrowserConfiguration? resourceRuntimeConfiguration,
        BrowserConfiguration? globalRuntimeConfiguration,
        IConfigurationSection resourceSection,
        IConfigurationSection browserLogsSection,
        BrowserUserDataMode resolvedUserDataMode)
        => ResolveValue<string?>(
            FromOptionalString(explicitValues.Browser),
            resourceRuntimeConfiguration,
            globalRuntimeConfiguration,
            static configuration => configuration.Browser,
            resourceSection,
            browserLogsSection,
            BrowserLogsBuilderExtensions.BrowserConfigurationKey,
            FromOptionalString,
            () => GetDefaultBrowser(resolvedUserDataMode))!;

    private static T ResolveValue<T>(
        ConfigurationValue<T> explicitValue,
        BrowserConfiguration? resourceRuntimeConfiguration,
        BrowserConfiguration? globalRuntimeConfiguration,
        Func<BrowserConfiguration, T> getRuntimeValue,
        IConfigurationSection resourceSection,
        IConfigurationSection browserLogsSection,
        string configurationKey,
        Func<string?, ConfigurationValue<T>> parseConfigurationValue,
        Func<T> getDefault)
    {
        if (explicitValue.HasValue)
        {
            return explicitValue.Value;
        }

        if (resourceRuntimeConfiguration is { } resourceRuntime)
        {
            return getRuntimeValue(resourceRuntime);
        }

        var resourceConfiguration = parseConfigurationValue(resourceSection[configurationKey]);
        if (resourceConfiguration.HasValue)
        {
            return resourceConfiguration.Value;
        }

        if (globalRuntimeConfiguration is { } globalRuntime)
        {
            return getRuntimeValue(globalRuntime);
        }

        var globalConfiguration = parseConfigurationValue(browserLogsSection[configurationKey]);
        return globalConfiguration.HasValue
            ? globalConfiguration.Value
            : getDefault();
    }

    private static ConfigurationValue<string?> FromOptionalString(string? value) =>
        value is null ? ConfigurationValue<string?>.Missing : ConfigurationValue<string?>.Present(value);

    private readonly record struct ConfigurationValue<T>(bool HasValue, T Value)
    {
        public static ConfigurationValue<T> Missing => new(false, default!);

        public static ConfigurationValue<T> Present(T value) => new(true, value);
    }
}

/// <summary>
/// Browser configuration values explicitly supplied by the resource builder.
/// </summary>
internal readonly record struct BrowserConfigurationExplicitValues(
    string? Browser = null,
    string? Profile = null,
    BrowserUserDataMode? UserDataMode = null);
