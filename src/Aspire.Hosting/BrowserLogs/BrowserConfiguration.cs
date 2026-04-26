// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

/// <summary>
/// Selects the Chromium user data directory used by tracked browser sessions.
/// </summary>
public enum BrowserUserDataMode
{
    /// <summary>
    /// Use the browser's real user data directory so the tracked session behaves like a persistent browser context
    /// with real cookies, sessions, extensions, and profile selection.
    /// </summary>
    /// <remarks>
    /// NOTE: Aspire can adopt a shared browser only when it previously launched that browser with remote debugging
    /// enabled. If a normal non-debuggable browser is already using the selected user data directory, the tracked
    /// session fails with guidance instead of opening a second browser against the same profile store. Google Chrome
    /// also blocks remote debugging against its default user data directory; use Microsoft Edge or <see cref="Isolated"/>
    /// mode when Chrome is selected.
    /// </remarks>
    Shared,

    /// <summary>
    /// Launch the tracked browser against a temporary user data directory, like a disposable persistent browser
    /// context, so the session starts from clean state and does not affect the user's normal browser profiles.
    /// </summary>
    Isolated,
}

/// <summary>
/// Resolved browser configuration used for one tracked browser session.
/// </summary>
/// <remarks>
/// Resolution keeps "which browser/profile did the caller ask for?" separate from "which user data directory
/// does that imply?". The later user-data-directory decision belongs to <see cref="BrowserHostRegistry"/>, where
/// the resolved browser executable path is available.
/// </remarks>
internal readonly record struct BrowserConfiguration(string Browser, string? Profile, BrowserUserDataMode UserDataMode)
{
    /// <summary>
    /// The default mode matches a normal browser launch by using the browser's real user data directory.
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
            throw new InvalidOperationException("Tracked browser configuration resolved an empty browser value.");
        }

        if (resolvedProfile is not null && string.IsNullOrWhiteSpace(resolvedProfile))
        {
            throw new InvalidOperationException("Tracked browser configuration resolved an empty profile value.");
        }

        if (resolvedUserDataMode == BrowserUserDataMode.Isolated && resolvedProfile is not null)
        {
            throw new InvalidOperationException(
                $"Tracked browser configuration set '{BrowserLogsBuilderExtensions.ProfileConfigurationKey}' to '{resolvedProfile}' while '{BrowserLogsBuilderExtensions.UserDataModeConfigurationKey}' is '{BrowserUserDataMode.Isolated}'. " +
                $"Profiles can only be selected when '{BrowserLogsBuilderExtensions.UserDataModeConfigurationKey}' is '{BrowserUserDataMode.Shared}'.");
        }

        return new BrowserConfiguration(resolvedBrowser, resolvedProfile, resolvedUserDataMode);
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
            $"Tracked browser configuration value '{value}' is not a valid '{BrowserLogsBuilderExtensions.UserDataModeConfigurationKey}'. Expected '{BrowserUserDataMode.Shared}' or '{BrowserUserDataMode.Isolated}'.");
    }

    private static string GetDefaultBrowser(BrowserUserDataMode userDataMode) =>
        GetDefaultBrowser(userDataMode, ChromiumBrowserResolver.TryResolveExecutable);
}

/// <summary>
/// Explicit browser configuration values supplied by the resource builder.
/// </summary>
internal readonly record struct BrowserConfigurationOverrides(string? Browser, string? Profile, BrowserUserDataMode? UserDataMode);
