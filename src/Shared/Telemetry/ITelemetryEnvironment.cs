// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Runtime.Versioning;

namespace Aspire.Shared.Telemetry;

/// <summary>
/// Provides environment and platform queries for shared telemetry detection.
/// </summary>
public interface ITelemetryEnvironment
{
    /// <summary>
    /// Gets an environment variable.
    /// </summary>
    /// <param name="variable">The variable name.</param>
    /// <returns>The value, or null when unset.</returns>
    string? GetEnvironmentVariable(string variable);

    /// <summary>
    /// Gets all environment variables.
    /// </summary>
    /// <returns>The variable names and values.</returns>
    IEnumerable<(string Name, string? Value)> GetEnvironmentVariables();

    /// <summary>
    /// Gets whether the current platform is Windows.
    /// </summary>
    /// <returns>Whether the platform is Windows.</returns>
    [SupportedOSPlatformGuard("windows")]
    bool IsWindows();

    /// <summary>
    /// Gets whether the current platform is Linux.
    /// </summary>
    /// <returns>Whether the platform is Linux.</returns>
    [SupportedOSPlatformGuard("linux")]
    bool IsLinux();

    /// <summary>
    /// Gets whether the current platform is macOS.
    /// </summary>
    /// <returns>Whether the platform is macOS.</returns>
    [SupportedOSPlatformGuard("macos")]
    bool IsMacOS();
}

internal sealed class TelemetryEnvironment : ITelemetryEnvironment
{
    public string? GetEnvironmentVariable(string variable) => Environment.GetEnvironmentVariable(variable);

    public IEnumerable<(string Name, string? Value)> GetEnvironmentVariables()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            yield return ((string)entry.Key, entry.Value?.ToString());
        }
    }

    [SupportedOSPlatformGuard("windows")]
    public bool IsWindows() => OperatingSystem.IsWindows();

    [SupportedOSPlatformGuard("linux")]
    public bool IsLinux() => OperatingSystem.IsLinux();

    [SupportedOSPlatformGuard("macos")]
    public bool IsMacOS() => OperatingSystem.IsMacOS();
}
