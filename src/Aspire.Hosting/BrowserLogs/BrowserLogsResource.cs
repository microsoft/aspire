// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

/// <summary>
/// Selects the Chromium user data directory used by tracked browser sessions.
/// </summary>
public enum BrowserUserDataMode
{
    /// <summary>
    /// Use the browser's real user data directory so the tracked session reuses real cookies, sessions,
    /// extensions, and profile selection. Behaves like clicking the browser icon.
    /// </summary>
    /// <remarks>
    /// NOTE: When the target browser is already running with the same user data directory, Chromium will
    /// typically forward the launch to the existing instance and exit the new process. The tracked session
    /// relies on <c>--remote-debugging-port</c> and the <c>DevToolsActivePort</c> file written by the
    /// launched process; if launching forwards to an existing browser, the DevTools endpoint may not be
    /// discoverable and the session will fail to start. Users must close existing browser windows for the
    /// selected user data directory before starting a tracked session in this mode.
    /// </remarks>
    Shared,

    /// <summary>
    /// Launch the tracked browser against a temporary user data directory so the session starts from clean
    /// state and does not affect the user's normal browser profiles.
    /// </summary>
    Isolated,
}

internal readonly record struct BrowserLogsSettings(string Browser, string? Profile, BrowserUserDataMode UserDataMode);

internal sealed class BrowserLogsResource(
    string name,
    IResourceWithEndpoints parentResource,
    BrowserLogsSettings initialSettings,
    string? browserOverride,
    string? profileOverride,
    BrowserUserDataMode? userDataModeOverride)
    : Resource(name)
{
    public IResourceWithEndpoints ParentResource { get; } = parentResource;

    public string Browser { get; } = initialSettings.Browser;

    public string? Profile { get; } = initialSettings.Profile;

    public BrowserUserDataMode UserDataMode { get; } = initialSettings.UserDataMode;

    public string? BrowserOverride { get; } = browserOverride;

    public string? ProfileOverride { get; } = profileOverride;

    public BrowserUserDataMode? UserDataModeOverride { get; } = userDataModeOverride;

    public BrowserLogsSettings ResolveCurrentSettings(IConfiguration configuration) =>
        BrowserLogsBuilderExtensions.ResolveSettings(configuration, ParentResource.Name, BrowserOverride, ProfileOverride, UserDataModeOverride);
}
