// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
internal readonly record struct BrowserConfiguration(string Browser, string? Profile, BrowserUserDataMode UserDataMode, string? AppHostKey)
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
        BrowserConfigurationOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        var browserLogsSection = configuration.GetSection(BrowserLogsBuilderExtensions.BrowserLogsConfigurationSectionName);
        var resourceSection = browserLogsSection.GetSection(resourceName);

        // Resolution order is explicit argument -> resource-specific config -> global browser-log config -> default.
        // Resolve user-data mode before browser so the browser default can prefer Edge for shared state and Chrome for
        // disposable isolated state.
        var resolvedProfile = overrides.Profile
            ?? resourceSection[BrowserLogsBuilderExtensions.ProfileConfigurationKey]
            ?? browserLogsSection[BrowserLogsBuilderExtensions.ProfileConfigurationKey];
        var resolvedUserDataMode = overrides.UserDataMode
            ?? ParseUserDataMode(resourceSection[BrowserLogsBuilderExtensions.UserDataModeConfigurationKey])
            ?? ParseUserDataMode(browserLogsSection[BrowserLogsBuilderExtensions.UserDataModeConfigurationKey])
            ?? DefaultUserDataMode;
        var resolvedBrowser = overrides.Browser
            ?? resourceSection[BrowserLogsBuilderExtensions.BrowserConfigurationKey]
            ?? browserLogsSection[BrowserLogsBuilderExtensions.BrowserConfigurationKey]
            ?? GetDefaultBrowser(resolvedUserDataMode);

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
        // has to re-read configuration. Same SHA value is used by FileDeploymentStateManager for analogous
        // per-AppHost persistent state.
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

    private static BrowserUserDataMode? ParseUserDataMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<BrowserUserDataMode>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
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
}

/// <summary>
/// Explicit browser configuration values supplied by the resource builder.
/// </summary>
internal readonly record struct BrowserConfigurationOverrides(string? Browser, string? Profile, BrowserUserDataMode? UserDataMode);
