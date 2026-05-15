// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Resolves the current user's home directory for acquisition code paths.
/// </summary>
internal static class HomeDirectoryResolver
{
    /// <summary>
    /// Returns the current user's home directory, preferring the primary OS environment variable before falling back to
    /// <see cref="Environment.SpecialFolder.UserProfile"/>.
    /// </summary>
    /// <remarks>
    /// On Windows, <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> can ignore process-level
    /// <c>USERPROFILE</c> overrides. Acquisition discovery and uninstall commands intentionally honor those overrides
    /// so sandboxed tests and spawned processes can redirect the user-owned <c>~/.aspire</c> tree consistently.
    /// </remarks>
    internal static string GetUserHomeDirectory()
    {
        var primaryEnvironmentVariable = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
        var home = Environment.GetEnvironmentVariable(primaryEnvironmentVariable);
        if (!string.IsNullOrEmpty(home))
        {
            return home;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    internal static string GetUserHomeOrThrow()
    {
        var home = GetUserHomeDirectory();
        if (string.IsNullOrEmpty(home))
        {
            throw new InvalidOperationException("Could not resolve the current user's home directory.");
        }

        return home;
    }
}
