// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;

namespace Aspire.Tray;

/// <summary>
/// Keeps the native Settings windows' user-facing text consistent.
/// </summary>
internal static class TraySettingsText
{
    internal const string Title = "Aspire Tray Settings";
    internal const string PreviewTitle = "Aspire Tray Preview Settings";
    internal const string General = "General";
    internal const string About = "About";
    internal const string StartupOption = "Launch Aspire Tray when I sign in";
    internal const string StableNativeInstallationRequired = "Launch at sign-in requires a stable native CLI installation.";

    internal static string GetAboutText(bool preview)
    {
        var assembly = typeof(TraySettingsText).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "Development";
        var build = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? assembly.GetName().Version?.ToString() ?? "Development";
        return string.Join(Environment.NewLine, "Aspire Tray", $"Version: {version}", $"Build: {build}",
            preview ? "Preview: fake AppHosts and startup settings only." : "An experimental companion for Aspire.");
    }

    internal static string GetStartupStatus(TrayStartupState state, string? error)
        => Join(error,
            error is null ? null : state.Enabled ? "Launch at sign-in is on." : "Launch at sign-in is off.",
            state.CanEnable ? null : StableNativeInstallationRequired);

    internal static string GetReadError(Exception exception, string? writeError)
        => Join(writeError, $"Could not read launch at sign-in: {exception.Message}", "Close and reopen Settings to retry.");

    internal static string GetWriteError(Exception exception)
        => $"Could not change launch at sign-in: {exception.Message}";

    private static string Join(params string?[] messages)
        => string.Join(Environment.NewLine, messages.Where(message => !string.IsNullOrWhiteSpace(message)));
}
